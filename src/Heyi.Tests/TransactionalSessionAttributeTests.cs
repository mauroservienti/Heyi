namespace Heyi.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

public class TransactionalSessionAttributeTests
{
    public record AddNote(int OrderId);

    static Task<TestApp> StartControllers(bool addTransactionalSession = true) =>
        TestApp.Start(
            endpoints => endpoints.MapControllers().WithTransactionalSessionAttributes(),
            addTransactionalSession: addTransactionalSession,
            configureServices: services => services.AddControllers().AddApplicationPart(typeof(TransactionalSessionAttributeTests).Assembly));

    [Test]
    public async Task Marked_actions_run_in_a_session()
    {
        await using var app = await StartControllers();

        using var response = await app.CreateClient().PostAsJsonAsync("/marked-actions/cancellations", new ControllerTests.CancelOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(app.Session.Committed, Is.True);
        Assert.That(app.Session.Operations.Single().Message, Is.EqualTo(new ControllerTests.CancelOrder(42)));
    }

    [Test]
    public async Task Unmarked_actions_run_without_a_session()
    {
        await using var app = await StartControllers();

        using var response = await app.CreateClient().PostAsJsonAsync("/marked-actions/notes", new AddNote(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }

    [TestCase("/marked-controller/cancellations")]
    [TestCase("/derived-controller/cancellations")]
    public async Task Marked_controllers_run_all_their_actions_in_a_session(string path)
    {
        await using var app = await StartControllers();

        using var response = await app.CreateClient().PostAsJsonAsync(path, new ControllerTests.CancelOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(app.Session.Committed, Is.True);
    }

    [Test]
    public async Task Marked_minimal_api_handlers_run_in_a_session()
    {
        await using var app = await TestApp.Start(endpoints =>
        {
            var orders = endpoints.MapGroup("/orders").WithTransactionalSessionAttributes();
            orders.MapPost("/cancellations", [TransactionalSession] async (ControllerTests.CancelOrder command, IOutgoingMessages messages) =>
            {
                await messages.Send(command);
                return Results.Accepted();
            });
            orders.MapPost("/notes", (AddNote note) => Results.Ok());
        });
        var client = app.CreateClient();

        using var notes = await client.PostAsJsonAsync("/orders/notes", new AddNote(42));
        Assert.That(app.Session.OpenedWith, Is.Null);

        using var cancellations = await client.PostAsJsonAsync("/orders/cancellations", new ControllerTests.CancelOrder(42));
        Assert.That(cancellations.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(app.Session.Committed, Is.True);
    }

    [Test]
    public async Task Marked_endpoints_still_require_the_transactional_session() =>
        Assert.That(async () => await StartControllers(addTransactionalSession: false),
            Throws.InvalidOperationException.With.Message.Contains("ITransactionalSession"));
}

[ApiController]
[Route("marked-actions")]
public class MarkedActionsController(IOutgoingMessages messages) : ControllerBase
{
    [HttpPost("cancellations")]
    [TransactionalSession]
    public async Task<IActionResult> Cancel(ControllerTests.CancelOrder command)
    {
        await messages.Send(command);
        return Accepted();
    }

    [HttpPost("notes")]
    public IActionResult AddNote(TransactionalSessionAttributeTests.AddNote note) => Ok();
}

[ApiController]
[Route("marked-controller")]
[TransactionalSession]
public class MarkedController(IOutgoingMessages messages) : ControllerBase
{
    [HttpPost("cancellations")]
    public async Task<IActionResult> Cancel(ControllerTests.CancelOrder command)
    {
        await messages.Send(command);
        return Accepted();
    }
}

[TransactionalSession]
public abstract class MarkedControllerBase(IOutgoingMessages messages) : ControllerBase
{
    [HttpPost("cancellations")]
    public async Task<IActionResult> Cancel(ControllerTests.CancelOrder command)
    {
        await messages.Send(command);
        return Accepted();
    }
}

[ApiController]
[Route("derived-controller")]
public class DerivedController(IOutgoingMessages messages) : MarkedControllerBase(messages);
