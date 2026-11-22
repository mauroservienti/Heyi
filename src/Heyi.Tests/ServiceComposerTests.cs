namespace Heyi.Tests;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus;
using NUnit.Framework;
using ServiceComposer.AspNetCore;

// ServiceComposer runs endpoint filters on composition endpoints, so WithTransactionalSession
// wraps every handler of a request in a single transactional session.
public class ServiceComposerTests
{
    public record SalesCommand(string OrderId);

    public record ShippingCommand(string OrderId);

    public record CartItemAdded(string CartId);

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
                    options.RegisterCompositionHandler<SalesCartHandler>();
                    options.RegisterCompositionHandler<WarehouseCartHandler>();
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
    public async Task Concurrent_handlers_get_deterministic_message_ids()
    {
        await using var app = await Start();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders/42");
        request.Headers.Add("Idempotency-Key", "\"order-42\"");
        using var response = await app.CreateClient().SendAsync(request);

        Assert.That(app.Session.Operations.Select(operation => operation.Options.GetMessageId()), Is.EquivalentTo(new[]
        {
            $"order-42/{IdempotencyKey.TypeTag(typeof(SalesCommand))}/0",
            $"order-42/{IdempotencyKey.TypeTag(typeof(ShippingCommand))}/0"
        }));
    }

    [Test]
    public async Task Concurrent_handlers_sending_the_same_type_get_their_own_message_ids()
    {
        await using var app = await Start();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/carts/42");
        request.Headers.Add("Idempotency-Key", "\"cart-42\"");
        using var response = await app.CreateClient().SendAsync(request);

        var type = IdempotencyKey.TypeTag(typeof(CartItemAdded));
        Assert.That(app.Session.Operations.Select(operation => operation.Options.GetMessageId()), Is.EquivalentTo(new[]
        {
            $"cart-42/{IdempotencyKey.TypeTag(typeof(SalesCartHandler))}/{type}/0",
            $"cart-42/{IdempotencyKey.TypeTag(typeof(WarehouseCartHandler))}/{type}/0"
        }));
    }

    [Test]
    public async Task Reads_run_without_a_session()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().GetAsync("/orders/42");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }

    static Task<TestApp> StartWith(Action<ViewModelCompositionOptions> registerHandlers) =>
        TestApp.Start(
            endpoints => endpoints.MapCompositionHandlers().WithTransactionalSession(),
            configureServices: services =>
            {
                services.AddControllers();
                services.AddViewModelComposition(options =>
                {
                    options.AssemblyScanner.Disable();
                    registerHandlers(options);
                    options.ResponseSerialization.UseOutputFormatters = true;
                });
            });

    [Test]
    public async Task A_route_whose_handlers_are_all_opted_out_runs_without_a_session()
    {
        await using var app = await StartWith(options =>
        {
            options.RegisterCompositionHandler<OptedOutWebhookHandler>();
            options.RegisterCompositionHandler<OptedOutWebhookAuditHandler>();
        });

        using var response = await app.CreateClient().PostAsync("/webhooks/42", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }

    [Test]
    public async Task A_handler_method_opts_back_in_when_its_class_is_opted_out()
    {
        await using var app = await StartWith(options => options.RegisterCompositionHandler<OptedBackInHandler>());

        using var response = await app.CreateClient().PostAsync("/payments/42", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.Committed, Is.True);
    }

    [Test]
    public async Task Handlers_of_other_routes_are_unaffected()
    {
        await using var app = await StartWith(options =>
        {
            options.RegisterCompositionHandler<OptedOutWebhookHandler>();
            options.RegisterCompositionHandler<SalesHandler>();
            options.RegisterCompositionHandler<ShippingHandler>();
        });

        using var response = await app.CreateClient().PostAsync("/orders/42", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.Committed, Is.True);
    }

    [Test]
    public async Task Opting_out_only_some_handlers_of_a_route_fails()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = await StartWith(options =>
            {
                options.RegisterCompositionHandler<OptedOutWebhookHandler>();
                options.RegisterCompositionHandler<WebhookHandler>();
            });
            app.BuildEndpoints();
        });

        Assert.That(exception!.Message, Does.Contain("OptedOutWebhookHandler").And.Contain("(WebhookHandler)"));
    }

    class OptedOutWebhookHandler : ICompositionRequestsHandler
    {
        [HttpPost("/webhooks/{id}"), WithoutTransactionalSession]
        public Task Handle(HttpRequest request) => Task.CompletedTask;
    }

    [WithoutTransactionalSession]
    class OptedOutWebhookAuditHandler : ICompositionRequestsHandler
    {
        [HttpPost("/webhooks/{id}")]
        public Task Handle(HttpRequest request) => Task.CompletedTask;
    }

    class WebhookHandler : ICompositionRequestsHandler
    {
        [HttpPost("/webhooks/{id}")]
        public Task Handle(HttpRequest request) => Task.CompletedTask;
    }

    [WithoutTransactionalSession]
    class OptedBackInHandler(IOutgoingMessages messages) : ICompositionRequestsHandler
    {
        [HttpPost("/payments/{id}"), WithTransactionalSession]
        public Task Handle(HttpRequest request) =>
            messages.Send(new SalesCommand((string)request.RouteValues["id"]!));
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

    // Two services lazily sharing a message type: each counts its own messages.
    class SalesCartHandler(IOutgoingMessages<SalesCartHandler> messages) : ICompositionRequestsHandler
    {
        [HttpPost("/carts/{id}")]
        public Task Handle(HttpRequest request) =>
            messages.Publish(new CartItemAdded((string)request.RouteValues["id"]!));
    }

    class WarehouseCartHandler(IOutgoingMessages<WarehouseCartHandler> messages) : ICompositionRequestsHandler
    {
        [HttpPost("/carts/{id}")]
        public Task Handle(HttpRequest request) =>
            messages.Publish(new CartItemAdded((string)request.RouteValues["id"]!));
    }
}
