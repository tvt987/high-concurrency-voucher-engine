IF DB_ID('VoucherDb') IS NULL
BEGIN
    CREATE DATABASE VoucherDb;
END
GO

USE VoucherDb;
GO

IF OBJECT_ID('dbo.Vouchers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Vouchers (
        Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        Code NVARCHAR(64) NOT NULL,
        CampaignId NVARCHAR(128) NOT NULL,
        DiscountAmount DECIMAL(18, 2) NOT NULL,
        MinimumSpend DECIMAL(18, 2) NOT NULL,
        TotalQuota INT NOT NULL,
        RemainingQuota INT NOT NULL,
        Status INT NOT NULL,
        StartsAtUtc DATETIME2 NOT NULL,
        ExpiresAtUtc DATETIME2 NOT NULL,
        RowVersion ROWVERSION NOT NULL
    );

    CREATE UNIQUE INDEX IX_Vouchers_Code ON dbo.Vouchers (Code);
END
GO

IF OBJECT_ID('dbo.RedemptionRecords', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RedemptionRecords (
        Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        VoucherId UNIQUEIDENTIFIER NOT NULL,
        CustomerId NVARCHAR(128) NOT NULL,
        OrderId NVARCHAR(128) NOT NULL,
        StoreId NVARCHAR(64) NOT NULL,
        IdempotencyKey NVARCHAR(128) NOT NULL,
        OrderAmount DECIMAL(18, 2) NOT NULL,
        AppliedDiscount DECIMAL(18, 2) NOT NULL,
        RedeemedAtUtc DATETIME2 NOT NULL,
        CONSTRAINT FK_RedemptionRecords_Vouchers FOREIGN KEY (VoucherId) REFERENCES dbo.Vouchers(Id)
    );

    CREATE UNIQUE INDEX IX_RedemptionRecords_IdempotencyKey ON dbo.RedemptionRecords (IdempotencyKey);
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Vouchers WHERE Code = 'CONCUNG50K')
BEGIN
    INSERT INTO dbo.Vouchers (
        Id, Code, CampaignId, DiscountAmount, MinimumSpend,
        TotalQuota, RemainingQuota, Status, StartsAtUtc, ExpiresAtUtc
    )
    VALUES (
        NEWID(), 'CONCUNG50K', 'FLASH_SALE_SAMPLE', 50000.00, 200000.00,
        5000, 5000, 1, DATEADD(DAY, -1, SYSUTCDATETIME()), DATEADD(DAY, 30, SYSUTCDATETIME())
    );
END
GO
