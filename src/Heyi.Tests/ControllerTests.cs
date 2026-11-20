namespace Heyi.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

// MVC runs endpoint filters on controller actions, so MapControllers().WithTransactionalSession()
// wraps every action, and IOutgoingMessages can be injected into controllers.
public class ControllerTests
{
    public record CancelOrder(int OrderId);

    static Task<TestApp> Start() =>
        TestApp.Start(
            endpoints => endpoints.MapControllers().WithTransactionalSession(),
            configureServices: services => services.AddControllers().AddApplicationPart(typeof(ControllerTests).Assembly));

    [Test]
    public async Task A_success_result_commits()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().PostAsJsonAsync("/cancellations", new CancelOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(app.Session.Committed, Is.True);
        Assert.That(app.Session.Operations.Single().Message, Is.EqualTo(new CancelOrder(42)));
    }

    [TestCase("/cancellations/rejected", HttpStatusCode.BadRequest)]
    [TestCase("/cancellations/unknown", HttpStatusCode.NotFound)]
    public async Task An_error_result_rolls_back(string path, HttpStatusCode statusCode)
    {
        await using var app = await Start();

        using var response = await app.CreateClient().PostAsJsonAsync(path, new CancelOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(statusCode));
        Assert.That(app.Session.OpenedWith, Is.Not.Null);
        Assert.That(app.Session.Committed, Is.False);
        Assert.That(app.Session.Disposed, Is.True);
    }

    [Test]
    public async Task An_invalid_model_is_rejected_before_the_session_opens()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().PostAsync("/cancellations", JsonContent.Create(new { OrderId = "not a number" }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }

    [Test]
    public async Task Reads_run_without_a_session()
    {
        await using var app = await Start();

        using var response = await app.CreateClient().GetAsync("/cancellations/42");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }
}

[ApiController]
[Route("cancellations")]
public class CancellationsController(IOutgoingMessages messages) : ControllerBase
{
    [HttpGet("{orderId:int}")]
    public ControllerTests.CancelOrder Get(int orderId) => new(orderId);

    [HttpPost]
    public async Task<IActionResult> Cancel(ControllerTests.CancelOrder command)
    {
        await messages.Send(command);
        return Accepted();
    }

    [HttpPost("rejected")]
    public async Task<IActionResult> Rejected(ControllerTests.CancelOrder command)
    {
        await messages.Send(command);
        return BadRequest("The order has already shipped.");
    }

    [HttpPost("unknown")]
    public async Task<ActionResult<ControllerTests.CancelOrder>> Unknown(ControllerTests.CancelOrder command)
    {
        await messages.Send(command);
        return NotFound();
    }
}
