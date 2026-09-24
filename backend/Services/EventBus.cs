using System.Collections.Concurrent;
using StackExchange.Redis;

namespace LoveCapsule.Api.Services;

// Abstracts the cross-process transport MemoryEventPublisher uses to fan out
// live events. Production uses Redis Pub/Sub; tests use the in-memory fake so
// the suite never needs a real Redis instance.
public interface IEventBus
{
    Task PublishAsync(string channel, string message);
    void Subscribe(string channel, Action<string> onMessage);
    void Unsubscribe(string channel);
}

public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<string, Action<string>> _handlers = new();

    public Task PublishAsync(string channel, string message)
    {
        if (_handlers.TryGetValue(channel, out var handler))
        {
            handler(message);
        }

        return Task.CompletedTask;
    }

    public void Subscribe(string channel, Action<string> onMessage)
    {
        _handlers[channel] = onMessage;
    }

    public void Unsubscribe(string channel)
    {
        _handlers.TryRemove(channel, out _);
    }
}

public sealed class RedisEventBus : IEventBus
{
    private readonly IConnectionMultiplexer _redis;

    public RedisEventBus(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task PublishAsync(string channel, string message)
    {
        await _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(channel), message);
    }

    public void Subscribe(string channel, Action<string> onMessage)
    {
        _redis.GetSubscriber().Subscribe(RedisChannel.Literal(channel), (_, value) => onMessage(value!));
    }

    public void Unsubscribe(string channel)
    {
        _redis.GetSubscriber().Unsubscribe(RedisChannel.Literal(channel));
    }
}
