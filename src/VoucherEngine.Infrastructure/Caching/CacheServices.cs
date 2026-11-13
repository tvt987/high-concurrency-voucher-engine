using System.Collections.Concurrent;
using StackExchange.Redis;
using VoucherEngine.Application.Interfaces;

namespace VoucherEngine.Infrastructure.Caching;

public class InMemoryDistributedCacheService : IDistributedCacheService
{
    private readonly ConcurrentDictionary<string, long> _quotas = new();

    public Task<bool> SetVoucherQuotaCacheAsync(string voucherCode, int remainingQuota, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        _quotas[voucherCode.Trim().ToUpperInvariant()] = remainingQuota;
        return Task.FromResult(true);
    }

    public Task<long> DecrementQuotaAtomicAsync(string voucherCode, CancellationToken cancellationToken = default)
    {
        var key = voucherCode.Trim().ToUpperInvariant();
        var val = _quotas.AddOrUpdate(key, -1, (_, old) => old - 1);
        return Task.FromResult(val);
    }

    public Task RollbackQuotaAtomicAsync(string voucherCode, CancellationToken cancellationToken = default)
    {
        var key = voucherCode.Trim().ToUpperInvariant();
        _quotas.AddOrUpdate(key, 1, (_, old) => old + 1);
        return Task.CompletedTask;
    }
}

public class RedisDistributedCacheService : IDistributedCacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    // Fast-path Lua script: Atomically check and decrement only if quota > 0
    private const string AtomicDecrScript = @"
        local current = redis.call('GET', KEYS[1])
        if not current then
            return -1
        end
        local val = tonumber(current)
        if val <= 0 then
            return -1
        else
            return redis.call('DECR', KEYS[1])
        end";

    public RedisDistributedCacheService(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _db = redis.GetDatabase();
    }

    public async Task<bool> SetVoucherQuotaCacheAsync(string voucherCode, int remainingQuota, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var key = $"voucher:quota:{voucherCode.Trim().ToUpperInvariant()}";
        return await _db.StringSetAsync(key, remainingQuota, ttl);
    }

    public async Task<long> DecrementQuotaAtomicAsync(string voucherCode, CancellationToken cancellationToken = default)
    {
        var key = $"voucher:quota:{voucherCode.Trim().ToUpperInvariant()}";
        var result = await _db.ScriptEvaluateAsync(AtomicDecrScript, new RedisKey[] { key });
        return (long)result;
    }

    public async Task RollbackQuotaAtomicAsync(string voucherCode, CancellationToken cancellationToken = default)
    {
        var key = $"voucher:quota:{voucherCode.Trim().ToUpperInvariant()}";
        await _db.StringIncrementAsync(key);
    }
}
