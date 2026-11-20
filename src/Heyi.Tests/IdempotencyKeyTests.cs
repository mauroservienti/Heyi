namespace Heyi.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus;
using NServiceBus.Extensibility;
using NUnit.Framework;

public class IdempotencyKeyTests
{
    static Task<TestApp> StartAtomic() => TestApp.Start(endpoints =>
        endpoints.MapPost("/orders", async (PlaceOrder request, IOutgoingMessages messages) =>
        {
            await messages.Publish(new OrderPlaced(request.OrderId));
            await messages.Send(new OrderPlaced(request.OrderId));
            var explicitId = new SendOptions();
            explicitId.SetMessageId("explicit");
            await messages.SendLocal(new OrderPlaced(request.OrderId), explicitId);
            await messages.Publish(new OrderPlaced(request.OrderId));
            return Results.Created($"/orders/{request.OrderId}", null);
        })
        .WithTransactionalSession());

    static async Task<HttpResponseMessage> Post(TestApp app, string uri, object body, params string[] keys)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
        if (keys.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", keys);
        }

        return await app.CreateClient().SendAsync(request);
    }

    static IEnumerable<string?> MessageIds(IEnumerable<ExtendableOptions> options) => options.Select(o => o.GetMessageId());

    static string Id(string key, Type messageType, int index) => $"{key}/{IdempotencyKey.TypeTag(messageType)}/{index}";

    static string Id(string key, Type messageType, string destination, int index) =>
        $"{key}/{IdempotencyKey.TypeTag(messageType)}/{IdempotencyKey.Tag(destination)}/{index}";

    static IdempotencyKey Key(string value)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[IdempotencyKey.HeaderName] = value;
        IdempotencyKey.TryRead(request, out var key);
        return key!;
    }

    static SendOptions To(string destination)
    {
        var options = new SendOptions();
        options.SetDestination(destination);
        return options;
    }

    [Test]
    public async Task Atomic_endpoint_messages_get_the_key_and_their_position()
    {
        await using var app = await StartAtomic();

        using var response = await Post(app, "/orders", new PlaceOrder(42), "\"order-42\"");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(
            MessageIds(app.Session.Operations.Select(operation => operation.Options)),
            Is.EqualTo(new[] { Id("order-42", typeof(OrderPlaced), 0), Id("order-42", typeof(OrderPlaced), 1), "explicit", Id("order-42", typeof(OrderPlaced), 3) }));
    }

    [Test]
    public async Task Unquoted_keys_are_accepted()
    {
        await using var app = await StartAtomic();

        using var response = await Post(app, "/orders", new PlaceOrder(42), "order-42");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(app.Session.Operations[0].Options.GetMessageId(), Is.EqualTo(Id("order-42", typeof(OrderPlaced), 0)));
    }

    [Test]
    public async Task Without_a_key_message_ids_are_left_to_NServiceBus()
    {
        await using var app = await StartAtomic();

        using var response = await Post(app, "/orders", new PlaceOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(
            MessageIds(app.Session.Operations.Select(operation => operation.Options)),
            Is.EqualTo(new[] { null, null, "explicit", null }));
    }

    [TestCase("\"\"")]
    [TestCase(" ")]
    public async Task Empty_key_is_rejected_before_the_session_opens(string key)
    {
        await using var app = await StartAtomic();

        using var response = await Post(app, "/orders", new PlaceOrder(42), key);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(app.Session.OpenedWith, Is.Null);
        Assert.That(app.Session.Operations, Is.Empty);
    }

    [Test]
    public async Task More_than_one_key_is_rejected()
    {
        await using var app = await StartAtomic();

        using var response = await Post(app, "/orders", new PlaceOrder(42), "a", "b");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(app.Session.OpenedWith, Is.Null);
    }

    static Task<TestApp> StartAcceptOnly(FakeMessageSession messageSession) => TestApp.Start(
        endpoints =>
        {
            endpoints.MapCommand<PlaceOrder>("/orders");
            endpoints.MapEvent<OrderPlaced>("/order-placed-events");
        },
        addHeyi: false,
        addTransactionalSession: false,
        configureServices: services => services.AddSingleton<IMessageSession>(messageSession));

    [Test]
    public async Task Accept_only_commands_get_the_key()
    {
        var messageSession = new FakeMessageSession();
        await using var app = await StartAcceptOnly(messageSession);

        using var response = await Post(app, "/orders", new PlaceOrder(42), "\"order-42\"");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(messageSession.SentMessages.Single().Options.GetMessageId(), Is.EqualTo(Id("order-42", typeof(PlaceOrder), 0)));
    }

    [Test]
    public async Task Accept_only_events_get_the_key()
    {
        var messageSession = new FakeMessageSession();
        await using var app = await StartAcceptOnly(messageSession);

        using var response = await Post(app, "/order-placed-events", new OrderPlaced(42), "\"order-42\"");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(messageSession.PublishedMessages.Single().Options.GetMessageId(), Is.EqualTo(Id("order-42", typeof(OrderPlaced), 0)));
    }

    [Test]
    public async Task Accept_only_endpoints_reject_an_invalid_key_and_send_nothing()
    {
        var messageSession = new FakeMessageSession();
        await using var app = await StartAcceptOnly(messageSession);

        using var response = await Post(app, "/orders", new PlaceOrder(42), "a", "b");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(messageSession.SentMessages, Is.Empty);
    }

    [Test]
    public async Task Each_message_type_is_counted_on_its_own()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", async (PlaceOrder request, IOutgoingMessages messages) =>
            {
                await messages.Publish(new OrderPlaced(request.OrderId));
                await messages.SendLocal(new PlaceOrder(request.OrderId));
                await messages.Publish(new OrderPlaced(request.OrderId));
                return Results.Ok();
            })
            .WithTransactionalSession());

        using var response = await Post(app, "/orders", new PlaceOrder(42), "\"order-42\"");

        Assert.That(
            MessageIds(app.Session.Operations.Select(operation => operation.Options)),
            Is.EqualTo(new[] { Id("order-42", typeof(OrderPlaced), 0), Id("order-42", typeof(PlaceOrder), 0), Id("order-42", typeof(OrderPlaced), 1) }));
    }

    [Test]
    public void The_order_of_different_message_types_doesnt_change_their_ids()
    {
        string?[] StampInOrder(params Type[] types)
        {
            var key = Key("\"order-42\"");
            return types.Select(type =>
            {
                var options = new SendOptions();
                key!.Stamp(options, type);
                return options.GetMessageId();
            }).ToArray();
        }

        var placedFirst = StampInOrder(typeof(OrderPlaced), typeof(PlaceOrder));
        var placeFirst = StampInOrder(typeof(PlaceOrder), typeof(OrderPlaced));

        Assert.That(placedFirst, Is.EquivalentTo(placeFirst));
    }

    [Test]
    public void The_same_type_to_different_destinations_is_counted_per_destination()
    {
        string?[] StampInOrder(params string[] destinations)
        {
            var key = Key("\"order-42\"");
            return destinations.Select(destination =>
            {
                var options = To(destination);
                key.Stamp(options, typeof(PlaceOrder));
                return options.GetMessageId();
            }).ToArray();
        }

        var salesFirst = StampInOrder("Sales", "Warehouse", "Sales");
        var warehouseFirst = StampInOrder("Warehouse", "Sales", "Sales");

        Assert.That(salesFirst, Is.EqualTo(new[]
        {
            Id("order-42", typeof(PlaceOrder), "Sales", 0),
            Id("order-42", typeof(PlaceOrder), "Warehouse", 0),
            Id("order-42", typeof(PlaceOrder), "Sales", 1)
        }));
        Assert.That(warehouseFirst, Is.EquivalentTo(salesFirst));
    }

    [Test]
    public async Task Sends_with_an_explicit_destination_include_it()
    {
        await using var app = await TestApp.Start(endpoints =>
            endpoints.MapPost("/orders", async (PlaceOrder request, IOutgoingMessages messages) =>
            {
                await messages.Send(new PlaceOrder(request.OrderId), To("Sales"));
                await messages.Send(new PlaceOrder(request.OrderId));
                return Results.Ok();
            })
            .WithTransactionalSession());

        using var response = await Post(app, "/orders", new PlaceOrder(42), "\"order-42\"");

        Assert.That(
            MessageIds(app.Session.Operations.Select(operation => operation.Options)),
            Is.EqualTo(new[] { Id("order-42", typeof(PlaceOrder), "Sales", 0), Id("order-42", typeof(PlaceOrder), 0) }));
    }

    // Message IDs are a contract with receivers that deduplicate them:
    // the type tag must not change between versions.
    [Test]
    public void The_type_tag_is_the_start_of_the_full_name_hash() =>
        Assert.That(IdempotencyKey.TypeTag(typeof(OrderPlaced)), Is.EqualTo("0095bc78"));
}
