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
> Heyi is a work in progress and isn't published on NuGet yet. Atomic endpoints and the EF Core integration work today; accept-only endpoints and idempotency at the edge are next. The [design document](docs/design.md) describes the planned v1 scope.

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

## Requirements

- .NET 10
- NServiceBus 10 with the transactional session
- NServiceBus SQL persistence, for the EF Core integration

## License

[MIT](LICENSE)
