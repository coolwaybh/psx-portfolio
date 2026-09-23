BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923094112_AddFundamentalView'
)
BEGIN
    ALTER TABLE [Users] ADD [LastFundamentalRefreshUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923094112_AddFundamentalView'
)
BEGIN
    CREATE TABLE [FundamentalViews] (
        [Symbol] nvarchar(20) NOT NULL,
        [Signal] nvarchar(20) NOT NULL,
        [Confidence] nvarchar(20) NOT NULL,
        [Note] nvarchar(2000) NOT NULL,
        [SourcesJson] nvarchar(max) NOT NULL,
        [GeneratedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_FundamentalViews] PRIMARY KEY ([Symbol])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923094112_AddFundamentalView'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260923094112_AddFundamentalView', N'9.0.5');
END;

COMMIT;
GO

