using System.Collections.Concurrent;

namespace Heyi.AcceptanceTests;

public record OrderPlaced(Guid OrderId) : IEvent;

public class OrderPlacedHandler : IHandleMessages<OrderPlaced>
{
    static readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> received = new();

    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        Received(message.OrderId).TrySetResult(context.MessageId);
        return Task.CompletedTask;
    }

    /// <summary>Completes with the message ID when the event for the order is handled.</summary>
    public static TaskCompletionSource<string> Received(Guid orderId) =>
        received.GetOrAdd(orderId, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
}
