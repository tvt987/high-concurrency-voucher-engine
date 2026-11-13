using FluentAssertions;
using VoucherEngine.Domain.Entities;
using VoucherEngine.Domain.Exceptions;
using Xunit;

namespace VoucherEngine.UnitTests;

public class VoucherDomainTests
{
    [Fact]
    public void Voucher_Should_Throw_When_Expired()
    {
        var voucher = new Voucher(
            id: Guid.NewGuid(),
            code: "EXPIRED50",
            campaignId: "CAMP1",
            discountAmount: 50000m,
            minimumSpend: 100000m,
            totalQuota: 100,
            startsAtUtc: DateTime.UtcNow.AddDays(-10),
            expiresAtUtc: DateTime.UtcNow.AddDays(-1)
        );

        Action act = () => voucher.CanRedeem(150000m, DateTime.UtcNow);

        act.Should().Throw<VoucherDomainException>()
            .WithMessage("*expired*");
    }

    [Fact]
    public void Voucher_Should_Throw_When_OrderAmount_Below_MinimumSpend()
    {
        var voucher = new Voucher(
            id: Guid.NewGuid(),
            code: "MINSPEND50",
            campaignId: "CAMP1",
            discountAmount: 50000m,
            minimumSpend: 200000m,
            totalQuota: 100,
            startsAtUtc: DateTime.UtcNow.AddDays(-1),
            expiresAtUtc: DateTime.UtcNow.AddDays(5)
        );

        Action act = () => voucher.CanRedeem(150000m, DateTime.UtcNow);

        act.Should().Throw<VoucherDomainException>()
            .WithMessage("*Minimum order amount is 200000*");
    }

    [Fact]
    public void Voucher_Should_Decrement_Quota_On_Valid_Redeem()
    {
        var voucher = new Voucher(
            id: Guid.NewGuid(),
            code: "VALID50",
            campaignId: "CAMP1",
            discountAmount: 50000m,
            minimumSpend: 100000m,
            totalQuota: 5,
            startsAtUtc: DateTime.UtcNow.AddDays(-1),
            expiresAtUtc: DateTime.UtcNow.AddDays(5)
        );

        voucher.Redeem(150000m, DateTime.UtcNow);

        voucher.RemainingQuota.Should().Be(4);
    }
}
