namespace Heyi.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus;
using NUnit.Framework;

public class AcceptOnlyEndpointTests
{
    public record PlaceOrder(Guid OrderId);

    public record OrderRequested(Guid OrderId);

    FakeMessageSession messageSession = null!;

    [SetUp]
    public void NewMessageSession() => messageSession = new FakeMessageSession();

    Task<TestApp> Start(Action<IEndpointRouteBuilder> map, bool addMessageSession = true) =>
        TestApp.Start(
            map,
            addHeyi: false,
            addTransactionalSession: false,
            configureServices: services =>
            {
                if (addMessageSession)
                {
                    services.AddSingleton<IMessageSession>(messageSession);
                }
            });

    [Test]
    public async Task MapCommand_sends_the_body_and_returns_accepted()
    {
        await using var app = await Start(endpoints => endpoints.MapCommand<PlaceOrder>("/orders"));
        var orderId = Guid.NewGuid();

        using var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(orderId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Headers.Location, Is.Null);
        Assert.That(messageSession.SentMessages.Select(sent => sent.Message), Is.EqualTo(new object[] { new PlaceOrder(orderId) }));
        Assert.That(messageSession.PublishedMessages, Is.Empty);
    }

    [Test]
    public async Task MapEvent_publishes_the_body_and_returns_accepted()
    {
        await using var app = await Start(endpoints => endpoints.MapEvent<OrderRequested>("/order-requests"));
        var orderId = Guid.NewGuid();

        using var response = await app.CreateClient().PostAsJsonAsync("/order-requests", new OrderRequested(orderId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(messageSession.PublishedMessages.Select(published => published.Message), Is.EqualTo(new object[] { new OrderRequested(orderId) }));
        Assert.That(messageSession.SentMessages, Is.Empty);
    }

    [Test]
    public async Task Location_is_returned_when_configured()
    {
        await using var app = await Start(endpoints =>
            endpoints.MapCommand<PlaceOrder>("/orders", location: command => $"/orders/{command.OrderId}"));
        var orderId = Guid.NewGuid();

        using var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(orderId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(response.Headers.Location?.ToString(), Is.EqualTo($"/orders/{orderId}"));
    }

    [Test]
    public async Task Failed_dispatch_returns_a_problem()
    {
        messageSession.DispatchFailure = new InvalidOperationException("The broker is down.");
        await using var app = await Start(endpoints => endpoints.MapCommand<PlaceOrder>("/orders"));

        using var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(Guid.NewGuid()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
    }

    [Test]
    public async Task Missing_body_is_a_bad_request_and_sends_nothing()
    {
        await using var app = await Start(endpoints => endpoints.MapCommand<PlaceOrder>("/orders"));

        using var response = await app.CreateClient().PostAsync("/orders", JsonContent.Create<PlaceOrder?>(null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(messageSession.SentMessages, Is.Empty);
    }

    [Test]
    public async Task Works_in_route_groups()
    {
        await using var app = await Start(endpoints => endpoints.MapGroup("/orders").MapCommand<PlaceOrder>("/"));

        using var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(Guid.NewGuid()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(messageSession.SentMessages, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Missing_message_session_fails_when_endpoints_are_built()
    {
        await using var app = await Start(endpoints => endpoints.MapCommand<PlaceOrder>("/orders"), addMessageSession: false);

        Assert.That(app.BuildEndpoints, Throws.InvalidOperationException.With.Message.Contains("AddNServiceBusEndpoint"));
    }
}
