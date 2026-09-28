using System.Collections.Concurrent;
using StackExchange.Redis;

namespace LoveCapsule.Api.Services;

// Cache-aside abstraction over Redis, reusing the same connection MemoryEventPublisher
// uses for pub/sub. Testing uses an in-memory fake so the suite never needs real Redis.
public interface ICacheService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, TimeSpan ttl);
    Task RemoveAsync(string key);
}

public sealed class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer _redis;

    public RedisCacheService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<string?> GetAsync(string key)
    {
        var value = await _redis.GetDatabase().StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl)
    {
        await _redis.GetDatabase().StringSetAsync(key, value, ttl);
    }

    public async Task RemoveAsync(string key)
    {
        await _redis.GetDatabase().KeyDeleteAsync(key);
    }
}

// Testing-only stand-in so the suite never needs a real Redis instance.
public sealed class InMemoryCacheService : ICacheService
{
    private readonly ConcurrentDictionary<string, string> _entries = new();

    public Task<string?> GetAsync(string key)
    {
        return Task.FromResult(_entries.TryGetValue(key, out var value) ? value : null);
    }

    public Task SetAsync(string key, string value, TimeSpan ttl)
    {
        // No expiry simulation needed for tests - invalidation is always explicit.
        _entries[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
