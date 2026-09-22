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

    public MemoryEventSubscription Subscribe(int userId)
    {
        var subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<MemoryEvent>();
        var userSubscriptions = _subscribers.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Channel<MemoryEvent>>());
        userSubscriptions[subscriptionId] = channel;
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

        if (userSubscriptions.IsEmpty)
        {
            _subscribers.TryRemove(userId, out _);
        }
    }

    public async Task PublishAsync(IEnumerable<OutboxEvent> outboxEvents)
    {
        foreach (var outboxEvent in outboxEvents)
        {
            if (_subscribers.TryGetValue(outboxEvent.RecipientUserId, out var userSubscriptions))
            {
                var memoryEvent = new MemoryEvent(outboxEvent.Id, outboxEvent.MemoryId, outboxEvent.EventType);
                foreach (var channel in userSubscriptions.Values)
                {
                    await channel.Writer.WriteAsync(memoryEvent);
                }
            }
        }
    }

    public static string ToSseData(MemoryEvent memoryEvent)
    {
        return JsonSerializer.Serialize(memoryEvent, SseJsonOptions);
    }
}
