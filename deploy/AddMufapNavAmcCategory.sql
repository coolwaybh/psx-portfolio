BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924134711_AddMufapNavAmcCategory'
)
BEGIN
    ALTER TABLE [MufapNavs] ADD [Amc] nvarchar(150) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924134711_AddMufapNavAmcCategory'
)
BEGIN
    ALTER TABLE [MufapNavs] ADD [Category] nvarchar(100) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924134711_AddMufapNavAmcCategory'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260924134711_AddMufapNavAmcCategory', N'9.0.5');
END;

COMMIT;
GO

