# Heyi design

Heyi (合一, héyī, "to become one") turns ASP.NET Core endpoints into handler-shaped endpoints: an endpoint stores data and sends NServiceBus messages as one atomic operation. It builds on the [NServiceBus transactional session](https://docs.particular.net/nservicebus/transactional-session/) and the outbox; Heyi adds the programming model, not the guarantees.

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
- `IOutgoingMessages` is a request-scoped facade over the open session (`Send`, `SendLocal`, `Publish`). Endpoint code never opens, commits, or disposes anything.
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
- Heyi doesn't host NServiceBus; it expects the endpoint to be registered with the standard hosting integration.

## Open questions

- Returning messages as values instead of injecting `IOutgoingMessages`, for example `return Http.Created(...).AndPublish(new OrderPlaced(...))`. Easier to test, but needs a custom result type. Not in v1.
- A class-based endpoint style with attributes, closer to ServiceComposer composition handlers.
- Package split: a single `Heyi` package, or `Heyi` plus `Heyi.EntityFrameworkCore`.

## Testing

- Unit tests for the filter rules: status codes, exceptions, commit failures.
- Acceptance tests with a real broker and database in containers: data and messages committed together, rolled back together, and a crash between commit and dispatch still dispatching.
