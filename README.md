# Heyi

Heyi (合一, héyī, "to become one") turns ASP.NET Core minimal API endpoints into handler-shaped endpoints: an endpoint stores data and sends NServiceBus messages as one atomic operation.

```csharp
app.MapPost("/orders", async (PlaceOrder request, OrdersDbContext db, IOutgoingMessages messages) =>
{
    var order = Order.From(request);
    db.Orders.Add(order);
    await messages.Publish(new OrderPlaced(order.Id));
    return Results.Created($"/orders/{order.Id}", order);
})
.WithTransactionalSession();
```

The endpoint doesn't open, commit, or dispose anything. Heyi does it around the endpoint, on top of the [NServiceBus transactional session](https://docs.particular.net/nservicebus/transactional-session/) and the outbox. Heyi adds the programming model, not the guarantees.

> [!NOTE]
> Heyi is a work in progress and isn't published on NuGet yet. The three v1 features work today: atomic endpoints with the EF Core integration, accept-only endpoints, and idempotency at the edge. The [design document](docs/design.md) describes the planned v1 scope.

## How it works

- The session is opened before the endpoint runs.
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

builder.Services.AddDbContext<OrdersDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddHeyiDbContext<OrdersDbContext>();
```

Mark endpoints, or whole route groups, as transactional:

```csharp
app.MapPost("/orders", ...).WithTransactionalSession();

var orders = app.MapGroup("/orders").WithTransactionalSession();
orders.MapPost("/{id}/cancel", ...);
```

The open session options can be overridden per endpoint, by passing them to `WithTransactionalSession`.

Heyi checks its prerequisites when the endpoints are built, and fails fast with a clear message if `AddHeyi` is missing, the transactional session isn't enabled, or no open session options are configured.

Web applications are usually send-only endpoints, which requires a [remote processor endpoint](https://docs.particular.net/nservicebus/transactional-session/#remote-processor) with the outbox and the transactional session enabled.

## Accept-only endpoints

Not every request needs to do the work right away. An accept-only endpoint binds the request body to a message, sends or publishes it, and returns `202 Accepted`; a regular message handler does the work:

```csharp
app.MapCommand<PlaceOrder>("/orders");           // Send, 202 Accepted
app.MapEvent<OrderRequested>("/order-requests"); // Publish, 202 Accepted
```

- No database and no transactional session: only the NServiceBus endpoint.
- Destinations come from the NServiceBus routing configuration.
- `location: command => $"/orders/{command.OrderId}"` returns a location to poll.
- A missing or malformed body is a `400`, and nothing is sent. A failed send is a `500` problem details response.
- They return the `RouteHandlerBuilder`, so validation filters, authorization, and other conventions are added as usual.

Heyi doesn't retry failed sends. [NServiceBus.Extensions.DispatchRetries](https://github.com/mauroservienti/NServiceBus.Extensions.DispatchRetries) is one option for retrying them on the server.

## Idempotency at the edge

Clients retry, proxies retry, users double-click. When a request carries an [`Idempotency-Key`](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) header, Heyi turns it into the identity of the outgoing messages: the n-th message sent while handling the request gets the `{key}/{n}` message ID. A retried request produces messages with the same IDs, and receivers can deduplicate them, for example with the outbox.

```http
POST /orders
Idempotency-Key: "8e03978e-40d5-43e8-bc93-6894a57f9324"
```

- Works for atomic and accept-only endpoints.
- The IDs are deterministic as long as the endpoint sends its messages in the same order, so avoid sending them concurrently.
- A message ID set explicitly in the options wins, and still counts as a position.
- An empty key, or more than one, is a `400` problem details response, and nothing is sent.

Heyi doesn't store responses or deduplicate requests: a retried atomic request runs the endpoint again, so the database must reject the duplicate, for example with a unique constraint.

## Requirements

- .NET 10
- NServiceBus 10 with the transactional session
- NServiceBus SQL persistence, for the EF Core integration

## License

[MIT](LICENSE)
