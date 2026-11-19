using System.Collections.Concurrent;
using System.Threading;
using VoucherEngine.Application.Interfaces;
using VoucherEngine.Domain.Entities;

namespace VoucherEngine.Infrastructure.Persistence;

public class InMemoryVoucherRepository : IVoucherRepository
{
    private readonly ConcurrentDictionary<string, Voucher> _vouchersByCode = new();
    private readonly ConcurrentDictionary<string, RedemptionRecord> _recordsByIdempotency = new();
    private readonly object _lock = new();
    private long _versionCounter = 1;

    public void Seed(Voucher voucher)
    {
        if (voucher.RowVersion.Length == 0)
        {
            long initialVersion = Interlocked.Increment(ref _versionCounter);
            typeof(Voucher).GetProperty(nameof(Voucher.RowVersion))!
                .SetValue(voucher, BitConverter.GetBytes(initialVersion));
        }
        _vouchersByCode[voucher.Code] = voucher;
    }

    public Task<Voucher?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        _vouchersByCode.TryGetValue(code.Trim().ToUpperInvariant(), out var voucher);
        return Task.FromResult(voucher);
    }

    public Task<RedemptionRecord?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        _recordsByIdempotency.TryGetValue(idempotencyKey, out var record);
        return Task.FromResult<RedemptionRecord?>(record);
    }

    public Task<bool> TryRedeemOptimisticAsync(
        Guid voucherId,
        byte[] expectedRowVersion,
        RedemptionRecord record,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Enforce unique IdempotencyKey inside the critical section before mutating quota
            if (_recordsByIdempotency.ContainsKey(record.IdempotencyKey))
            {
                return Task.FromResult(false);
            }

            var voucher = _vouchersByCode.Values.FirstOrDefault(v => v.Id == voucherId);
            if (voucher == null) return Task.FromResult(false);

            if (!voucher.RowVersion.SequenceEqual(expectedRowVersion))
            {
                return Task.FromResult(false);
            }

            voucher.Redeem(record.OrderAmount, DateTime.UtcNow);

            // Strictly monotonic version avoids same-tick timestamp collisions under tight loops
            long nextVersion = Interlocked.Increment(ref _versionCounter);
            typeof(Voucher).GetProperty(nameof(Voucher.RowVersion))!
                .SetValue(voucher, BitConverter.GetBytes(nextVersion));

            _recordsByIdempotency[record.IdempotencyKey] = record;
            return Task.FromResult(true);
        }
    }
}
