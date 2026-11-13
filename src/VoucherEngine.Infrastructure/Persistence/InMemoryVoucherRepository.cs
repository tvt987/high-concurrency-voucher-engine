using System.Collections.Concurrent;
using VoucherEngine.Application.Interfaces;
using VoucherEngine.Domain.Entities;

namespace VoucherEngine.Infrastructure.Persistence;

public class InMemoryVoucherRepository : IVoucherRepository
{
    private readonly ConcurrentDictionary<string, Voucher> _vouchersByCode = new();
    private readonly ConcurrentDictionary<string, RedemptionRecord> _recordsByIdempotency = new();
    private readonly object _lock = new();

    public void Seed(Voucher voucher)
    {
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
            var voucher = _vouchersByCode.Values.FirstOrDefault(v => v.Id == voucherId);
            if (voucher == null) return Task.FromResult(false);

            // Compare RowVersion
            if (!voucher.RowVersion.SequenceEqual(expectedRowVersion))
            {
                // Concurrency violation!
                return Task.FromResult(false);
            }

            voucher.Redeem(record.OrderAmount, DateTime.UtcNow);

            // Bump RowVersion
            var newVersion = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
            typeof(Voucher).GetProperty(nameof(Voucher.RowVersion))!
                .SetValue(voucher, newVersion);

            _recordsByIdempotency[record.IdempotencyKey] = record;
            return Task.FromResult(true);
        }
    }
}
