using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Serilog;

namespace KrakenReact.Server.Services;

/// <summary>Additive column and small-table fixes applied to existing databases.</summary>
public static partial class AutoMigrationService
{
    private static void EnsureNewFeatureColumns(KrakenDbContext db)
    {
        try
        {
            // MicroTradeRules.CooldownHours — minimum gap between orders on the same pair (added after initial release)
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('MicroTradeRules', 'CooldownHours') IS NULL
                    ALTER TABLE [MicroTradeRules] ADD [CooldownHours] int NOT NULL CONSTRAINT [DF_MicroTradeRules_CooldownHours] DEFAULT 1;
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not ensure MicroTradeRules.CooldownHours column");
        }

        try
        {
            // MicroTradeRules.DropIntervalHours — configurable drop-check window (added after initial release, was hardcoded to 24h)
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('MicroTradeRules', 'DropIntervalHours') IS NULL
                    ALTER TABLE [MicroTradeRules] ADD [DropIntervalHours] int NOT NULL CONSTRAINT [DF_MicroTradeRules_DropIntervalHours] DEFAULT 24;
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not ensure MicroTradeRules.DropIntervalHours column");
        }

        try
        {
            // MicroTradeRules stop-loss columns (added after initial release)
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('MicroTradeRules', 'StopLossEnabled') IS NULL
                    ALTER TABLE [MicroTradeRules] ADD [StopLossEnabled] bit NOT NULL CONSTRAINT [DF_MicroTradeRules_StopLossEnabled] DEFAULT 0;
                IF COL_LENGTH('MicroTradeRules', 'StopLossPct') IS NULL
                    ALTER TABLE [MicroTradeRules] ADD [StopLossPct] decimal(38,9) NOT NULL CONSTRAINT [DF_MicroTradeRules_StopLossPct] DEFAULT 95;
                IF COL_LENGTH('MicroTradeOrders', 'StopLossTriggered') IS NULL
                    ALTER TABLE [MicroTradeOrders] ADD [StopLossTriggered] bit NOT NULL CONSTRAINT [DF_MicroTradeOrders_StopLossTriggered] DEFAULT 0;
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not ensure MicroTrade stop-loss columns");
        }

        try
        {
            // MicroTradeOrders userref columns � lets a placement whose response timed out be found on Kraken again
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('MicroTradeOrders', 'BuyUserRef') IS NULL
                    ALTER TABLE [MicroTradeOrders] ADD [BuyUserRef] bigint NULL;
                IF COL_LENGTH('MicroTradeOrders', 'SellUserRef') IS NULL
                    ALTER TABLE [MicroTradeOrders] ADD [SellUserRef] bigint NULL;
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not ensure MicroTradeOrders userref columns");
        }

        try
        {
            // PriceAlert auto-order columns (added in new feature release)
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('PriceAlerts', 'AutoOrderEnabled') IS NULL
                    ALTER TABLE [PriceAlerts] ADD [AutoOrderEnabled] bit NOT NULL CONSTRAINT [DF_PriceAlerts_AutoOrderEnabled] DEFAULT 0;
                IF COL_LENGTH('PriceAlerts', 'AutoOrderSide') IS NULL
                    ALTER TABLE [PriceAlerts] ADD [AutoOrderSide] nvarchar(10) NOT NULL CONSTRAINT [DF_PriceAlerts_AutoOrderSide] DEFAULT 'Buy';
                IF COL_LENGTH('PriceAlerts', 'AutoOrderQty') IS NULL
                    ALTER TABLE [PriceAlerts] ADD [AutoOrderQty] decimal(38,9) NOT NULL CONSTRAINT [DF_PriceAlerts_AutoOrderQty] DEFAULT 0;
                IF COL_LENGTH('PriceAlerts', 'AutoOrderOffsetPct') IS NULL
                    ALTER TABLE [PriceAlerts] ADD [AutoOrderOffsetPct] decimal(38,9) NOT NULL CONSTRAINT [DF_PriceAlerts_AutoOrderOffsetPct] DEFAULT 0;
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not ensure new feature columns");
        }

        try
        {
            // RebalanceSchedules table
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'RebalanceSchedules')
                BEGIN
                    CREATE TABLE [RebalanceSchedules] (
                        [Id]             int IDENTITY(1,1) NOT NULL,
                        [Targets]        nvarchar(max) NOT NULL DEFAULT '',
                        [CronExpression] nvarchar(100) NOT NULL DEFAULT '0 9 * * 1',
                        [Active]         bit NOT NULL DEFAULT 1,
                        [DriftMinPct]    decimal(38,9) NOT NULL DEFAULT 5,
                        [AutoExecute]    bit NOT NULL DEFAULT 0,
                        [Note]           nvarchar(max) NOT NULL DEFAULT '',
                        [CreatedAt]      datetime2 NOT NULL,
                        [LastRunAt]      datetime2 NULL,
                        [LastRunResult]  nvarchar(max) NOT NULL DEFAULT '',
                        CONSTRAINT [PK_RebalanceSchedules] PRIMARY KEY ([Id])
                    )
                END
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create RebalanceSchedules table");
        }

        try
        {
            // ScheduledOrders table
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScheduledOrders')
                BEGIN
                    CREATE TABLE [ScheduledOrders] (
                        [Id]           int IDENTITY(1,1) NOT NULL,
                        [Symbol]       nvarchar(100) NOT NULL DEFAULT '',
                        [Side]         nvarchar(10) NOT NULL DEFAULT 'Buy',
                        [Price]        decimal(38,9) NOT NULL DEFAULT 0,
                        [Quantity]     decimal(38,9) NOT NULL DEFAULT 0,
                        [ScheduledAt]  datetime2 NOT NULL,
                        [ExecutedAt]   datetime2 NULL,
                        [Status]       nvarchar(20) NOT NULL DEFAULT 'Pending',
                        [Note]         nvarchar(max) NOT NULL DEFAULT '',
                        [ErrorMessage] nvarchar(max) NOT NULL DEFAULT '',
                        [CreatedAt]    datetime2 NOT NULL,
                        CONSTRAINT [PK_ScheduledOrders] PRIMARY KEY ([Id])
                    )
                END
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create ScheduledOrders table");
        }

        try
        {
            // OrderTemplates table
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OrderTemplates')
                BEGIN
                    CREATE TABLE [OrderTemplates] (
                        [Id]             int IDENTITY(1,1) NOT NULL,
                        [Name]           nvarchar(200) NOT NULL DEFAULT '',
                        [Symbol]         nvarchar(100) NOT NULL DEFAULT '',
                        [Side]           nvarchar(10) NOT NULL DEFAULT 'Buy',
                        [PriceOffsetPct] decimal(38,9) NULL,
                        [Quantity]       decimal(38,9) NULL,
                        [QtyPct]         decimal(38,9) NULL,
                        [Note]           nvarchar(max) NOT NULL DEFAULT '',
                        [CreatedAt]      datetime2 NOT NULL,
                        CONSTRAINT [PK_OrderTemplates] PRIMARY KEY ([Id])
                    )
                END
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create OrderTemplates table");
        }

        try
        {
            // DcaRules — smart/conditional columns + ATR sizing + Fear & Greed gate
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('DcaRules', 'ConditionalEnabled') IS NULL
                    ALTER TABLE [DcaRules] ADD [ConditionalEnabled] bit NOT NULL CONSTRAINT [DF_DcaRules_ConditionalEnabled] DEFAULT 0;
                IF COL_LENGTH('DcaRules', 'ConditionalMaPeriod') IS NULL
                    ALTER TABLE [DcaRules] ADD [ConditionalMaPeriod] int NOT NULL CONSTRAINT [DF_DcaRules_ConditionalMaPeriod] DEFAULT 20;
                IF COL_LENGTH('DcaRules', 'AtrSizingEnabled') IS NULL
                    ALTER TABLE [DcaRules] ADD [AtrSizingEnabled] bit NOT NULL CONSTRAINT [DF_DcaRules_AtrSizingEnabled] DEFAULT 0;
                IF COL_LENGTH('DcaRules', 'AtrRiskUsd') IS NULL
                    ALTER TABLE [DcaRules] ADD [AtrRiskUsd] decimal(38,9) NOT NULL CONSTRAINT [DF_DcaRules_AtrRiskUsd] DEFAULT 50;
                IF COL_LENGTH('DcaRules', 'FearGreedEnabled') IS NULL
                    ALTER TABLE [DcaRules] ADD [FearGreedEnabled] bit NOT NULL CONSTRAINT [DF_DcaRules_FearGreedEnabled] DEFAULT 0;
                IF COL_LENGTH('DcaRules', 'FearGreedMaxIndex') IS NULL
                    ALTER TABLE [DcaRules] ADD [FearGreedMaxIndex] int NOT NULL CONSTRAINT [DF_DcaRules_FearGreedMaxIndex] DEFAULT 75;
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not add DcaRules columns");
        }

        try
        {
            // BracketOrders table
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'BracketOrders')
                BEGIN
                    CREATE TABLE [BracketOrders] (
                        [Id]               int IDENTITY(1,1) NOT NULL,
                        [KrakenOrderId]    nvarchar(100) NOT NULL DEFAULT '',
                        [Symbol]           nvarchar(100) NOT NULL DEFAULT '',
                        [Side]             nvarchar(10) NOT NULL DEFAULT 'Buy',
                        [Quantity]         decimal(38,9) NOT NULL DEFAULT 0,
                        [EntryPrice]       decimal(38,9) NOT NULL DEFAULT 0,
                        [StopPrice]        decimal(38,9) NOT NULL DEFAULT 0,
                        [TakeProfitPrice]  decimal(38,9) NOT NULL DEFAULT 0,
                        [Status]           nvarchar(20) NOT NULL DEFAULT 'Watching',
                        [StopOrderId]      nvarchar(100) NULL,
                        [TakeProfitOrderId] nvarchar(100) NULL,
                        [CreatedAt]        datetime2 NOT NULL,
                        [ActivatedAt]      datetime2 NULL,
                        [Note]             nvarchar(max) NOT NULL DEFAULT '',
                        CONSTRAINT [PK_BracketOrders] PRIMARY KEY ([Id])
                    )
                    CREATE INDEX [IX_BracketOrders_Status] ON [BracketOrders] ([Status])
                END
            ");
            Log.Information("[AutoMigration] BracketOrders table ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create BracketOrders table");
        }

        try
        {
            // AutoRepriceRules table
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AutoRepriceRules')
                BEGIN
                    CREATE TABLE [AutoRepriceRules] (
                        [Id]                 int IDENTITY(1,1) NOT NULL,
                        [Symbol]             nvarchar(100) NOT NULL DEFAULT '',
                        [MaxDeviationPct]    decimal(38,9) NOT NULL DEFAULT 2,
                        [MinAgeMinutes]      int NOT NULL DEFAULT 15,
                        [MaxAgeMinutes]      int NOT NULL DEFAULT 0,
                        [RepriceBuys]        bit NOT NULL DEFAULT 1,
                        [RepriceSells]       bit NOT NULL DEFAULT 0,
                        [NewPriceOffsetPct]  decimal(38,9) NOT NULL DEFAULT 0,
                        [Active]             bit NOT NULL DEFAULT 1,
                        [CreatedAt]          datetime2 NOT NULL,
                        [LastResult]         nvarchar(max) NOT NULL DEFAULT '',
                        [LastRunAt]          datetime2 NULL,
                        CONSTRAINT [PK_AutoRepriceRules] PRIMARY KEY ([Id])
                    )
                END
                ELSE
                BEGIN
                    IF COL_LENGTH('AutoRepriceRules', 'MaxAgeMinutes') IS NULL
                        ALTER TABLE [AutoRepriceRules] ADD [MaxAgeMinutes] int NOT NULL CONSTRAINT [DF_AutoRepriceRules_MaxAgeMinutes] DEFAULT 0;
                    IF COL_LENGTH('AutoRepriceRules', 'RepriceBuys') IS NULL
                        ALTER TABLE [AutoRepriceRules] ADD [RepriceBuys] bit NOT NULL CONSTRAINT [DF_AutoRepriceRules_RepriceBuys] DEFAULT 1;
                    IF COL_LENGTH('AutoRepriceRules', 'RepriceSells') IS NULL
                        ALTER TABLE [AutoRepriceRules] ADD [RepriceSells] bit NOT NULL CONSTRAINT [DF_AutoRepriceRules_RepriceSells] DEFAULT 0;
                    IF COL_LENGTH('AutoRepriceRules', 'NewPriceOffsetPct') IS NULL
                        ALTER TABLE [AutoRepriceRules] ADD [NewPriceOffsetPct] decimal(38,9) NOT NULL CONSTRAINT [DF_AutoRepriceRules_NewPriceOffsetPct] DEFAULT 0;
                END
            ");
            Log.Information("[AutoMigration] AutoRepriceRules table ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create AutoRepriceRules table");
        }

        try
        {
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('RebalanceSchedules', 'Note') IS NULL
                    ALTER TABLE [RebalanceSchedules] ADD [Note] nvarchar(max) NOT NULL CONSTRAINT [DF_RebalanceSchedules_Note] DEFAULT '';
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not add Note column to RebalanceSchedules");
        }

        try
        {
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PriceSnapshots')
                BEGIN
                    CREATE TABLE [PriceSnapshots] (
                        [Id]          int IDENTITY(1,1) NOT NULL,
                        [Symbol]      nvarchar(100) NOT NULL DEFAULT '',
                        [Price]       decimal(38,9) NOT NULL DEFAULT 0,
                        [CapturedAt]  datetime2 NOT NULL,
                        CONSTRAINT [PK_PriceSnapshots] PRIMARY KEY ([Id])
                    )
                    CREATE INDEX [IX_PriceSnapshots_Symbol_CapturedAt] ON [PriceSnapshots] ([Symbol], [CapturedAt])
                END
            ");
            Log.Information("[AutoMigration] PriceSnapshots table ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create PriceSnapshots table");
        }
    }

    private static void EnsurePredictionResultColumns(KrakenDbContext db)
    {
        try
        {
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('PredictionResults', 'WalkForwardAccuracy') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardAccuracy] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardAccuracy] DEFAULT 0;

                IF COL_LENGTH('PredictionResults', 'WalkForwardAuc') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardAuc] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardAuc] DEFAULT 0;

                IF COL_LENGTH('PredictionResults', 'WalkForwardFoldCount') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardFoldCount] int NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardFoldCount] DEFAULT 0;

                IF COL_LENGTH('PredictionResults', 'PredictedUp3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [PredictedUp3] bit NOT NULL CONSTRAINT [DF_PredictionResults_PredictedUp3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'Probability3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [Probability3] real NOT NULL CONSTRAINT [DF_PredictionResults_Probability3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'ModelAccuracy3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [ModelAccuracy3] real NOT NULL CONSTRAINT [DF_PredictionResults_ModelAccuracy3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'ModelAuc3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [ModelAuc3] real NOT NULL CONSTRAINT [DF_PredictionResults_ModelAuc3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardAccuracy3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardAccuracy3] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardAccuracy3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardAuc3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardAuc3] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardAuc3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardFoldCount3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardFoldCount3] int NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardFoldCount3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'LogRegAccuracy3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [LogRegAccuracy3] real NOT NULL CONSTRAINT [DF_PredictionResults_LogRegAccuracy3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'BenchmarkBuyHold3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [BenchmarkBuyHold3] real NOT NULL CONSTRAINT [DF_PredictionResults_BenchmarkBuyHold3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'BenchmarkSma3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [BenchmarkSma3] real NOT NULL CONSTRAINT [DF_PredictionResults_BenchmarkSma3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'TrainSamples3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [TrainSamples3] int NOT NULL CONSTRAINT [DF_PredictionResults_TrainSamples3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'TestSamples3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [TestSamples3] int NOT NULL CONSTRAINT [DF_PredictionResults_TestSamples3] DEFAULT 0;

                IF COL_LENGTH('PredictionResults', 'PredictedUp6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [PredictedUp6] bit NOT NULL CONSTRAINT [DF_PredictionResults_PredictedUp6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'Probability6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [Probability6] real NOT NULL CONSTRAINT [DF_PredictionResults_Probability6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'ModelAccuracy6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [ModelAccuracy6] real NOT NULL CONSTRAINT [DF_PredictionResults_ModelAccuracy6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'ModelAuc6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [ModelAuc6] real NOT NULL CONSTRAINT [DF_PredictionResults_ModelAuc6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardAccuracy6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardAccuracy6] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardAccuracy6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardAuc6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardAuc6] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardAuc6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardFoldCount6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardFoldCount6] int NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardFoldCount6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'LogRegAccuracy6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [LogRegAccuracy6] real NOT NULL CONSTRAINT [DF_PredictionResults_LogRegAccuracy6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'BenchmarkBuyHold6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [BenchmarkBuyHold6] real NOT NULL CONSTRAINT [DF_PredictionResults_BenchmarkBuyHold6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'BenchmarkSma6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [BenchmarkSma6] real NOT NULL CONSTRAINT [DF_PredictionResults_BenchmarkSma6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'TrainSamples6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [TrainSamples6] int NOT NULL CONSTRAINT [DF_PredictionResults_TrainSamples6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'TestSamples6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [TestSamples6] int NOT NULL CONSTRAINT [DF_PredictionResults_TestSamples6] DEFAULT 0;

            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not ensure PredictionResults walk-forward columns");
        }

        // Separate block so a failure above does not prevent these newer columns from being added.
        try
        {
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('PredictionResults', 'WalkForwardLogRegAccuracy') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardLogRegAccuracy] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardLogRegAccuracy] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardLogRegAuc') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardLogRegAuc] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardLogRegAuc] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardLogRegAccuracy3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardLogRegAccuracy3] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardLogRegAccuracy3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardLogRegAuc3') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardLogRegAuc3] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardLogRegAuc3] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardLogRegAccuracy6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardLogRegAccuracy6] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardLogRegAccuracy6] DEFAULT 0;
                IF COL_LENGTH('PredictionResults', 'WalkForwardLogRegAuc6') IS NULL
                    ALTER TABLE [PredictionResults] ADD [WalkForwardLogRegAuc6] real NOT NULL CONSTRAINT [DF_PredictionResults_WalkForwardLogRegAuc6] DEFAULT 0;
            ");
            Log.Information("[AutoMigration] PredictionResults WalkForwardLogReg columns ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not ensure PredictionResults WalkForwardLogReg columns");
        }
    }
}
