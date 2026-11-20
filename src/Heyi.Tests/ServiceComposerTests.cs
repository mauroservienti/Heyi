namespace Heyi.Tests;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using ServiceComposer.AspNetCore;

// ServiceComposer runs endpoint filters on composition endpoints, so WithTransactionalSession
// wraps every handler of a request in a single transactional session.
public class ServiceComposerTests
{
    public record SalesCommand(string OrderId);

    public record ShippingCommand(string OrderId);

    static Task<TestApp> Start() =>
        TestApp.Start(
            endpoints => endpoints.MapCompositionHandlers().WithTransactionalSession(),
            configureServices: services =>
            {
                services.AddControllers();
                services.AddViewModelComposition(options =>
                {
                    options.AssemblyScanner.Disable();
                    options.RegisterCompositionHandler<SalesHandler>();
                    options.RegisterCompositionHandler<ShippingHandler>();
                    options.RegisterCompositionHandler<OrderDetailsHandler>();
                    options.ResponseSerialization.UseOutputFormatters = true;
                });
            });

    [Test]
    public async Task Handlers_of_a_request_commit_together()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().PostAsync("/orders/42", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.Committed, Is.True);
        Assert.That(app.Session.Operations.Select(operation => operation.Message),
            Is.EquivalentTo(new object[] { new SalesCommand("42"), new ShippingCommand("42") }));
    }

    [Test]
    public async Task An_action_result_with_an_error_status_code_rolls_back()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().PostAsync("/orders/0", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(app.Session.Committed, Is.False);
        Assert.That(app.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Reads_run_without_a_session()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().GetAsync("/orders/42");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }

    class OrderDetailsHandler : ICompositionRequestsHandler
    {
        [HttpGet("/orders/{id}")]
        public Task Handle(HttpRequest request)
        {
            request.GetComposedResponseModel().OrderId = (string)request.RouteValues["id"]!;
            return Task.CompletedTask;
        }
    }

    class SalesHandler(IOutgoingMessages messages) : ICompositionRequestsHandler
    {
        [HttpPost("/orders/{id}")]
        public async Task Handle(HttpRequest request)
        {
            var orderId = (string)request.RouteValues["id"]!;
            await messages.Send(new SalesCommand(orderId));

            if (orderId == "0")
            {
                request.SetActionResult(new BadRequestObjectResult("Order 0 isn't valid."));
            }
        }
    }

    class ShippingHandler(IOutgoingMessages messages) : ICompositionRequestsHandler
    {
        [HttpPost("/orders/{id}")]
        public Task Handle(HttpRequest request) =>
            messages.Send(new ShippingCommand((string)request.RouteValues["id"]!));
    }
}
