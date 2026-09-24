using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using LoveCapsule.Api.Data;

namespace LoveCapsule.Api.Services;

public sealed record MemoryEvent(int Id, int MemoryId, string EventType);
public sealed record MemoryEventSubscription(Guid Id, ChannelReader<MemoryEvent> Reader);

public sealed class MemoryEventPublisher
{
    // Match the camelCase naming ASP.NET Core's MVC JSON formatter uses everywhere else in the API.
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, Channel<MemoryEvent>>> _subscribers = new();
    private readonly object _busSubscriptionLock = new();
    private readonly IEventBus _eventBus;

    public MemoryEventPublisher(IEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    private static string ChannelNameFor(int userId) => $"memory-events:user:{userId}";

    public MemoryEventSubscription Subscribe(int userId)
    {
        var subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<MemoryEvent>();
        var userSubscriptions = _subscribers.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Channel<MemoryEvent>>());
        userSubscriptions[subscriptionId] = channel;

        lock (_busSubscriptionLock)
        {
            // Only the first local subscriber for this user needs to open the bus subscription;
            // additional tabs on this same process just reuse it via the dictionary above.
            if (userSubscriptions.Count == 1)
            {
                _eventBus.Subscribe(ChannelNameFor(userId), message => RelayFromBus(userId, message));
            }
        }

        return new MemoryEventSubscription(subscriptionId, channel.Reader);
    }

    public void Unsubscribe(int userId, Guid subscriptionId)
    {
        if (!_subscribers.TryGetValue(userId, out var userSubscriptions))
        {
            return;
        }

        if (userSubscriptions.TryRemove(subscriptionId, out var channel))
        {
            channel.Writer.TryComplete();
        }

        lock (_busSubscriptionLock)
        {
            if (userSubscriptions.IsEmpty)
            {
                _subscribers.TryRemove(userId, out _);
                _eventBus.Unsubscribe(ChannelNameFor(userId));
            }
        }
    }

    public async Task PublishAsync(IEnumerable<OutboxEvent> outboxEvents)
    {
        foreach (var outboxEvent in outboxEvents)
        {
            var memoryEvent = new MemoryEvent(outboxEvent.Id, outboxEvent.MemoryId, outboxEvent.EventType);
            await _eventBus.PublishAsync(ChannelNameFor(outboxEvent.RecipientUserId), ToSseData(memoryEvent));
        }
    }

    // Runs when this process's event bus subscription receives a message, possibly published
    // by a completely different pod/process; fans it out to this pod's local subscribers only.
    private void RelayFromBus(int userId, string message)
    {
        if (!_subscribers.TryGetValue(userId, out var userSubscriptions))
        {
            return;
        }

        var memoryEvent = JsonSerializer.Deserialize<MemoryEvent>(message, SseJsonOptions);
        if (memoryEvent is null)
        {
            return;
        }

        foreach (var channel in userSubscriptions.Values)
        {
            channel.Writer.TryWrite(memoryEvent);
        }
    }

    public static string ToSseData(MemoryEvent memoryEvent)
    {
        return JsonSerializer.Serialize(memoryEvent, SseJsonOptions);
    }
}
