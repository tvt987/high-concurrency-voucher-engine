using VoucherEngine.Domain.Enums;
using VoucherEngine.Domain.Exceptions;

namespace VoucherEngine.Domain.Entities;

public class Voucher
{
    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string CampaignId { get; private set; } = string.Empty;
    public decimal DiscountAmount { get; private set; }
    public decimal MinimumSpend { get; private set; }
    public int TotalQuota { get; private set; }
    public int RemainingQuota { get; private set; }
    public VoucherStatus Status { get; private set; }
    public DateTime StartsAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    // For Dapper materialization
    protected Voucher() { }

    public Voucher(
        Guid id,
        string code,
        string campaignId,
        decimal discountAmount,
        decimal minimumSpend,
        int totalQuota,
        DateTime startsAtUtc,
        DateTime expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new VoucherDomainException("INVALID_CODE", "Voucher code cannot be empty.");
        if (discountAmount <= 0)
            throw new VoucherDomainException("INVALID_DISCOUNT", "Discount must be greater than zero.");
        if (totalQuota <= 0)
            throw new VoucherDomainException("INVALID_QUOTA", "Quota must be greater than zero.");
        if (expiresAtUtc <= startsAtUtc)
            throw new VoucherDomainException("INVALID_DATE_RANGE", "Expiration date must be after start date.");

        Id = id;
        Code = code.Trim().ToUpperInvariant();
        CampaignId = campaignId;
        DiscountAmount = discountAmount;
        MinimumSpend = minimumSpend;
        TotalQuota = totalQuota;
        RemainingQuota = totalQuota;
        Status = VoucherStatus.Active;
        StartsAtUtc = startsAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public void CanRedeem(decimal orderAmount, DateTime nowUtc)
    {
        if (Status != VoucherStatus.Active)
            throw new VoucherDomainException("VOUCHER_INACTIVE", $"Voucher is not active. Status: {Status}.");

        if (nowUtc < StartsAtUtc)
            throw new VoucherDomainException("CAMPAIGN_NOT_STARTED", "Voucher campaign has not started yet.");

        if (nowUtc > ExpiresAtUtc)
        {
            Status = VoucherStatus.Expired;
            throw new VoucherDomainException("VOUCHER_EXPIRED", "Voucher has expired.");
        }

        if (orderAmount < MinimumSpend)
            throw new VoucherDomainException("INSUFFICIENT_ORDER_AMOUNT", $"Minimum order amount is {MinimumSpend}. Current: {orderAmount}.");

        if (RemainingQuota <= 0)
        {
            Status = VoucherStatus.Depleted;
            throw new VoucherDomainException("QUOTA_DEPLETED", "Voucher quota has been completely depleted.");
        }
    }

    public void Redeem(decimal orderAmount, DateTime nowUtc)
    {
        CanRedeem(orderAmount, nowUtc);

        RemainingQuota--;
        if (RemainingQuota == 0)
        {
            Status = VoucherStatus.Depleted;
        }
    }
}
