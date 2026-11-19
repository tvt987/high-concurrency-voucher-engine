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

        // Act: Fire 100 truly parallel thread-pool redemption attempts
        var tasks = Enumerable.Range(1, totalConcurrentUsers).Select(i => Task.Run(async () =>
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
        }));

        var results = await Task.WhenAll(tasks);

        // Assert: Exactly 25 must succeed, exactly 75 must be rejected
        int successCount = results.Count(r => r.Success);
        int failureCount = results.Count(r => !r.Success);

        successCount.Should().Be(initialQuota, "Exactly the initial quota must be successfully redeemed");
        failureCount.Should().Be(totalConcurrentUsers - initialQuota, "All excess attempts must be rejected with zero race condition");

        var updatedVoucher = await repo.GetByCodeAsync(voucherCode);
        updatedVoucher!.RemainingQuota.Should().Be(0);
    }

    [Fact]
    public async Task IdempotencyKey_Should_Prevent_Duplicate_Charge_On_Network_Retry()
    {
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

        var firstResult = await useCase.ExecuteAsync(request);
        firstResult.Success.Should().BeTrue();

        var secondResult = await useCase.ExecuteAsync(request);
        secondResult.Success.Should().BeTrue();
        secondResult.Message.Should().Contain("Idempotency Hit");

        var v = await repo.GetByCodeAsync("IDEM_TEST");
        v!.RemainingQuota.Should().Be(9);
    }

    [Fact]
    public async Task QuotaDepleted_RepeatedRejectedAttempts_NeverResurrectPhantomQuota()
    {
        // Regression test: previously RollbackQuotaAtomicAsync ran when currentQuota < 0,
        // leaking +1 phantom quota back into cache on every rejected request.
        var repo = new InMemoryVoucherRepository();
        var cache = new InMemoryDistributedCacheService();

        var voucher = new Voucher(
            id: Guid.NewGuid(),
            code: "PHANTOM_GUARD",
            campaignId: "CAMP",
            discountAmount: 10000m,
            minimumSpend: 50000m,
            totalQuota: 5,
            startsAtUtc: DateTime.UtcNow.AddMinutes(-5),
            expiresAtUtc: DateTime.UtcNow.AddHours(2)
        );
        repo.Seed(voucher);
        await cache.SetVoucherQuotaCacheAsync("PHANTOM_GUARD", 5, TimeSpan.FromHours(1));

        var useCase = new RedeemVoucherUseCase(repo, cache);

        // Deplete initial quota of 5
        for (int i = 1; i <= 5; i++)
        {
            var r = await useCase.ExecuteAsync(new RedeemVoucherRequest("PHANTOM_GUARD", $"C{i}", $"O{i}", "S1", 100000m, $"OK_{i}"));
            r.Success.Should().BeTrue();
        }

        // Send 50 additional requests after depletion
        for (int i = 6; i <= 55; i++)
        {
            var rej = await useCase.ExecuteAsync(new RedeemVoucherRequest("PHANTOM_GUARD", $"C{i}", $"O{i}", "S1", 100000m, $"EXTRA_{i}"));
            rej.Success.Should().BeFalse("Quota is depleted and must never resurrect phantom quota");
        }

        var updated = await repo.GetByCodeAsync("PHANTOM_GUARD");
        updated!.RemainingQuota.Should().Be(0);
        (await cache.DecrementQuotaAtomicAsync("PHANTOM_GUARD")).Should().Be(-1);
    }

    [Fact]
    public async Task SimultaneousDuplicateIdempotencyKeys_UnderHighConcurrency_RedeemExactlyOnce()
    {
        var repo = new InMemoryVoucherRepository();
        var cache = new InMemoryDistributedCacheService();

        var voucher = new Voucher(
            id: Guid.NewGuid(),
            code: "RACE_IDEM",
            campaignId: "CAMP",
            discountAmount: 10000m,
            minimumSpend: 50000m,
            totalQuota: 20,
            startsAtUtc: DateTime.UtcNow.AddMinutes(-5),
            expiresAtUtc: DateTime.UtcNow.AddHours(2)
        );
        repo.Seed(voucher);
        await cache.SetVoucherQuotaCacheAsync("RACE_IDEM", 20, TimeSpan.FromHours(1));

        var useCase = new RedeemVoucherUseCase(repo, cache);

        var tasks = Enumerable.Range(1, 30).Select(_ => Task.Run(async () =>
            await useCase.ExecuteAsync(new RedeemVoucherRequest(
                VoucherCode: "RACE_IDEM",
                CustomerId: "CUST_SAME",
                OrderId: "ORD_SAME",
                StoreId: "STORE_01",
                OrderAmount: 100000m,
                IdempotencyKey: "IDENTICAL_RETRY_KEY"
            ))
        ));

        var results = await Task.WhenAll(tasks);

        results.All(r => r.Success).Should().BeTrue("Every idempotent retry should succeed with the original result");
        var v = await repo.GetByCodeAsync("RACE_IDEM");
        v!.RemainingQuota.Should().Be(19, "Duplicate concurrent retries must only decrement quota once");
    }

    [Fact]
    public async Task RowVersion_MonotonicIncrement_UnderTightLoop_NeverCollides()
    {
        var repo = new InMemoryVoucherRepository();
        var cache = new InMemoryDistributedCacheService();

        var voucher = new Voucher(
            id: Guid.NewGuid(),
            code: "VERSION_SEQ",
            campaignId: "CAMP",
            discountAmount: 10000m,
            minimumSpend: 50000m,
            totalQuota: 10,
            startsAtUtc: DateTime.UtcNow.AddMinutes(-5),
            expiresAtUtc: DateTime.UtcNow.AddHours(2)
        );
        repo.Seed(voucher);
        await cache.SetVoucherQuotaCacheAsync("VERSION_SEQ", 10, TimeSpan.FromHours(1));

        var useCase = new RedeemVoucherUseCase(repo, cache);
        var observedVersions = new List<byte[]>();

        for (int i = 1; i <= 10; i++)
        {
            var current = await repo.GetByCodeAsync("VERSION_SEQ");
            observedVersions.Add(current!.RowVersion.ToArray());

            var res = await useCase.ExecuteAsync(new RedeemVoucherRequest(
                "VERSION_SEQ", $"C{i}", $"O{i}", "S1", 100000m, $"SEQ_{i}"
            ));
            res.Success.Should().BeTrue();
        }

        var distinctHex = observedVersions.Select(Convert.ToHexString).Distinct().ToList();
        distinctHex.Should().HaveCount(10, "Each committed redemption must advance RowVersion monotonically without tight-loop collisions");
    }
}
