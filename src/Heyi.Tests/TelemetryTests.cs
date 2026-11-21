namespace Heyi.Tests;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus;
using NUnit.Framework;

public class TelemetryTests
{
    TelemetryRecorder recorder = null!;

    [SetUp]
    public void StartRecording() => recorder = new TelemetryRecorder();

    [TearDown]
    public void StopRecording() => recorder.Dispose();

    [Test]
    public void Names_are_stable()
    {
        // Telemetry names are public API: renaming them breaks dashboards and alerts.
        Assert.That(HeyiTelemetry.ActivitySourceName, Is.EqualTo("Heyi"));
        Assert.That(HeyiTelemetry.MeterName, Is.EqualTo("Heyi"));
        Assert.That(Telemetry.SessionActivityName, Is.EqualTo("Heyi.TransactionalSession"));
        Assert.That(Telemetry.AcceptanceCheckActivityName, Is.EqualTo("Heyi.AcceptanceCheck"));
        Assert.That(Telemetry.SessionOutcomeTag, Is.EqualTo("heyi.session.outcome"));
        Assert.That(Telemetry.RollbackReasonTag, Is.EqualTo("heyi.session.rollback_reason"));
        Assert.That(Telemetry.IdempotencyKeyPresentTag, Is.EqualTo("heyi.idempotency_key.present"));
        Assert.That(Telemetry.AcceptanceCheckTypeTag, Is.EqualTo("heyi.acceptance_check.type"));
        Assert.That(Telemetry.AcceptanceCheckOutcomeTag, Is.EqualTo("heyi.acceptance_check.outcome"));
    }

    [Test]
    public async Task Committed_sessions_are_traced_and_measured()
    {
        Activity? endpointActivity = null;
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", async (PlaceOrder request, IOutgoingMessages messages) =>
            {
                endpointActivity = Activity.Current;
                await messages.Publish(new OrderPlaced(request.OrderId));
                return Results.Ok();
            })
            .WithTransactionalSession());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new PlaceOrder(42)) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "\"order-42\"");
        using var response = await app.CreateClient().SendAsync(request);

        var span = recorder.Single("Heyi.TransactionalSession");
        Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Ok));
        Assert.That(span.GetTagItem("heyi.session.outcome"), Is.EqualTo("committed"));
        Assert.That(span.GetTagItem("heyi.session.rollback_reason"), Is.Null);
        Assert.That(span.GetTagItem("heyi.idempotency_key.present"), Is.EqualTo(true));
        // The endpoint runs inside the span, so the messages it sends are traced as its children.
        Assert.That(endpointActivity, Is.SameAs(span));

        var measurement = recorder.Measurements.Single();
        Assert.That(measurement.Instrument, Is.EqualTo("heyi.session.duration"));
        Assert.That(measurement.Tags, Is.EquivalentTo(new Dictionary<string, object?> { ["heyi.session.outcome"] = "committed" }));
    }

    [Test]
    public async Task Unsuccessful_results_are_rollbacks_not_errors()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", () => Results.Conflict()).WithTransactionalSession());

        await app.CreateClient().PostAsync("/orders", null);

        var span = recorder.Single("Heyi.TransactionalSession");
        Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Unset));
        Assert.That(span.GetTagItem("heyi.session.outcome"), Is.EqualTo("rolled_back"));
        Assert.That(span.GetTagItem("heyi.session.rollback_reason"), Is.EqualTo("unsuccessful_result"));
        Assert.That(span.GetTagItem("heyi.idempotency_key.present"), Is.EqualTo(false));
        Assert.That(recorder.Measurements.Single().Tags, Is.EquivalentTo(new Dictionary<string, object?>
        {
            ["heyi.session.outcome"] = "rolled_back",
            ["heyi.session.rollback_reason"] = "unsuccessful_result"
        }));
    }

    [Test]
    public async Task Exceptions_are_rollbacks_and_errors()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", IResult () => throw new InvalidOperationException("Boom")).WithTransactionalSession());

        Assert.That(async () => await app.CreateClient().PostAsync("/orders", null), Throws.InvalidOperationException);

        var span = recorder.Single("Heyi.TransactionalSession");
        Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Error));
        Assert.That(span.StatusDescription, Is.EqualTo("Boom"));
        Assert.That(span.GetTagItem("heyi.session.outcome"), Is.EqualTo("rolled_back"));
        Assert.That(span.GetTagItem("heyi.session.rollback_reason"), Is.EqualTo("exception"));
        Assert.That(span.Events.Single().Name, Is.EqualTo("exception"));
    }

    [Test]
    public async Task Failed_commits_are_errors()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", () => Results.Ok()).WithTransactionalSession());
        app.Session.CommitException = new InvalidOperationException("Commit failed");

        using var response = await app.CreateClient().PostAsync("/orders", null);

        var span = recorder.Single("Heyi.TransactionalSession");
        Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Error));
        Assert.That(span.StatusDescription, Is.EqualTo("Commit failed"));
        Assert.That(span.GetTagItem("heyi.session.outcome"), Is.EqualTo("commit_failed"));
        Assert.That(span.GetTagItem("heyi.session.rollback_reason"), Is.Null);
    }

    [Test]
    public async Task Cancelled_commits_are_neither_rollbacks_nor_errors()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", () => Results.Ok()).WithTransactionalSession());
        app.Session.CommitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cancellationTokenSource = new CancellationTokenSource();
        var request = app.CreateClient().PostAsync("/orders", null, cancellationTokenSource.Token);
        await app.Session.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellationTokenSource.CancelAsync();
        await Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => request);

        // The client gives up before the server is done: wait for the span to end.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (recorder.Activities.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var span = recorder.Single("Heyi.TransactionalSession");
        Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Unset));
        Assert.That(span.GetTagItem("heyi.session.outcome"), Is.EqualTo("cancelled"));
        Assert.That(span.GetTagItem("heyi.session.rollback_reason"), Is.Null);
    }

    [Test]
    public async Task Requests_without_a_session_arent_traced()
    {
        await using var app = await TestApp.Start(endpoints =>
        {
            var orders = endpoints.MapGroup("/orders").WithTransactionalSession();
            orders.MapGet("/", () => Results.Ok());
            orders.MapPost("/webhook", [WithoutTransactionalSession] () => Results.Ok());
        });
        var client = app.CreateClient();

        using var read = await client.GetAsync("/orders/");
        using var webhook = await client.PostAsync("/orders/webhook", null);

        Assert.That(recorder.Activities, Is.Empty);
        Assert.That(recorder.Measurements, Is.Empty);
    }

    [Test]
    public async Task Acceptance_checks_are_traced_and_measured()
    {
        await using var app = await TestApp.Start(
            endpoints => endpoints.MapCommand<AcceptOnlyEndpointTests.PlaceOrder>("/orders")
                .WithAcceptanceCheck<Accepting>()
                .WithAcceptanceCheck<Rejecting>(),
            addHeyi: false,
            addTransactionalSession: false,
            configureServices: services => services.AddSingleton<IMessageSession>(new FakeMessageSession()));

        using var response = await app.CreateClient().PostAsJsonAsync("/orders", new AcceptOnlyEndpointTests.PlaceOrder(Guid.NewGuid()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var spans = recorder.Activities.Where(activity => activity.OperationName == "Heyi.AcceptanceCheck").ToList();
        Assert.That(spans.Select(span => span.DisplayName), Is.EqualTo(new[] { "Heyi.AcceptanceCheck Accepting", "Heyi.AcceptanceCheck Rejecting" }));
        Assert.That(spans.Select(span => span.GetTagItem("heyi.acceptance_check.type")), Is.EqualTo(new[] { typeof(Accepting).FullName, typeof(Rejecting).FullName }));
        Assert.That(spans.Select(span => span.GetTagItem("heyi.acceptance_check.outcome")), Is.EqualTo(new[] { "accepted", "rejected" }));
        // Each span covers its own check only: the second check isn't a child of the first.
        Assert.That(spans[1].ParentSpanId, Is.Not.EqualTo(spans[0].SpanId));

        Assert.That(recorder.Measurements.Select(measurement => measurement.Instrument), Is.All.EqualTo("heyi.acceptance_check.duration"));
        Assert.That(recorder.Measurements.Select(measurement => measurement.Tags["heyi.acceptance_check.outcome"]), Is.EqualTo(new[] { "accepted", "rejected" }));
    }

    [Test]
    public async Task Failing_acceptance_checks_are_errors()
    {
        await using var app = await TestApp.Start(
            endpoints => endpoints.MapCommand<AcceptOnlyEndpointTests.PlaceOrder>("/orders").WithAcceptanceCheck<Failing>(),
            addHeyi: false,
            addTransactionalSession: false,
            configureServices: services => services.AddSingleton<IMessageSession>(new FakeMessageSession()));

        Assert.That(async () => await app.CreateClient().PostAsJsonAsync("/orders", new AcceptOnlyEndpointTests.PlaceOrder(Guid.NewGuid())), Throws.InvalidOperationException);

        var span = recorder.Single("Heyi.AcceptanceCheck");
        Assert.That(span.Status, Is.EqualTo(ActivityStatusCode.Error));
        Assert.That(span.GetTagItem("heyi.acceptance_check.outcome"), Is.EqualTo("failed"));
    }

    class Accepting : IAcceptanceCheck<AcceptOnlyEndpointTests.PlaceOrder>
    {
        public ValueTask<Acceptance> Check(AcceptOnlyEndpointTests.PlaceOrder message, HttpContext httpContext) =>
            ValueTask.FromResult(Acceptance.Accept());
    }

    class Rejecting : IAcceptanceCheck<AcceptOnlyEndpointTests.PlaceOrder>
    {
        public ValueTask<Acceptance> Check(AcceptOnlyEndpointTests.PlaceOrder message, HttpContext httpContext) =>
            ValueTask.FromResult(Acceptance.Reject(TypedResults.Conflict()));
    }

    class Failing : IAcceptanceCheck<AcceptOnlyEndpointTests.PlaceOrder>
    {
        public ValueTask<Acceptance> Check(AcceptOnlyEndpointTests.PlaceOrder message, HttpContext httpContext) =>
            throw new InvalidOperationException("The check failed.");
    }
}
