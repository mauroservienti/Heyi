# Heyi design

Heyi (合一, héyī, "to become one") turns ASP.NET Core endpoints into handler-shaped endpoints: an endpoint stores data and sends NServiceBus messages as one atomic operation. It builds on the [NServiceBus transactional session](https://docs.particular.net/nservicebus/transactional-session/) and the outbox; Heyi adds the programming model, not the guarantees.

## Setup

NServiceBus comes first, Heyi second. Heyi doesn't configure or host NServiceBus; it relies on an endpoint that's already configured and registered with the standard hosting integration.

1. Configure the NServiceBus endpoint as usual, including what the transactional session needs: a supported persistence with the transactional session enabled, and the outbox.

   ```csharp
   var endpointConfiguration = new EndpointConfiguration("Sales.Api");
   endpointConfiguration.UseTransport(new RabbitMQTransport(...))
       .RouteToEndpoint(typeof(CancelOrder), "Sales");

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
   builder.Services.AddDbContext<RequestsDbContext>(options => options.UseSqlServer(connectionString));
   builder.Services.AddHeyiDbContext<RequestsDbContext>();

   var app = builder.Build();

   app.MapPost("/orders/{orderId}/cancellations", ...).WithTransactionalSession();
   app.MapCommand<PlaceOrder>("/orders");
   ```

Heyi checks its prerequisites when the endpoints are built, and fails fast with a clear message: no `ITransactionalSession` registered (the transactional session isn't enabled), or no open session options for atomic endpoints. Accept-only endpoints only need the endpoint itself, since they send through `IMessageSession` without the transactional session; without it, they fail fast the same way.

## Scope for v1

Three features, all in v1:

1. Atomic endpoints
2. Accept-only endpoints
3. Idempotency at the edge

### 1. Atomic endpoints

Atomic endpoints are for task-based applications: the endpoint records the task users follow, such as a cancellation request, and sends the message that starts the work, as one operation. The endpoint doesn't run business logic; a message handler does. For everything else, accept-only endpoints are the default.

Minimal API endpoints opt in with an endpoint filter:

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

Rules:

- The filter opens the `ITransactionalSession` before the endpoint runs.
- The session is committed after the endpoint returns and before the response is written. A failed commit must never produce a success response; it becomes a `500` problem details response.
- Only success status codes (`2xx`) commit. An exception or a `4xx`/`5xx` result disposes the session, which rolls back data and messages.
- `IOutgoingMessages` is a request-scoped facade over the open session (`Send`, `SendLocal`, `Publish`), asynchronous like the rest of the NServiceBus API, so errors surface where the message is sent. Endpoint code never opens, commits, or disposes anything, and using `IOutgoingMessages` outside a transactional endpoint throws.
- The status code comes from the returned result, including nested results such as `Results<Created, BadRequest>`, and falls back to the response status code for plain return values.
- Route groups work too: `app.MapGroup("/orders/{orderId}").WithTransactionalSession()`.

Persistence-specific open options are registered once, with a per-endpoint override:

```csharp
builder.Services.AddHeyi(options =>
    options.OpenSessionOptions(() => new SqlPersistenceOpenSessionOptions()));

app.MapPost("/orders/{orderId}/cancellations", ...)
    .WithTransactionalSession(() => new SqlPersistenceOpenSessionOptions { MaximumCommitDuration = TimeSpan.FromSeconds(30) });
```

### 2. Accept-only endpoints

The HTTP edge accepts a command or an event and returns `202 Accepted`. No database, no business logic: a regular message handler does the work.

```csharp
app.MapCommand<PlaceOrder>("/orders");          // Send, 202 Accepted
app.MapEvent<OrderRequested>("/order-requests"); // Publish, 202 Accepted
```

- The request body is bound to the message type; a missing or malformed body is a `400`, and nothing is sent.
- `MapCommand` and `MapEvent` return the `RouteHandlerBuilder`, so validation filters, authorization, and other conventions are added as usual.
- Routing follows the endpoint's NServiceBus routing configuration; no destinations in the HTTP mapping.
- The response can carry a location to poll, for example `MapCommand<PlaceOrder>("/orders", location: command => $"/orders/{command.OrderId}")`.
- `.WithAcceptanceCheck<TCheck>()` runs an `IAcceptanceCheck<TMessage>` before the message is dispatched, for example against a read model. It returns `Acceptance.Accept()` or `Acceptance.Reject(response)`, explicit rather than a `null` meaning "go ahead"; on a rejection the response is returned and nothing is sent. Checks are classes, so their dependencies come in through the constructor, from the request services, and they can be tested on their own. They run in the order they're added, as endpoint filters that find the message by type, not by the handler's argument positions. A check whose message type the endpoint doesn't accept fails when the endpoints are built. The check isn't atomic with the send and reads state that can change, so the message handler still decides; upfront checks make rejections rare, as described in ["What if my command was rejected?"](https://milestone.topics.it/2023/06/27/reject-commands.html).
- If sending fails, the endpoint returns a `500` problem details response. Heyi doesn't retry sends itself. One option for retrying them on the server is [NServiceBus.Extensions.DispatchRetries](https://github.com/mauroservienti/NServiceBus.Extensions.DispatchRetries), configured on the NServiceBus endpoint: these endpoints send through `IMessageSession`, so the send goes through the outgoing pipeline it hooks into. It doesn't apply to atomic endpoints, whose commit sends the transactional session's dispatch message directly through the transport.

### 3. Idempotency at the edge

An `Idempotency-Key` request header (see the [IETF draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/)) becomes the identity of the outgoing messages, so a retried request produces messages with the same IDs, and receivers can deduplicate them.

- One key, many messages: the n-th message sent while handling the request gets the `{key}/{n}` message ID, counting from `0`. The IDs are deterministic as long as the endpoint sends its messages in the same order, so avoid sending them concurrently.
- A message ID set explicitly in the send or publish options wins. It still counts as a position, so it doesn't shift the others.
- The key is a structured field string, so it's quoted (`Idempotency-Key: "8e03978e"`); unquoted keys are accepted too. An empty key, or more than one, is a `400` problem details response, before the session opens or anything is sent.
- Without the header, message IDs are left to NServiceBus.
- Applies to both atomic and accept-only endpoints.
- v1 stamps message IDs only. The transactional session doesn't deduplicate incoming requests, so a retried atomic request still runs the endpoint again; the database must reject the duplicate (for example, with a unique constraint), as described in ["Idempotent, you must be"](https://milestone.topics.it/2026/11/26/idempotency-deep-dive.html).
- Later: store the key with the first response and replay it, which the IETF draft describes. Out of scope for v1.

## Data access

The endpoint's data access must share the transactional session's connection and transaction. For EF Core with SQL persistence, Heyi does what the [Particular sample](https://docs.particular.net/samples/transactional-session/aspnetcore-webapi/) does by hand. The context is registered as usual, then Heyi is told about it:

```csharp
builder.Services.AddDbContext<RequestsDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddHeyiDbContext<RequestsDbContext>();
```

- Inside a transactional endpoint, right after opening the session, Heyi switches the request's context to the session's `ISqlStorageSession` connection and transaction, and registers `SaveChangesAsync` to run right before the commit.
- Because Heyi attaches the request-scoped instance instead of creating the context itself, it doesn't matter when the context was resolved. A context injected as an endpoint parameter, resolved before the session is opened, still joins it.
- Outside a transactional endpoint, the context works as registered, with its own connection.
- With a persistence other than SQL persistence, transactional endpoints fail with a clear message.

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
