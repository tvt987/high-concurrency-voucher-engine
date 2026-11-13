namespace VoucherEngine.Domain.Entities;

public class RedemptionRecord
{
    public Guid Id { get; private set; }
    public Guid VoucherId { get; private set; }
    public string CustomerId { get; private set; } = string.Empty;
    public string OrderId { get; private set; } = string.Empty;
    public string StoreId { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public decimal OrderAmount { get; private set; }
    public decimal AppliedDiscount { get; private set; }
    public DateTime RedeemedAtUtc { get; private set; }

    protected RedemptionRecord() { }

    public RedemptionRecord(
        Guid id,
        Guid voucherId,
        string customerId,
        string orderId,
        string storeId,
        string idempotencyKey,
        decimal orderAmount,
        decimal appliedDiscount,
        DateTime redeemedAtUtc)
    {
        Id = id;
        VoucherId = voucherId;
        CustomerId = customerId;
        OrderId = orderId;
        StoreId = storeId;
        IdempotencyKey = idempotencyKey;
        OrderAmount = orderAmount;
        AppliedDiscount = appliedDiscount;
        RedeemedAtUtc = redeemedAtUtc;
    }
}
