namespace Heyi.Tests;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using ServiceComposer.AspNetCore;

// ServiceComposer runs composition over controllers as an endpoint filter, right after the controller action,
// so MapControllers().WithTransactionalSession() wraps the action and the composition handlers in one session.
public class CompositionOverControllersTests
{
    public record ReserveTicket(string ReservationId);

    public record InitializePayment(string ReservationId);

    public record InitializeShipping(string ReservationId);

    public class ReservationRequested
    {
        public string ReservationId { get; set; } = "";
    }

    // ServiceComposer's composition filter must be the innermost one, so WithCompositionOverControllers() goes last.
    static Task<TestApp> Start() =>
        TestApp.Start(
            endpoints =>
            {
                endpoints.MapControllers()
                    .WithTransactionalSession()
                    .WithCompositionOverControllers();
                endpoints.MapCompositionHandlers();
            },
            configureServices: services =>
            {
                services.AddControllersWithViews().AddApplicationPart(typeof(CompositionOverControllersTests).Assembly);
                services.AddViewModelComposition(options =>
                {
                    options.AssemblyScanner.Disable();
                    options.RegisterCompositionHandler<ReservationsReserveHandler>();
                    options.RegisterCompositionHandler<FinanceReserveHandler>();
                    options.RegisterCompositionHandler<ShippingReservationRequestedSubscriber>();
                    options.RegisterCompositionHandler<FailingCheckoutHandler>();
                    options.RegisterCompositionHandler<ReviewHandler>();
                });
            });

    [Test]
    public async Task The_action_and_the_composition_handlers_commit_together()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().PostAsync("/composed/reservations/42", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(app.Session.Committed, Is.True);
        Assert.That(app.Session.Operations.Select(operation => operation.Message), Is.EquivalentTo(new object[]
        {
            new ReserveTicket("42"), new InitializePayment("42"), new InitializeShipping("42")
        }));
    }

    [Test]
    public async Task A_failing_composition_handler_rolls_back()
    {
        await using var app = await Start();

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.CreateClient().PostAsync("/composed/checkout", null));

        Assert.That(app.Session.OpenedWith, Is.Not.Null);
        Assert.That(app.Session.Committed, Is.False);
        Assert.That(app.Session.Disposed, Is.True);
    }

    [Test]
    public async Task Reads_run_without_a_session()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().GetAsync("/composed/review");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }

    class ReservationsReserveHandler(IOutgoingMessages messages) : ICompositionRequestsHandler
    {
        [HttpPost("/composed/reservations/{id}")]
        public async Task Handle(HttpRequest request)
        {
            var id = (string)request.HttpContext.GetRouteValue("id")!;
            await request.GetCompositionContext().RaiseEvent(new ReservationRequested { ReservationId = id });
            await messages.Send(new ReserveTicket(id));
        }
    }

    class FinanceReserveHandler(IOutgoingMessages messages) : ICompositionRequestsHandler
    {
        [HttpPost("/composed/reservations/{id}")]
        public Task Handle(HttpRequest request) =>
            messages.Send(new InitializePayment((string)request.HttpContext.GetRouteValue("id")!));
    }

    class ShippingReservationRequestedSubscriber(IOutgoingMessages messages) : ICompositionEventsSubscriber
    {
        [HttpPost("/composed/reservations/{id}")]
        public void Subscribe(ICompositionEventsPublisher publisher) =>
            publisher.Subscribe<ReservationRequested>((@event, _) => messages.Send(new InitializeShipping(@event.ReservationId)));
    }

    class FailingCheckoutHandler(IOutgoingMessages messages) : ICompositionRequestsHandler
    {
        [HttpPost("/composed/checkout")]
        public async Task Handle(HttpRequest request)
        {
            await messages.Send(new InitializePayment("42"));
            throw new InvalidOperationException("The payment method is invalid.");
        }
    }

    class ReviewHandler : ICompositionRequestsHandler
    {
        [HttpGet("/composed/review")]
        public Task Handle(HttpRequest request) => Task.CompletedTask;
    }
}

// MVC only discovers top-level controllers. The actions redirect, as after a form post.
public class ComposedReservationsController : Controller
{
    [HttpPost("/composed/reservations/{id}")]
    public IActionResult Reserve(string id) => RedirectToAction(nameof(Review));

    [HttpPost("/composed/checkout")]
    public IActionResult Checkout() => RedirectToAction(nameof(Review));

    [HttpGet("/composed/review")]
    public IActionResult Review() => Ok(new { });
}
