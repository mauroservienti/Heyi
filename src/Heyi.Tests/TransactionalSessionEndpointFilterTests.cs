namespace Heyi.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;

public class TransactionalSessionEndpointFilterTests
{
    [Test]
    public async Task Success_status_code_commits_data_and_messages()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request, IOutgoingMessages messages) =>
            {
                messages.Publish(new OrderPlaced(request.OrderId));
                return Results.Created($"/orders/{request.OrderId}", null);
            })
            .WithTransactionalSession());

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(app.Session.OpenedWith, Is.TypeOf<TestOpenSessionOptions>());
        Assert.That(app.Session.Committed, Is.True);
        Assert.That(app.Session.Disposed, Is.True);
        Assert.That(app.Session.Operations, Has.Count.EqualTo(1));
        Assert.That(app.Session.Operations[0].Kind, Is.EqualTo("Publish"));
        Assert.That(app.Session.Operations[0].Message, Is.EqualTo(new OrderPlaced(42)));
    }

    [Test]
    public async Task Plain_return_value_commits()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request) => request).WithTransactionalSession());

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.Committed, Is.True);
    }

    [TestCase(StatusCodes.Status400BadRequest)]
    [TestCase(StatusCodes.Status404NotFound)]
    [TestCase(StatusCodes.Status409Conflict)]
    [TestCase(StatusCodes.Status500InternalServerError)]
    [TestCase(StatusCodes.Status503ServiceUnavailable)]
    public async Task Non_success_status_code_rolls_back(int statusCode)
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request, IOutgoingMessages messages) =>
            {
                messages.Publish(new OrderPlaced(request.OrderId));
                return Results.StatusCode(statusCode);
            })
            .WithTransactionalSession());

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That((int)response.StatusCode, Is.EqualTo(statusCode));
        Assert.That(app.Session.Committed, Is.False);
        Assert.That(app.Session.Operations, Is.Empty);
        Assert.That(app.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Typed_union_result_uses_the_inner_status_code()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", Results<Created, BadRequest> (PlaceOrder request) =>
                request.OrderId > 0 ? TypedResults.Created($"/orders/{request.OrderId}") : TypedResults.BadRequest())
            .WithTransactionalSession());

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(0));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(app.Session.Committed, Is.False);
    }

    [Test]
    public async Task Status_code_set_on_the_response_is_respected()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request, HttpContext httpContext) =>
            {
                httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
                return request;
            })
            .WithTransactionalSession());

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(app.Session.Committed, Is.False);
    }

    [Test]
    public async Task Exception_rolls_back()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", IResult (PlaceOrder request, IOutgoingMessages messages) =>
            {
                messages.Publish(new OrderPlaced(request.OrderId));
                throw new InvalidOperationException("Boom");
            })
            .WithTransactionalSession());

        // TestServer rethrows unhandled exceptions to the client instead of returning a 500.
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42)));

        Assert.That(app.Session.Committed, Is.False);
        Assert.That(app.Session.Operations, Is.Empty);
        Assert.That(app.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Failed_commit_never_produces_a_success_response()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request) => Results.Created($"/orders/{request.OrderId}", null))
            .WithTransactionalSession());
        app.Session.CommitException = new InvalidOperationException("Commit failed");

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(response.Headers.Location, Is.Null);
        Assert.That(app.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Route_groups_are_transactional()
    {
        await using var app = await TestApp.Start(endpoints =>
        {
            var orders = endpoints.MapGroup("/orders").WithTransactionalSession();
            orders.MapPost("/", (PlaceOrder request, IOutgoingMessages messages) =>
            {
                messages.Send(request);
                return Results.Accepted();
            });
        });

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(app.Session.Committed, Is.True);
        Assert.That(app.Session.Operations[0].Kind, Is.EqualTo("Send"));
    }

    [Test]
    public async Task Per_endpoint_open_session_options_override_the_default()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request) => Results.Ok())
                .WithTransactionalSession(() => new TestOpenSessionOptions { Name = "override" }));

        await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(((TestOpenSessionOptions)app.Session.OpenedWith!).Name, Is.EqualTo("override"));
    }

    [Test]
    public async Task Outgoing_messages_outside_a_transactional_endpoint_throw()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request, IOutgoingMessages messages) =>
            {
                messages.Publish(new OrderPlaced(request.OrderId));
                return Results.Ok();
            }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42)));

        Assert.That(exception!.Message, Does.Contain("WithTransactionalSession"));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }

    [Test]
    public async Task Missing_AddHeyi_fails_when_endpoints_are_built()
    {
        await using var app = await TestApp.Start(
            endpoints => endpoints.MapPost("/orders", () => Results.Ok()).WithTransactionalSession(),
            addHeyi: false);

        var exception = Assert.Throws<InvalidOperationException>(app.BuildEndpoints);
        Assert.That(exception!.Message, Does.Contain("AddHeyi"));
    }

    [Test]
    public async Task Missing_transactional_session_fails_when_endpoints_are_built()
    {
        await using var app = await TestApp.Start(
            endpoints => endpoints.MapPost("/orders", () => Results.Ok()).WithTransactionalSession(),
            addTransactionalSession: false);

        var exception = Assert.Throws<InvalidOperationException>(app.BuildEndpoints);
        Assert.That(exception!.Message, Does.Contain("ITransactionalSession"));
    }

    [Test]
    public async Task Missing_open_session_options_fail_when_endpoints_are_built()
    {
        await using var app = await TestApp.Start(
            endpoints => endpoints.MapPost("/orders", () => Results.Ok()).WithTransactionalSession(),
            configureHeyi: _ => { });

        var exception = Assert.Throws<InvalidOperationException>(app.BuildEndpoints);
        Assert.That(exception!.Message, Does.Contain("open session options"));
    }
}
