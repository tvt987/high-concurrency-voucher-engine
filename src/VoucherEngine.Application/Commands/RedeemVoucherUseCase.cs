using VoucherEngine.Application.DTOs;
using VoucherEngine.Application.Interfaces;
using VoucherEngine.Domain.Entities;
using VoucherEngine.Domain.Exceptions;

namespace VoucherEngine.Application.Commands;

public class RedeemVoucherUseCase
{
    private const int MaxOptimisticCasRetries = 10;

    private readonly IVoucherRepository _repository;
    private readonly IDistributedCacheService _cacheService;

    public RedeemVoucherUseCase(
        IVoucherRepository repository,
        IDistributedCacheService cacheService)
    {
        _repository = repository;
        _cacheService = cacheService;
    }

    public async Task<RedeemVoucherResponse> ExecuteAsync(
        RedeemVoucherRequest request,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;

        // Stage 0: Fast Idempotency Check (Anti-Double-Spending on network retry)
        var existingRecord = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken);
        if (existingRecord != null)
        {
            return IdempotencyHitResponse(request.VoucherCode, existingRecord);
        }

        // Stage 1: Fast-Path Quota Check via Redis Atomic Lua / Thread-Safe Cache (< 2ms).
        // When currentQuota < 0, neither Redis nor InMemory decremented the counter, so we
        // MUST NOT call RollbackQuotaAtomicAsync here (calling INCR would inject phantom quota).
        long currentQuota = await _cacheService.DecrementQuotaAtomicAsync(request.VoucherCode, cancellationToken);
        if (currentQuota < 0)
        {
            return new RedeemVoucherResponse(
                Success: false,
                Code: request.VoucherCode,
                DiscountApplied: 0,
                Message: "Flash-sale quota depleted (Fast-path Cache Rejection).",
                TimestampUtc: nowUtc
            );
        }

        // Stage 2: Database Optimistic Concurrency Persistence via Dapper (RowVersion CAS + retry on contention)
        for (int attempt = 0; attempt < MaxOptimisticCasRetries; attempt++)
        {
            // Re-check idempotency in case a parallel thread with the same key committed first
            var raceRecord = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken);
            if (raceRecord != null)
            {
                await _cacheService.RollbackQuotaAtomicAsync(request.VoucherCode, cancellationToken);
                return IdempotencyHitResponse(request.VoucherCode, raceRecord);
            }

            var voucher = await _repository.GetByCodeAsync(request.VoucherCode, cancellationToken);
            if (voucher == null)
            {
                await _cacheService.RollbackQuotaAtomicAsync(request.VoucherCode, cancellationToken);
                return new RedeemVoucherResponse(false, request.VoucherCode, 0, "Voucher not found.", nowUtc);
            }

            try
            {
                voucher.CanRedeem(request.OrderAmount, nowUtc);
            }
            catch (VoucherDomainException ex)
            {
                await _cacheService.RollbackQuotaAtomicAsync(request.VoucherCode, cancellationToken);
                return new RedeemVoucherResponse(false, request.VoucherCode, 0, ex.Message, nowUtc);
            }

            var redemptionRecord = new RedemptionRecord(
                id: Guid.NewGuid(),
                voucherId: voucher.Id,
                customerId: request.CustomerId,
                orderId: request.OrderId,
                storeId: request.StoreId,
                idempotencyKey: request.IdempotencyKey,
                orderAmount: request.OrderAmount,
                appliedDiscount: voucher.DiscountAmount,
                redeemedAtUtc: nowUtc
            );

            bool committed = await _repository.TryRedeemOptimisticAsync(
                voucher.Id,
                voucher.RowVersion,
                redemptionRecord,
                cancellationToken
            );

            if (committed)
            {
                return new RedeemVoucherResponse(
                    Success: true,
                    Code: request.VoucherCode,
                    DiscountApplied: voucher.DiscountAmount,
                    Message: "Voucher redeemed successfully with optimistic lock verification.",
                    TimestampUtc: nowUtc,
                    RedemptionId: redemptionRecord.Id.ToString()
                );
            }

            // Commit failed either due to duplicate IdempotencyKey race or RowVersion contention.
            var postFailIdem = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken);
            if (postFailIdem != null)
            {
                await _cacheService.RollbackQuotaAtomicAsync(request.VoucherCode, cancellationToken);
                return IdempotencyHitResponse(request.VoucherCode, postFailIdem);
            }

            await Task.Yield();
        }

        await _cacheService.RollbackQuotaAtomicAsync(request.VoucherCode, cancellationToken);
        return new RedeemVoucherResponse(
            Success: false,
            Code: request.VoucherCode,
            DiscountApplied: 0,
            Message: "Concurrency collision: Voucher state was updated by a parallel transaction. Please retry.",
            TimestampUtc: nowUtc
        );
    }

    private static RedeemVoucherResponse IdempotencyHitResponse(string code, RedemptionRecord record) =>
        new(
            Success: true,
            Code: code,
            DiscountApplied: record.AppliedDiscount,
            Message: "Voucher already redeemed for this transaction (Idempotency Hit).",
            TimestampUtc: record.RedeemedAtUtc,
            RedemptionId: record.Id.ToString()
        );
}
