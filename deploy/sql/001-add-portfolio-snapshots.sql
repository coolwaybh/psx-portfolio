BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104083440_AddPortfolioSnapshots'
)
BEGIN
    CREATE TABLE [PortfolioSnapshots] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [SnapshotDate] date NOT NULL,
        [StocksValue] decimal(18,2) NOT NULL,
        [StocksCost] decimal(18,2) NOT NULL,
        [RealizedCum] decimal(18,2) NOT NULL,
        [FundsValue] decimal(18,2) NOT NULL,
        [FundsCost] decimal(18,2) NOT NULL,
        [Cash] decimal(18,2) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_PortfolioSnapshots] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PortfolioSnapshots_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104083440_AddPortfolioSnapshots'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PortfolioSnapshots_UserId_SnapshotDate] ON [PortfolioSnapshots] ([UserId], [SnapshotDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261104083440_AddPortfolioSnapshots'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261104083440_AddPortfolioSnapshots', N'9.0.5');
END;

COMMIT;
GO

