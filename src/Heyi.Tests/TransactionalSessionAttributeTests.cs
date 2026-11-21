namespace Heyi.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

// [TransactionalSession] is an MVC filter: marked controllers and actions are transactional
// without any endpoint configuration.
public class TransactionalSessionAttributeTests
{
    public record AddNote(int OrderId);

    static Task<TestApp> StartControllers(bool withTransactionalSession = false, bool addTransactionalSession = true) =>
        TestApp.Start(
            endpoints =>
            {
                var controllers = endpoints.MapControllers();
                if (withTransactionalSession)
                {
                    controllers.WithTransactionalSession();
                }
            },
            addTransactionalSession: addTransactionalSession,
            configureServices: services => services.AddControllers().AddApplicationPart(typeof(TransactionalSessionAttributeTests).Assembly));

    static async Task<HttpResponseMessage> Cancel(TestApp app, string path, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new ControllerTests.CancelOrder(42)) };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await app.CreateClient().SendAsync(request);
    }

    [Test]
    public async Task Marked_actions_run_in_a_session()
    {
        await using var app = await StartControllers();

        using var response = await Cancel(app, "/marked-actions/cancellations");

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

        using var response = await Cancel(app, path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(app.Session.Committed, Is.True);
    }

    [Test]
    public async Task An_error_result_rolls_back()
    {
        await using var app = await StartControllers();

        using var response = await Cancel(app, "/marked-actions/rejected");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(app.Session.Committed, Is.False);
        Assert.That(app.Session.Disposed, Is.True);
    }

    [Test]
    public async Task An_exception_rolls_back_and_reaches_mvc()
    {
        await using var app = await StartControllers();

        Assert.That(async () => await Cancel(app, "/marked-actions/failing"),
            Throws.InvalidOperationException.With.Message.EqualTo("The warehouse is on fire."));
        Assert.That(app.Session.OpenedWith, Is.Not.Null);
        Assert.That(app.Session.Committed, Is.False);
        Assert.That(app.Session.Disposed, Is.True);
    }

    [Test]
    public async Task An_invalid_idempotency_key_is_rejected_before_the_action_runs()
    {
        await using var app = await StartControllers();

        using var response = await Cancel(app, "/marked-actions/cancellations", "\"\"");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(app.Session.OpenedWith, Is.Null);
        Assert.That(app.Session.Operations, Is.Empty);
    }

    [Test]
    public async Task Marked_actions_mapped_with_WithTransactionalSession_run_in_a_single_session()
    {
        await using var app = await StartControllers(withTransactionalSession: true);

        using var response = await Cancel(app, "/marked-actions/cancellations");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(app.Session.Committed, Is.True);
        Assert.That(app.Session.Operations, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Marked_actions_require_the_transactional_session()
    {
        await using var app = await StartControllers(addTransactionalSession: false);

        Assert.That(async () => await Cancel(app, "/marked-actions/cancellations"),
            Throws.InvalidOperationException.With.Message.Contains("ITransactionalSession"));
    }
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

    [HttpPost("rejected")]
    [TransactionalSession]
    public async Task<IActionResult> Rejected(ControllerTests.CancelOrder command)
    {
        await messages.Send(command);
        return BadRequest("The order has already shipped.");
    }

    [HttpPost("failing")]
    [TransactionalSession]
    public async Task<IActionResult> Failing(ControllerTests.CancelOrder command)
    {
        await messages.Send(command);
        throw new InvalidOperationException("The warehouse is on fire.");
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
