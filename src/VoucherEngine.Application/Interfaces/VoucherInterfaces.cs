using VoucherEngine.Domain.Entities;

namespace VoucherEngine.Application.Interfaces;

public interface IVoucherRepository
{
    Task<Voucher?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
    Task<RedemptionRecord?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    Task<bool> TryRedeemOptimisticAsync(Guid voucherId, byte[] expectedRowVersion, RedemptionRecord record, CancellationToken cancellationToken = default);
}

public interface IDistributedCacheService
{
    Task<bool> SetVoucherQuotaCacheAsync(string voucherCode, int remainingQuota, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task<long> DecrementQuotaAtomicAsync(string voucherCode, CancellationToken cancellationToken = default);
    Task RollbackQuotaAtomicAsync(string voucherCode, CancellationToken cancellationToken = default);
}
