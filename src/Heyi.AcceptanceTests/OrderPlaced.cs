using System.Collections.Concurrent;

namespace Heyi.AcceptanceTests;

public record OrderPlaced(Guid OrderId) : IEvent;

public class OrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    static readonly ConcurrentDictionary<Guid, TaskCompletionSource> received = new();

    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        Received(message.OrderId).TrySetResult();
        return Task.CompletedTask;
    }

    public static TaskCompletionSource Received(Guid orderId) =>
        received.GetOrAdd(orderId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
