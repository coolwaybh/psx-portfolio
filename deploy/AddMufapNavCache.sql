BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924083353_AddMufapNavCache'
)
BEGIN
    CREATE TABLE [MufapNavs] (
        [Id] int NOT NULL IDENTITY,
        [FundName] nvarchar(200) NOT NULL,
        [Nav] decimal(18,4) NOT NULL,
        [AsOfDate] date NOT NULL,
        [FetchedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_MufapNavs] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924083353_AddMufapNavCache'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MufapNavs_FundName] ON [MufapNavs] ([FundName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924083353_AddMufapNavCache'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260924083353_AddMufapNavCache', N'9.0.5');
END;

COMMIT;
GO

