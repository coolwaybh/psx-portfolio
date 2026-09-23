BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923154914_AddMutualFunds'
)
BEGIN
    CREATE TABLE [MutualFunds] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Amc] nvarchar(100) NOT NULL,
        [Category] nvarchar(50) NOT NULL,
        [CurrentNav] decimal(18,4) NOT NULL,
        [NavUpdatedAt] date NOT NULL,
        CONSTRAINT [PK_MutualFunds] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MutualFunds_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923154914_AddMutualFunds'
)
BEGIN
    CREATE TABLE [FundNavHistories] (
        [Id] int NOT NULL IDENTITY,
        [FundId] int NOT NULL,
        [Nav] decimal(18,4) NOT NULL,
        [AsOfDate] date NOT NULL,
        CONSTRAINT [PK_FundNavHistories] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FundNavHistories_MutualFunds_FundId] FOREIGN KEY ([FundId]) REFERENCES [MutualFunds] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923154914_AddMutualFunds'
)
BEGIN
    CREATE TABLE [FundTransactions] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [FundId] int NOT NULL,
        [Type] nvarchar(20) NOT NULL,
        [TxDate] date NOT NULL,
        [Units] decimal(18,4) NOT NULL,
        [Nav] decimal(18,4) NOT NULL,
        [Amount] decimal(18,4) NOT NULL,
        [FrontLoadPct] decimal(5,2) NOT NULL,
        [BackLoadPct] decimal(5,2) NOT NULL,
        [Notes] nvarchar(1000) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_FundTransactions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FundTransactions_MutualFunds_FundId] FOREIGN KEY ([FundId]) REFERENCES [MutualFunds] ([Id]),
        CONSTRAINT [FK_FundTransactions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923154914_AddMutualFunds'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FundNavHistories_FundId_AsOfDate] ON [FundNavHistories] ([FundId], [AsOfDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923154914_AddMutualFunds'
)
BEGIN
    CREATE INDEX [IX_FundTransactions_FundId] ON [FundTransactions] ([FundId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923154914_AddMutualFunds'
)
BEGIN
    CREATE INDEX [IX_FundTransactions_UserId_FundId] ON [FundTransactions] ([UserId], [FundId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923154914_AddMutualFunds'
)
BEGIN
    CREATE INDEX [IX_MutualFunds_UserId] ON [MutualFunds] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923154914_AddMutualFunds'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260923154914_AddMutualFunds', N'9.0.5');
END;

COMMIT;
GO

