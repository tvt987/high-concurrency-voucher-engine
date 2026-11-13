using FluentAssertions;
using VoucherEngine.Application.Commands;
using VoucherEngine.Application.DTOs;
using VoucherEngine.Domain.Entities;
using VoucherEngine.Infrastructure.Caching;
using VoucherEngine.Infrastructure.Persistence;
using Xunit;

namespace VoucherEngine.UnitTests;

public class ConcurrencyRedemptionTests
{
    [Fact]
    public async Task Concurrent_Redemptions_Should_Never_Double_Spend_Beyond_Quota()
    {
        // Arrange
        int initialQuota = 25;
        int totalConcurrentUsers = 100;
        string voucherCode = "FLASH25";

        var repo = new InMemoryVoucherRepository();
        var cache = new InMemoryDistributedCacheService();

        var voucher = new Voucher(
            id: Guid.NewGuid(),
            code: voucherCode,
            campaignId: "FLASH_SALE_CONCURRENT",
            discountAmount: 20000m,
            minimumSpend: 50000m,
            totalQuota: initialQuota,
            startsAtUtc: DateTime.UtcNow.AddMinutes(-5),
            expiresAtUtc: DateTime.UtcNow.AddHours(2)
        );

        repo.Seed(voucher);
        await cache.SetVoucherQuotaCacheAsync(voucherCode, initialQuota, TimeSpan.FromHours(1));

        var useCase = new RedeemVoucherUseCase(repo, cache);

        // Act: Fire 100 concurrent parallel redemption attempts
        var tasks = Enumerable.Range(1, totalConcurrentUsers).Select(async i =>
        {
            var request = new RedeemVoucherRequest(
                VoucherCode: voucherCode,
                CustomerId: $"CUST_{i:D4}",
                OrderId: $"ORD_{i:D4}",
                StoreId: $"STORE_{i % 10:D2}",
                OrderAmount: 100000m,
                IdempotencyKey: $"IDEM_{i:D4}"
            );

            return await useCase.ExecuteAsync(request);
        });

        var results = await Task.WhenAll(tasks);

        // Assert: Exactly 25 must succeed, exactly 75 must be rejected
        int successCount = results.Count(r => r.Success);
        int failureCount = results.Count(r => !r.Success);

        successCount.Should().Be(initialQuota, "Exactly the initial quota must be successfully redeemed");
        failureCount.Should().Be(totalConcurrentUsers - initialQuota, "All excess attempts must be rejected with zero race condition");

        // Remaining quota in repository must be zero
        var updatedVoucher = await repo.GetByCodeAsync(voucherCode);
        updatedVoucher!.RemainingQuota.Should().Be(0);
    }

    [Fact]
    public async Task IdempotencyKey_Should_Prevent_Duplicate_Charge_On_Network_Retry()
    {
        // Arrange
        var repo = new InMemoryVoucherRepository();
        var cache = new InMemoryDistributedCacheService();

        var voucher = new Voucher(
            id: Guid.NewGuid(),
            code: "IDEM_TEST",
            campaignId: "CAMP",
            discountAmount: 10000m,
            minimumSpend: 50000m,
            totalQuota: 10,
            startsAtUtc: DateTime.UtcNow.AddMinutes(-5),
            expiresAtUtc: DateTime.UtcNow.AddHours(2)
        );
        repo.Seed(voucher);
        await cache.SetVoucherQuotaCacheAsync("IDEM_TEST", 10, TimeSpan.FromHours(1));

        var useCase = new RedeemVoucherUseCase(repo, cache);

        var request = new RedeemVoucherRequest(
            VoucherCode: "IDEM_TEST",
            CustomerId: "CUST_001",
            OrderId: "ORD_001",
            StoreId: "STORE_01",
            OrderAmount: 80000m,
            IdempotencyKey: "SAME_TRANSACTION_KEY"
        );

        // First attempt
        var firstResult = await useCase.ExecuteAsync(request);
        firstResult.Success.Should().BeTrue();

        // Second attempt with same idempotency key (simulating retry)
        var secondResult = await useCase.ExecuteAsync(request);
        secondResult.Success.Should().BeTrue();
        secondResult.Message.Should().Contain("Idempotency Hit");

        // Quota should only be decremented ONCE!
        var v = await repo.GetByCodeAsync("IDEM_TEST");
        v!.RemainingQuota.Should().Be(9);
    }
}
