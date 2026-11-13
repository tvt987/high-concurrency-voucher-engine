using Microsoft.AspNetCore.Mvc;
using VoucherEngine.Application.Commands;
using VoucherEngine.Application.DTOs;
using VoucherEngine.Application.Interfaces;
using VoucherEngine.Domain.Entities;
using VoucherEngine.Infrastructure.Caching;
using VoucherEngine.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// In-Memory standalone defaults (Can be swapped with Redis & SQL Server via appsettings)
var inMemoryRepo = new InMemoryVoucherRepository();
var inMemoryCache = new InMemoryDistributedCacheService();

// Seed initial flash-sale voucher for demo
var sampleVoucher = new Voucher(
    id: Guid.NewGuid(),
    code: "CONCUNG50K",
    campaignId: "FLASH_SALE_700_STORES",
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
    tier = "Solution Architect High-Concurrency Engine",
    targetRps = 5000,
    timestamp = DateTime.UtcNow
}));

app.MapGet("/api/v1/vouchers/{code}", async (string code, [FromServices] IVoucherRepository repo) =>
{
    var voucher = await repo.GetByCodeAsync(code);
    return voucher is not null ? Results.Ok(voucher) : Results.NotFound(new { message = "Voucher not found." });
});

app.MapPost("/api/v1/vouchers/redeem", async (
    [FromBody] RedeemVoucherRequest request,
    [FromServices] RedeemVoucherUseCase useCase,
    CancellationToken ct) =>
{
    var result = await useCase.ExecuteAsync(request, ct);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.Run();
