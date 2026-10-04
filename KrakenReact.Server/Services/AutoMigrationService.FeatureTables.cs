using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Serilog;

namespace KrakenReact.Server.Services;

/// <summary>Creation of the feature tables added after the initial schema.</summary>
public static partial class AutoMigrationService
{
    private static void CreateNewFeatureTables(KrakenDbContext db)
    {
        try
        {
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PortfolioSnapshots')
                BEGIN
                    CREATE TABLE [PortfolioSnapshots] (
                        [Date]     datetime2 NOT NULL,
                        [TotalUsd] decimal(38,2) NOT NULL DEFAULT 0,
                        [TotalGbp] decimal(38,2) NOT NULL DEFAULT 0,
                        CONSTRAINT [PK_PortfolioSnapshots] PRIMARY KEY ([Date])
                    )
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AlertLogs')
                BEGIN
                    CREATE TABLE [AlertLogs] (
                        [Id]        int IDENTITY(1,1) NOT NULL,
                        [Title]     nvarchar(max) NOT NULL DEFAULT '',
                        [Text]      nvarchar(max) NOT NULL DEFAULT '',
                        [Type]      nvarchar(50) NOT NULL DEFAULT 'info',
                        [CreatedAt] datetime2 NOT NULL,
                        CONSTRAINT [PK_AlertLogs] PRIMARY KEY ([Id])
                    )
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PriceAlerts')
                BEGIN
                    CREATE TABLE [PriceAlerts] (
                        [Id]          int IDENTITY(1,1) NOT NULL,
                        [Symbol]      nvarchar(100) NOT NULL DEFAULT '',
                        [TargetPrice] decimal(38,9) NOT NULL DEFAULT 0,
                        [Direction]   nvarchar(10) NOT NULL DEFAULT 'above',
                        [Active]      bit NOT NULL DEFAULT 1,
                        [TriggeredAt] datetime2 NULL,
                        [Note]        nvarchar(max) NOT NULL DEFAULT '',
                        [CreatedAt]   datetime2 NOT NULL,
                        CONSTRAINT [PK_PriceAlerts] PRIMARY KEY ([Id])
                    )
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PredictionHistories')
                BEGIN
                    CREATE TABLE [PredictionHistories] (
                        [Id]                  int IDENTITY(1,1) NOT NULL,
                        [Symbol]              nvarchar(100) NOT NULL DEFAULT '',
                        [ComputedAt]          datetime2 NOT NULL,
                        [PredictedUp]         bit NOT NULL DEFAULT 0,
                        [Probability]         real NOT NULL DEFAULT 0,
                        [ModelAccuracy]       real NOT NULL DEFAULT 0,
                        [WalkForwardAccuracy] real NOT NULL DEFAULT 0,
                        [Interval]            nvarchar(50) NOT NULL DEFAULT '',
                        CONSTRAINT [PK_PredictionHistories] PRIMARY KEY ([Id])
                    )
                    CREATE INDEX [IX_PredictionHistories_Symbol] ON [PredictionHistories] ([Symbol])
                    CREATE INDEX [IX_PredictionHistories_Symbol_ComputedAt] ON [PredictionHistories] ([Symbol], [ComputedAt])
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DcaRules')
                BEGIN
                    CREATE TABLE [DcaRules] (
                        [Id]             int IDENTITY(1,1) NOT NULL,
                        [Symbol]         nvarchar(100) NOT NULL DEFAULT '',
                        [AmountUsd]      decimal(38,2) NOT NULL DEFAULT 0,
                        [CronExpression] nvarchar(100) NOT NULL DEFAULT '0 9 * * 1',
                        [Active]         bit NOT NULL DEFAULT 1,
                        [CreatedAt]      datetime2 NOT NULL,
                        [LastRunAt]      datetime2 NULL,
                        [LastRunResult]  nvarchar(max) NOT NULL DEFAULT '',
                        CONSTRAINT [PK_DcaRules] PRIMARY KEY ([Id])
                    )
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProfitLadderRules')
                BEGIN
                    CREATE TABLE [ProfitLadderRules] (
                        [Id]               int IDENTITY(1,1) NOT NULL,
                        [Symbol]           nvarchar(100) NOT NULL DEFAULT '',
                        [TriggerPct]       decimal(38,2) NOT NULL DEFAULT 0,
                        [SellPct]          decimal(38,2) NOT NULL DEFAULT 25,
                        [Active]           bit NOT NULL DEFAULT 1,
                        [CreatedAt]        datetime2 NOT NULL,
                        [LastTriggeredAt]  datetime2 NULL,
                        [LastResult]       nvarchar(max) NOT NULL DEFAULT '',
                        [CooldownHours]    int NOT NULL DEFAULT 24,
                        CONSTRAINT [PK_ProfitLadderRules] PRIMARY KEY ([Id])
                    )
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MicroTradeRules')
                BEGIN
                    CREATE TABLE [MicroTradeRules] (
                        [Id]                 int IDENTITY(1,1) NOT NULL,
                        [Symbol]             nvarchar(100) NOT NULL DEFAULT '',
                        [DropPct]            decimal(38,9) NOT NULL DEFAULT 0,
                        [DropIntervalHours]  int NOT NULL DEFAULT 24,
                        [RisePct]            decimal(38,9) NOT NULL DEFAULT 0,
                        [BuyOrderTotal]      decimal(38,9) NOT NULL DEFAULT 0,
                        [MaxOrdersPerWindow] int NOT NULL DEFAULT 2,
                        [WindowHours]        int NOT NULL DEFAULT 2,
                        [CooldownHours]      int NOT NULL DEFAULT 1,
                        [StopLossEnabled]    bit NOT NULL DEFAULT 0,
                        [StopLossPct]        decimal(38,9) NOT NULL DEFAULT 95,
                        [Active]             bit NOT NULL DEFAULT 1,
                        [DryRun]             bit NOT NULL DEFAULT 0,
                        [CreatedAt]          datetime2 NOT NULL,
                        [LastCheckedAt]      datetime2 NULL,
                        [LastResult]         nvarchar(max) NOT NULL DEFAULT '',
                        CONSTRAINT [PK_MicroTradeRules] PRIMARY KEY ([Id])
                    )
                    CREATE INDEX [IX_MicroTradeRules_Active] ON [MicroTradeRules] ([Active])
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MicroTradeOrders')
                BEGIN
                    CREATE TABLE [MicroTradeOrders] (
                        [Id]          int IDENTITY(1,1) NOT NULL,
                        [RuleId]      int NOT NULL,
                        [Symbol]      nvarchar(100) NOT NULL DEFAULT '',
                        [BuyOrderId]  nvarchar(100) NULL,
                        [BuyPrice]    decimal(38,9) NOT NULL DEFAULT 0,
                        [Quantity]    decimal(38,9) NOT NULL DEFAULT 0,
                        [SellOrderId] nvarchar(100) NULL,
                        [SellPrice]   decimal(38,9) NOT NULL DEFAULT 0,
                        [Status]      nvarchar(20) NOT NULL DEFAULT 'Buying',
                        [DryRun]      bit NOT NULL DEFAULT 0,
                        [StopLossTriggered] bit NOT NULL DEFAULT 0,
                        [CreatedAt]   datetime2 NOT NULL,
                        [BuyFilledAt] datetime2 NULL,
                        [SoldAt]      datetime2 NULL,
                        [Note]        nvarchar(max) NOT NULL DEFAULT '',
                        CONSTRAINT [PK_MicroTradeOrders] PRIMARY KEY ([Id])
                    )
                    CREATE INDEX [IX_MicroTradeOrders_RuleId_CreatedAt] ON [MicroTradeOrders] ([RuleId], [CreatedAt])
                    CREATE INDEX [IX_MicroTradeOrders_Status] ON [MicroTradeOrders] ([Status])
                END

                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'MultiTfPredictionResults')
                BEGIN
                    CREATE TABLE [MultiTfPredictionResults] (
                        [Symbol]              nvarchar(450) NOT NULL,
                        [Interval]            nvarchar(50) NOT NULL,
                        [ComputedAt]          datetime2 NOT NULL,
                        [Status]              nvarchar(50) NOT NULL DEFAULT '',
                        [PredictedUp]         bit NOT NULL DEFAULT 0,
                        [Probability]         real NOT NULL DEFAULT 0,
                        [ModelAccuracy]       real NOT NULL DEFAULT 0,
                        [ModelAuc]            real NOT NULL DEFAULT 0,
                        [WalkForwardAccuracy] real NOT NULL DEFAULT 0,
                        [WalkForwardAuc]      real NOT NULL DEFAULT 0,
                        [PredictedUp3]        bit NOT NULL DEFAULT 0,
                        [Probability3]        real NOT NULL DEFAULT 0,
                        [PredictedUp6]        bit NOT NULL DEFAULT 0,
                        [Probability6]        real NOT NULL DEFAULT 0,
                        [TotalCandles]        int NOT NULL DEFAULT 0,
                        [ErrorMessage]        nvarchar(max) NULL,
                        CONSTRAINT [PK_MultiTfPredictionResults] PRIMARY KEY ([Symbol], [Interval])
                    )
                END
            ");
            Log.Information("[AutoMigration] New feature tables ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create new feature tables");
        }
    }
}
