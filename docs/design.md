# Heyi design

Heyi (合一, héyī, "to become one") turns ASP.NET Core endpoints into handler-shaped endpoints: an endpoint stores data and sends NServiceBus messages as one atomic operation. It builds on the [NServiceBus transactional session](https://docs.particular.net/nservicebus/transactional-session/) and the outbox; Heyi adds the programming model, not the guarantees.

## Setup

NServiceBus comes first, Heyi second. Heyi doesn't configure or host NServiceBus; it relies on an endpoint that's already configured and registered with the standard hosting integration.

1. Configure the NServiceBus endpoint as usual, including what the transactional session needs: a supported persistence with the transactional session enabled, and the outbox.

   ```csharp
   var endpointConfiguration = new EndpointConfiguration("Sales.Api");
   endpointConfiguration.UseTransport(new RabbitMQTransport(...))
       .RouteToEndpoint(typeof(PlaceOrder), "Sales");

   var persistence = endpointConfiguration.UsePersistence<SqlPersistence>();
   persistence.SqlDialect<SqlDialect.MsSqlServer>();
   persistence.ConnectionBuilder(() => new SqlConnection(connectionString));
   persistence.EnableTransactionalSession(new TransactionalSessionOptions { ProcessorEndpoint = "Sales.Api.Processor" });

   endpointConfiguration.EnableOutbox();
   endpointConfiguration.SendOnly();

   builder.Services.AddNServiceBusEndpoint(endpointConfiguration);
   ```

2. Add Heyi, then map the endpoints:

   ```csharp
   builder.Services.AddHeyi(options =>
       options.OpenSessionOptions(() => new SqlPersistenceOpenSessionOptions()));
   builder.Services.AddHeyiDbContext<OrdersDbContext>((options, connection) => options.UseSqlServer(connection));

   var app = builder.Build();

   app.MapPost("/orders", ...).WithTransactionalSession();
   app.MapCommand<CancelOrder>("/orders/cancel");
   ```

Heyi checks its prerequisites when the endpoints are built, and fails fast with a clear message: no `ITransactionalSession` registered (the transactional session isn't enabled), or no open session options for atomic endpoints. Accept-only endpoints only need the endpoint itself, since they send through `IMessageSession` without the transactional session.

## Scope for v1

Three features, all in v1:

1. Atomic endpoints
2. Accept-only endpoints
3. Idempotency at the edge

### 1. Atomic endpoints

Minimal API endpoints opt in with an endpoint filter:

```csharp
app.MapPost("/orders", (PlaceOrder request, OrdersDbContext db, IOutgoingMessages messages) =>
{
    var order = Order.From(request);
    db.Orders.Add(order);
    messages.Publish(new OrderPlaced(order.Id));
    return Results.Created($"/orders/{order.Id}", order);
})
.WithTransactionalSession();
```

Rules:

- The filter opens the `ITransactionalSession` before the endpoint runs.
- The session is committed after the endpoint returns and before the response is written. A failed commit must never produce a success response; it becomes a `500` problem details response.
- Only success status codes (`2xx`) commit. An exception or a `4xx`/`5xx` result disposes the session, which rolls back data and messages.
- `IOutgoingMessages` is a request-scoped collector (`Send`, `SendLocal`, `Publish`). Its methods are synchronous: messages are handed to the session only when the endpoint returns a success status code, right before the commit. Endpoint code never opens, commits, or disposes anything, and using `IOutgoingMessages` outside a transactional endpoint throws instead of silently dropping messages.
- The status code comes from the returned result, including nested results such as `Results<Created, BadRequest>`, and falls back to the response status code for plain return values.
- Route groups work too: `app.MapGroup("/orders").WithTransactionalSession()`.

Persistence-specific open options are registered once, with a per-endpoint override:

```csharp
builder.Services.AddHeyi(options =>
    options.OpenSessionOptions(() => new SqlPersistenceOpenSessionOptions()));

app.MapPost("/orders", ...)
    .WithTransactionalSession(() => new SqlPersistenceOpenSessionOptions { MaximumCommitDuration = TimeSpan.FromSeconds(30) });
```

### 2. Accept-only endpoints

The HTTP edge accepts a command or an event and returns `202 Accepted`. No database, no business logic: a regular message handler does the work.

```csharp
app.MapCommand<PlaceOrder>("/orders");          // Send, 202 Accepted
app.MapEvent<OrderRequested>("/order-requests"); // Publish, 202 Accepted
```

- The request body is bound to the message type and validated with the standard minimal API mechanisms.
- Routing follows the endpoint's NServiceBus routing configuration; no destinations in the HTTP mapping.
- The response can carry a location to poll, for example `MapCommand<PlaceOrder>("/orders", accepted: cmd => $"/orders/{cmd.OrderId}")`.
- If sending fails, the endpoint returns a `500`. Heyi doesn't retry sends itself. One option for retrying them on the server is [NServiceBus.Extensions.DispatchRetries](https://github.com/mauroservienti/NServiceBus.Extensions.DispatchRetries), configured on the NServiceBus endpoint: these endpoints send through `IMessageSession`, so the send goes through the outgoing pipeline it hooks into. It doesn't apply to atomic endpoints, whose commit sends the transactional session's dispatch message directly through the transport.

### 3. Idempotency at the edge

An `Idempotency-Key` request header (see the [IETF draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/)) becomes the identity of the outgoing messages, so a retried request produces messages with the same IDs, and receivers can deduplicate them.

- One key, many messages: message IDs are derived deterministically from the key and the message's position in the request, for example `{key}/{index}`.
- Applies to both atomic and accept-only endpoints.
- v1 stamps message IDs only. The transactional session doesn't deduplicate incoming requests, so a retried atomic request still runs the endpoint again; the database must reject the duplicate (for example, with a unique constraint), as described in ["Idempotent, you must be"](https://milestone.topics.it/2026/11/26/idempotency-deep-dive.html).
- Later: store the key with the first response and replay it, which the IETF draft describes. Out of scope for v1.

## Data access

The endpoint's data access must share the transactional session's connection and transaction. For EF Core with SQL persistence, Heyi ships a registration helper that does what the [Particular sample](https://docs.particular.net/samples/transactional-session/aspnetcore-webapi/) does by hand:

```csharp
builder.Services.AddHeyiDbContext<OrdersDbContext>((options, connection) => options.UseSqlServer(connection));
```

- Inside a transactional endpoint, the context uses the session's `ISqlStorageSession` connection and transaction, and `SaveChangesAsync` runs right before the commit.
- Outside one, the context gets its own connection, as usual.

Other persistences expose their own storage sessions; v1 covers SQL persistence and EF Core, and the design keeps the seam open for the others.

## Hosting

- Web applications are usually send-only endpoints, which requires a [remote processor endpoint](https://docs.particular.net/nservicebus/transactional-session/#remote-processor) with the outbox and the transactional session enabled. The docs and samples show both setups.
- Heyi doesn't host NServiceBus; see [Setup](#setup).

## Packaging

One package, `Heyi`, including the EF Core and SQL persistence helper. Splitting it, for example into `Heyi` and `Heyi.EntityFrameworkCore`, can wait until another persistence or data access library needs its own helper.

## Later

- Returning messages as values instead of injecting `IOutgoingMessages`: #4
- Class-based endpoints with attributes: #5

## Testing

- Unit tests for the filter rules: status codes, exceptions, commit failures.
- Acceptance tests with a real broker and database in containers: data and messages committed together, rolled back together, and a crash between commit and dispatch still dispatching.
