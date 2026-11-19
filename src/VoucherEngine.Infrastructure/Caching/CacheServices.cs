using StackExchange.Redis;
using VoucherEngine.Application.Interfaces;

namespace VoucherEngine.Infrastructure.Caching;

/// <summary>
/// Thread-safe in-memory distributed cache matching Redis atomic Lua semantics:
/// returns -1 without mutating state when key is missing or quota is already <= 0.
/// </summary>
public class InMemoryDistributedCacheService : IDistributedCacheService
{
    private readonly Dictionary<string, long> _quotas = new();
    private readonly object _lock = new();

    public Task<bool> SetVoucherQuotaCacheAsync(string voucherCode, int remainingQuota, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var key = voucherCode.Trim().ToUpperInvariant();
        lock (_lock)
        {
            _quotas[key] = remainingQuota;
        }
        return Task.FromResult(true);
    }

    public Task<long> DecrementQuotaAtomicAsync(string voucherCode, CancellationToken cancellationToken = default)
    {
        var key = voucherCode.Trim().ToUpperInvariant();
        lock (_lock)
        {
            if (!_quotas.TryGetValue(key, out long current) || current <= 0)
            {
                return Task.FromResult(-1L);
            }

            long updated = current - 1;
            _quotas[key] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task RollbackQuotaAtomicAsync(string voucherCode, CancellationToken cancellationToken = default)
    {
        var key = voucherCode.Trim().ToUpperInvariant();
        lock (_lock)
        {
            if (_quotas.TryGetValue(key, out long current))
            {
                _quotas[key] = current + 1;
            }
            else
            {
                _quotas[key] = 1;
            }
        }
        return Task.CompletedTask;
    }
}

public class RedisDistributedCacheService : IDistributedCacheService
{
    private readonly IDatabase _db;

    // Fast-path Lua script: Atomically check and decrement ONLY if quota > 0
    private const string AtomicDecrScript = @"
        local current = redis.call('GET', KEYS[1])
        if not current then
            return -1
        end
        local val = tonumber(current)
        if not val or val <= 0 then
            return -1
        else
            return redis.call('DECR', KEYS[1])
        end";

    public RedisDistributedCacheService(IConnectionMultiplexer redis)
    {
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
