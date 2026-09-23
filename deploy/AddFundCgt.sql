BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923194457_AddFundCgt'
)
BEGIN
    ALTER TABLE [FundTransactions] ADD [CgtAmount] decimal(18,4) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923194457_AddFundCgt'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260923194457_AddFundCgt', N'9.0.5');
END;

COMMIT;
GO

