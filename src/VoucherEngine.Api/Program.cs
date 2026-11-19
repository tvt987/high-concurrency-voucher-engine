using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using VoucherEngine.Application.Commands;
using VoucherEngine.Application.DTOs;
using VoucherEngine.Application.Interfaces;
using VoucherEngine.Domain.Entities;
using VoucherEngine.Infrastructure.Caching;
using VoucherEngine.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

string? sqlConn = builder.Configuration.GetConnectionString("SqlServer");
string? redisConn = builder.Configuration.GetConnectionString("Redis");

if (!string.IsNullOrWhiteSpace(sqlConn) && !string.IsNullOrWhiteSpace(redisConn))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
    builder.Services.AddSingleton<IDistributedCacheService, RedisDistributedCacheService>();
    builder.Services.AddScoped<IVoucherRepository>(sp =>
        new DapperVoucherRepository(sqlConn, sp.GetRequiredService<ILogger<DapperVoucherRepository>>()));
}
else
{
    // Standalone in-memory fallback when external SQL Server + Redis are not configured
    var inMemoryRepo = new InMemoryVoucherRepository();
    var inMemoryCache = new InMemoryDistributedCacheService();

    var sampleVoucher = new Voucher(
        id: Guid.NewGuid(),
        code: "CONCUNG50K",
        campaignId: "FLASH_SALE_SAMPLE",
        discountAmount: 50000m,
        minimumSpend: 200000m,
        totalQuota: 5000,
        startsAtUtc: DateTime.UtcNow.AddDays(-1),
        expiresAtUtc: DateTime.UtcNow.AddDays(7)
    );
    inMemoryRepo.Seed(sampleVoucher);
    inMemoryCache.SetVoucherQuotaCacheAsync("CONCUNG50K", 5000, TimeSpan.FromDays(7)).GetAwaiter().GetResult();

    builder.Services.AddSingleton<IVoucherRepository>(inMemoryRepo);
    builder.Services.AddSingleton<IDistributedCacheService>(inMemoryCache);
}

builder.Services.AddScoped<RedeemVoucherUseCase>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    mode = !string.IsNullOrWhiteSpace(sqlConn) && !string.IsNullOrWhiteSpace(redisConn) ? "SqlServer+Redis" : "InMemoryFallback",
    timestamp = DateTime.UtcNow
}));

app.MapGet("/api/v1/vouchers/{code}", async (string code, [FromServices] IVoucherRepository repo, CancellationToken ct) =>
{
    var voucher = await repo.GetByCodeAsync(code, ct);
    return voucher is not null ? Results.Ok(voucher) : Results.NotFound(new { message = "Voucher not found." });
});

app.MapPost("/api/v1/vouchers/redeem", async (
    [FromBody] RedeemVoucherRequest request,
    [FromServices] RedeemVoucherUseCase useCase,
    CancellationToken ct) =>
{
    var result = await useCase.ExecuteAsync(request, ct);
    if (result.Success)
        return Results.Ok(result);
    if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        return Results.NotFound(result);
    if (result.Message.Contains("Concurrency collision", StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(result);
    return Results.BadRequest(result);
});

app.Run();
