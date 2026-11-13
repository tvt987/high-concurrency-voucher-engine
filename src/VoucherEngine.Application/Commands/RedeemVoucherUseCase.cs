using VoucherEngine.Application.DTOs;
using VoucherEngine.Application.Interfaces;
using VoucherEngine.Domain.Entities;
using VoucherEngine.Domain.Exceptions;

namespace VoucherEngine.Application.Commands;

public class RedeemVoucherUseCase
{
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

        // Stage 0: Strict Idempotency Check (Anti-Double-Spending on network retry)
        var existingRecord = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken);
        if (existingRecord != null)
        {
            return new RedeemVoucherResponse(
                Success: true,
                Code: request.VoucherCode,
                DiscountApplied: existingRecord.AppliedDiscount,
                Message: "Voucher already redeemed for this transaction (Idempotency Hit).",
                TimestampUtc: existingRecord.RedeemedAtUtc,
                RedemptionId: existingRecord.Id.ToString()
            );
        }

        // Stage 1: Fast-Path Quota Check via Redis Atomic Operation (< 2ms)
        // Eliminates 90% of requests when quota is exhausted without hitting SQL Server
        long currentQuota = await _cacheService.DecrementQuotaAtomicAsync(request.VoucherCode, cancellationToken);
        if (currentQuota < 0)
        {
            await _cacheService.RollbackQuotaAtomicAsync(request.VoucherCode, cancellationToken);
            return new RedeemVoucherResponse(
                Success: false,
                Code: request.VoucherCode,
                DiscountApplied: 0,
                Message: "Flash-sale quota depleted (Fast-path Cache Rejection).",
                TimestampUtc: nowUtc
            );
        }

        // Stage 2: Database Optimistic Concurrency Persistence via Dapper
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

        bool success = await _repository.TryRedeemOptimisticAsync(
            voucher.Id,
            voucher.RowVersion,
            redemptionRecord,
            cancellationToken
        );

        if (!success)
        {
            await _cacheService.RollbackQuotaAtomicAsync(request.VoucherCode, cancellationToken);
            return new RedeemVoucherResponse(
                Success: false,
                Code: request.VoucherCode,
                DiscountApplied: 0,
                Message: "Concurrency collision: Voucher state was updated by a parallel transaction. Please retry.",
                TimestampUtc: nowUtc
            );
        }

        return new RedeemVoucherResponse(
            Success: true,
            Code: request.VoucherCode,
            DiscountApplied: voucher.DiscountAmount,
            Message: "Voucher redeemed successfully with optimistic lock verification.",
            TimestampUtc: nowUtc,
            RedemptionId: redemptionRecord.Id.ToString()
        );
    }
}
