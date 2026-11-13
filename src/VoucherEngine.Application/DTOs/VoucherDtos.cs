namespace VoucherEngine.Application.DTOs;

public record RedeemVoucherRequest(
    string VoucherCode,
    string CustomerId,
    string OrderId,
    string StoreId,
    decimal OrderAmount,
    string IdempotencyKey
);

public record RedeemVoucherResponse(
    bool Success,
    string Code,
    decimal DiscountApplied,
    string Message,
    DateTime TimestampUtc,
    string? RedemptionId = null
);
