# Heyi

Heyi (合一, héyī, "to become one") turns ASP.NET Core minimal API endpoints into handler-shaped endpoints: an endpoint stores data and sends NServiceBus messages as one atomic operation.

```csharp
app.MapPost("/orders/{orderId}/cancellations", async (Guid orderId, RequestsDbContext db, IOutgoingMessages messages) =>
{
    var cancellation = new CancellationRequest(orderId);
    db.CancellationRequests.Add(cancellation);
    await messages.Send(new CancelOrder(orderId, cancellation.Id));
    return Results.Accepted($"/cancellations/{cancellation.Id}", cancellation);
})
.WithTransactionalSession();
```

The endpoint records the cancellation request, the task users can check on, and sends the message that starts the work: both, or neither. It doesn't open, commit, or dispose anything. Heyi does it around the endpoint, on top of the [NServiceBus transactional session](https://docs.particular.net/nservicebus/transactional-session/) and the outbox. Heyi adds the programming model, not the guarantees.

Heyi is available [on NuGet](https://www.nuget.org/packages/Heyi): `dotnet add package Heyi`. The [design document](https://github.com/mauroservienti/Heyi/blob/main/docs/design.md) describes the design decisions and what's next.

## How it works

- The session is opened before the endpoint runs. Reads (`GET`, `HEAD`, `OPTIONS`, `TRACE`) run without one, so a route group can mix reads and writes. If reads might change data or send messages, `services.AddHeyi(options => options.OpenSessionsForSafeMethods())` opens a session for them too.
- Right after opening it, the request's `DbContext` is moved onto the session's connection and transaction, no matter when the context was created. Injecting it as an endpoint parameter is fine.
- A `2xx` result commits, before the response is written, and the context's changes are saved as part of the commit: no `SaveChanges` call needed.
- Anything else, exceptions and `4xx`/`5xx` results included, disposes the session, which rolls back data and messages.
- A failed commit becomes a `500` problem details response, never a success.
- If the client aborts the request, the commit is canceled.
- `IOutgoingMessages` sends and publishes through the open session. Using it outside a transactional endpoint throws, instead of quietly sending right away.

## Setup

NServiceBus comes first, Heyi second. Heyi doesn't configure or host NServiceBus: configure the endpoint as usual, with SQL persistence, the transactional session, and the outbox enabled, and register it with `AddNServiceBusEndpoint`. Then add Heyi:

```csharp
builder.Services.AddHeyi(options =>
    options.OpenSessionOptions(() => new SqlPersistenceOpenSessionOptions()));

builder.Services.AddDbContext<RequestsDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddHeyiDbContext<RequestsDbContext>();
```

Mark endpoints, or whole route groups, as transactional:

```csharp
app.MapPost("/orders/{orderId}/cancellations", ...).WithTransactionalSession();

var order = app.MapGroup("/orders/{orderId}").WithTransactionalSession();
order.MapPost("/cancellations", ...);
order.MapPost("/returns", ...);
```

The open session options can be overridden per endpoint, by passing them to `WithTransactionalSession`.

It works with anything that runs endpoint filters. MVC controllers run them too: inject `IOutgoingMessages` into the controller, and results such as `BadRequest()` or `NotFound()` roll back like any other `4xx`. With [ServiceComposer](https://github.com/ServiceComposer/ServiceComposer.AspNetCore), all the composition handlers of a request send through the same session, and commit or roll back together.

```csharp
app.MapControllers().WithTransactionalSession();
app.MapCompositionHandlers().WithTransactionalSession();
```

`MapControllers()` covers every controller; reads run without a session. To choose which controllers or actions are transactional instead, mark them with `[WithTransactionalSession]`, and leave `MapControllers()` as it is: the others run without a session.

```csharp
app.MapControllers();

public class OrdersController(IOutgoingMessages messages) : ControllerBase
{
    [HttpPost("orders/{orderId}/cancellations")]
    [WithTransactionalSession]
    public async Task<IActionResult> Cancel(Guid orderId) { ... }
}
```

For an endpoint shaped like a message handler, a controller needs no base class. A class with `[Controller]` gets its dependencies through the constructor, MVC discovers and routes it, and its actions can return the same `IResult` values as minimal API endpoints:

```csharp
[Controller]
public class PlaceOrderEndpoint(OrdersDbContext db, IOutgoingMessages messages)
{
    [HttpPost("orders"), WithTransactionalSession]
    public async Task<IResult> Handle([FromBody] PlaceOrder request)
    {
        var order = Order.From(request);
        db.Orders.Add(order);
        await messages.Publish(new OrderPlaced(order.Id));
        return Results.Created($"/orders/{order.Id}", order);
    }
}
```

Without `[ApiController]`, MVC doesn't bind complex parameters from the body by default, hence `[FromBody]`.

On a controller, the attribute applies to all its actions. It's an MVC filter, so it's for controllers only: minimal API endpoints and composition handlers use `WithTransactionalSession()`. A marked action that's also mapped with `WithTransactionalSession()` runs in a single session.

The other way around, `[WithoutTransactionalSession]` opts a controller or action out, for example a webhook in an app mapped with `MapControllers().WithTransactionalSession()`. It works on minimal API handlers in a transactional route group too:

```csharp
var order = app.MapGroup("/orders/{orderId}").WithTransactionalSession();
order.MapPost("/webhook", [WithoutTransactionalSession] () => ...);
```

The closest attribute wins: `[WithTransactionalSession]` on an action opts it back in, even if its controller is opted out, and the other way around.

Neither attribute works on ServiceComposer composition handlers: a request runs all the composition handlers of a route in one session, and `MapCompositionHandlers().WithTransactionalSession()` can't tell them apart. Marking any fails when the endpoints are built, instead of being silently ignored.

With ServiceComposer's [composition over controllers](https://github.com/ServiceComposer/ServiceComposer.AspNetCore/blob/master/docs/composition-over-controllers.md), the composition handlers run in an endpoint filter right after the controller action, so `WithTransactionalSession()` on the controllers wraps the action and the handlers in one session. It needs `WithCompositionOverControllers()`, from ServiceComposer 5.3: `EnableCompositionOverControllers()` runs the handlers in an MVC result filter, after the session has committed.

```csharp
app.MapControllers()
    .WithTransactionalSession()
    .WithCompositionOverControllers();
app.MapCompositionHandlers();
```

Call `WithCompositionOverControllers()` last: ServiceComposer's composition filter must be the innermost one. `[WithoutTransactionalSession]` and `[WithTransactionalSession]` apply to the controller and its actions only: on composition handlers composing over a controller, they're ignored.

Heyi checks its prerequisites when the endpoints are built, and fails fast with a clear message if `AddHeyi` is missing, the transactional session isn't enabled, or no open session options are configured.

Web applications are usually send-only endpoints, which requires a [remote processor endpoint](https://docs.particular.net/nservicebus/transactional-session/#remote-processor) with the outbox and the transactional session enabled.

## Accept-only endpoints

HTTP endpoints shouldn't run business logic: a message handler runs it with retries, the outbox, and everything else messaging offers. Atomic endpoints are for task-based applications, where the task users follow must be created together with the message that starts the work. For everything else, use accept-only endpoints.

An accept-only endpoint binds the request body to a message, sends or publishes it, and returns `202 Accepted`; a regular message handler does the work:

```csharp
app.MapCommand<PlaceOrder>("/orders");           // Send, 202 Accepted
app.MapEvent<OrderRequested>("/order-requests"); // Publish, 202 Accepted
```

- No database and no transactional session: only the NServiceBus endpoint.
- Destinations come from the NServiceBus routing configuration.
- `location: command => $"/orders/{command.OrderId}"` returns a location to poll.
- A missing or malformed body is a `400`, and nothing is sent. A failed send is a `500` problem details response.
- Validation filters, authorization, and other endpoint conventions are added as usual.

### Checking before accepting

Some requests shouldn't be accepted at all: malformed data, a card number that fails its checksum, a SKU that doesn't exist. An acceptance check runs before the message is dispatched, and either accepts it or rejects it with a response, typically a `4xx` problem details response:

```csharp
class SkuExists(IProductCatalog catalog) : IAcceptanceCheck<PlaceOrder>
{
    public async ValueTask<Acceptance> Check(PlaceOrder command, HttpContext httpContext) =>
        await catalog.Exists(command.Sku, httpContext.RequestAborted)
            ? Acceptance.Accept()
            : Acceptance.Reject(TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The product doesn't exist."));
}

app.MapCommand<PlaceOrder>("/orders").WithAcceptanceCheck<SkuExists>();
```

Dependencies come in through the constructor, from the request services. Checks run in the order they're added, and the first rejection stops the request. A check is tied to its message type, so adding it to the wrong endpoint doesn't compile.

Checks are for validating requests, not for business rules. Whether a customer can place this order, or an order can still be cancelled, is the message handler's decision: it runs with retries, and decides on the state at the time the message is handled. Checks are available on accept-only endpoints only; atomic endpoints are code already, and the same split applies there.

### Retries

Heyi doesn't retry failed sends. [NServiceBus.Extensions.DispatchRetries](https://github.com/mauroservienti/NServiceBus.Extensions.DispatchRetries) is one option for retrying them on the server.

## Idempotency at the edge

Clients retry, proxies retry, users double-click. When a request carries an [`Idempotency-Key`](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) header, Heyi turns it into the identity of the outgoing messages: the n-th message of a given type sent while handling the request gets the `{key}/{type}/{n}` message ID, where `{type}` is a short hash of the message type's full name. A retried request produces messages with the same IDs, and receivers can deduplicate them, for example with the outbox.

```http
POST /orders/1234/cancellations
Idempotency-Key: "8e03978e-40d5-43e8-bc93-6894a57f9324"
```

- Works for atomic and accept-only endpoints.
- Messages are counted per type, so the IDs stay deterministic when different message types are sent concurrently, as ServiceComposer's composition handlers do.
- Sends with an explicit destination (`SetDestination`) are counted per destination too, and get the `{key}/{type}/{destination}/{n}` message ID: two handlers sending the same message type to different backends don't swap IDs on retries.
- Otherwise, messages of the same type must be sent in the same order on every retry. When concurrent code sends or publishes the same message type without a destination, for example two composition handlers publishing the same event, inject `IOutgoingMessages<TSender>` instead: each sender counts its own messages, and gets the `{key}/{sender}/{type}/{n}` message ID.

```csharp
class SalesCartHandler(IOutgoingMessages<SalesCartHandler> messages) : ICompositionRequestsHandler
```

- A message ID set explicitly in the options wins, and still counts as a position.
- The key is client input, and two clients sending the same key would get the same message IDs: receivers would deduplicate one client's messages away. To scope keys per client, configure a scope, which is hashed and follows the key in every message ID (`{key}/{scope}/...`):

```csharp
builder.Services.AddHeyi(options => options
    .OpenSessionOptions(() => new SqlPersistenceOpenSessionOptions())
    .ScopeIdempotencyKeys(httpContext => httpContext.User.FindFirst("sub")?.Value));
```

  A `null` scope, for example for anonymous requests, leaves the key unscoped.
- An empty key, or more than one, is a `400` problem details response, and nothing is sent.

Heyi doesn't store responses or deduplicate requests: a retried atomic request runs the endpoint again, so the database must reject the duplicate, for example with a unique constraint.

## Observability

Heyi emits OpenTelemetry traces and metrics, at almost no cost until they're collected. Subscribe to them alongside NServiceBus's own:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("NServiceBus.Core")
        .AddSource(HeyiTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics
        .AddMeter("NServiceBus.TransactionalSession")
        .AddMeter(HeyiTelemetry.MeterName));
```

NServiceBus traces sends and publishes, and measures commits. Heyi adds what only it knows: why a session rolled back, and what acceptance checks decided.

| Span | When | Tags |
|---|---|---|
| `Heyi.TransactionalSession` | From opening the session to committing or rolling it back. The endpoint runs inside it, so its sends and publishes are its children. | `heyi.session.outcome`: `committed`, `rolled_back`, `commit_failed`, or `cancelled`<br>`heyi.session.rollback_reason`: `unsuccessful_result` or `exception`<br>`heyi.idempotency_key.present` |
| `Heyi.AcceptanceCheck` | Around each acceptance check. | `heyi.acceptance_check.type`<br>`heyi.acceptance_check.outcome`: `accepted`, `rejected`, or `failed` |

| Metric | Unit | Tags |
|---|---|---|
| `heyi.session.duration` | `s` | `heyi.session.outcome`, `heyi.session.rollback_reason` |
| `heyi.acceptance_check.duration` | `s` | `heyi.acceptance_check.type`, `heyi.acceptance_check.outcome` |

- Exceptions and failed commits set the span status to error and record the exception. A rollback because of an unsuccessful result, such as a `409`, isn't an error: the endpoint decided.
- `cancelled` means the client hung up during the commit. The transactional session still completes it atomically, one way or the other, but Heyi can't tell which.
- Requests that don't open a session, such as safe methods and opted-out endpoints, aren't traced.
- The idempotency key itself is never recorded: it's client input.

## Requirements

- .NET 10
- NServiceBus 10 with the transactional session
- NServiceBus SQL persistence, for the EF Core integration

## License

[MIT](https://github.com/mauroservienti/Heyi/blob/main/LICENSE)

---

Icon [together](https://thenounproject.com/browse/icons/term/together/) by Andika Cahya Fitriani from the Noun Project (CC BY 3.0)
