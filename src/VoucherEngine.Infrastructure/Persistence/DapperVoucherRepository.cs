using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using VoucherEngine.Application.Interfaces;
using VoucherEngine.Domain.Entities;

namespace VoucherEngine.Infrastructure.Persistence;

/// <summary>
/// Production Dapper repository backed by SQL Server.
/// Uses standard committed reads (no NOLOCK dirty reads) to guarantee RowVersion and IdempotencyKey integrity.
/// </summary>
public class DapperVoucherRepository : IVoucherRepository
{
    private readonly string _connectionString;
    private readonly ILogger<DapperVoucherRepository> _logger;

    public DapperVoucherRepository(string connectionString, ILogger<DapperVoucherRepository> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    private IDbConnection CreateConnection() => new SqlConnection(_connectionString);

    public async Task<Voucher?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        // Avoid WITH (NOLOCK): RowVersion must never be read uncommitted or stale under concurrent redemptions.
        const string sql = @"
            SELECT TOP 1
                Id, Code, CampaignId, DiscountAmount, MinimumSpend,
                TotalQuota, RemainingQuota, Status, StartsAtUtc, ExpiresAtUtc, RowVersion
            FROM Vouchers
            WHERE Code = @Code;";

        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Voucher>(new CommandDefinition(
            sql,
            new { Code = code.Trim().ToUpperInvariant() },
            cancellationToken: cancellationToken
        ));
    }

    public async Task<RedemptionRecord?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        // Avoid WITH (NOLOCK): idempotency checks require committed transactional state.
        const string sql = @"
            SELECT TOP 1
                Id, VoucherId, CustomerId, OrderId, StoreId,
                IdempotencyKey, OrderAmount, AppliedDiscount, RedeemedAtUtc
            FROM RedemptionRecords
            WHERE IdempotencyKey = @IdempotencyKey;";

        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<RedemptionRecord>(new CommandDefinition(
            sql,
            new { IdempotencyKey = idempotencyKey },
            cancellationToken: cancellationToken
        ));
    }

    public async Task<bool> TryRedeemOptimisticAsync(
        Guid voucherId,
        byte[] expectedRowVersion,
        RedemptionRecord record,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SET XACT_ABORT ON;
            BEGIN TRY
                BEGIN TRANSACTION;

                UPDATE Vouchers
                SET RemainingQuota = RemainingQuota - 1,
                    Status = CASE WHEN RemainingQuota - 1 <= 0 THEN 2 ELSE Status END
                WHERE Id = @VoucherId
                  AND RowVersion = @ExpectedRowVersion
                  AND RemainingQuota > 0;

                IF @@ROWCOUNT = 0
                BEGIN
                    ROLLBACK TRANSACTION;
                    SELECT 0 AS Result;
                    RETURN;
                END

                INSERT INTO RedemptionRecords (Id, VoucherId, CustomerId, OrderId, StoreId, IdempotencyKey, OrderAmount, AppliedDiscount, RedeemedAtUtc)
                VALUES (@Id, @VoucherId, @CustomerId, @OrderId, @StoreId, @IdempotencyKey, @OrderAmount, @AppliedDiscount, @RedeemedAtUtc);

                COMMIT TRANSACTION;
                SELECT 1 AS Result;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0
                    ROLLBACK TRANSACTION;
                THROW;
            END CATCH;";

        using var conn = CreateConnection();
        try
        {
            var result = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                sql,
                new
                {
                    VoucherId = voucherId,
                    ExpectedRowVersion = expectedRowVersion,
                    record.Id,
                    record.CustomerId,
                    record.OrderId,
                    record.StoreId,
                    record.IdempotencyKey,
                    record.OrderAmount,
                    record.AppliedDiscount,
                    record.RedeemedAtUtc
                },
                cancellationToken: cancellationToken
            ));
            return result == 1;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            _logger.LogInformation("Duplicate idempotency key collision handled cleanly for {Key}.", record.IdempotencyKey);
            return false;
        }
    }
}
