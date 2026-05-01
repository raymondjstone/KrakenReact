using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Serilog;

namespace KrakenReact.Server.Services;

/// <summary>
/// Automatically detects schema changes and applies migrations or creates missing tables.
/// This eliminates the need to manually run Add-Migration and Update-Database commands.
/// </summary>
public static class AutoMigrationService
{
    /// <summary>
    /// Ensures the database schema matches the current model.
    /// Creates missing tables and columns automatically.
    /// </summary>
    public static void EnsureDatabaseSchema(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

        try
        {
            Log.Information("[AutoMigration] Checking database schema...");

            // Check if there are pending migrations
            var pendingMigrations = db.Database.GetPendingMigrations().ToList();
            
            if (pendingMigrations.Any())
            {
                Log.Information("[AutoMigration] Found {Count} pending migrations, applying...", pendingMigrations.Count);
                db.Database.Migrate();
                Log.Information("[AutoMigration] Migrations applied successfully");
                return;
            }

            // Check if AppSettings table exists (our new table)
            var canConnectToAppSettings = false;
            try
            {
                canConnectToAppSettings = db.AppSettings.Any();
            }
            catch
            {
                // Table doesn't exist
            }

            // Check if AssetNormalizations table exists
            var canConnectToAssetNormalizations = false;
            try
            {
                canConnectToAssetNormalizations = db.AssetNormalizations.Any();
            }
            catch { }

            // Check if PredictionResults table exists
            var canConnectToPredictions = false;
            try
            {
                canConnectToPredictions = db.PredictionResults.Any();
            }
            catch { }

            // If tables are missing, create them
            if (!canConnectToAppSettings || !canConnectToAssetNormalizations || !canConnectToPredictions)
            {
                Log.Information("[AutoMigration] Missing tables detected, creating schema...");
                CreateMissingTables(db);
                Log.Information("[AutoMigration] Schema created successfully");
            }
            else
            {
                Log.Information("[AutoMigration] Database schema is up to date");
            }

            // Always attempt to create new feature tables (idempotent IF NOT EXISTS)
            CreateNewFeatureTables(db);

            // Apply performance optimizations (idempotent)
            ApplyPerformanceOptimizations(db);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AutoMigration] Error ensuring database schema");
            throw;
        }
    }

    /// <summary>
    /// Creates missing tables using raw SQL.
    /// This is a fallback when no migrations exist but tables are needed.
    /// </summary>
    private static void CreateMissingTables(KrakenDbContext db)
    {
        // Create AppSettings table if it doesn't exist
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings')
            BEGIN
                CREATE TABLE [AppSettings] (
                    [Key] nvarchar(450) NOT NULL,
                    [Value] nvarchar(max) NOT NULL,
                    [Description] nvarchar(max) NULL,
                    CONSTRAINT [PK_AppSettings] PRIMARY KEY ([Key])
                )
            END
        ");

        // Create AssetNormalizations table if it doesn't exist
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AssetNormalizations')
            BEGIN
                CREATE TABLE [AssetNormalizations] (
                    [KrakenName] nvarchar(450) NOT NULL,
                    [NormalizedName] nvarchar(max) NOT NULL,
                    CONSTRAINT [PK_AssetNormalizations] PRIMARY KEY ([KrakenName])
                )
            END
        ");

        // Create PredictionResults table if it doesn't exist
        db.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PredictionResults')
            BEGIN
                CREATE TABLE [PredictionResults] (
                    [Symbol]           nvarchar(450) NOT NULL,
                    [Interval]         nvarchar(max) NOT NULL DEFAULT '',
                    [ComputedAt]       datetime2 NOT NULL,
                    [Status]           nvarchar(max) NOT NULL DEFAULT '',
                    [PredictedUp]      bit NOT NULL DEFAULT 0,
                    [Probability]      real NOT NULL DEFAULT 0,
                    [ModelAccuracy]    real NOT NULL DEFAULT 0,
                    [ModelAuc]         real NOT NULL DEFAULT 0,
                    [WalkForwardAccuracy] real NOT NULL DEFAULT 0,
                    [WalkForwardAuc]      real NOT NULL DEFAULT 0,
                    [WalkForwardFoldCount] int NOT NULL DEFAULT 0,
                    [LogRegAccuracy]   real NOT NULL DEFAULT 0,
                    [BenchmarkBuyHold] real NOT NULL DEFAULT 0,
                    [BenchmarkSma]     real NOT NULL DEFAULT 0,
                    [PredictedUp3]      bit NOT NULL DEFAULT 0,
                    [Probability3]      real NOT NULL DEFAULT 0,
                    [ModelAccuracy3]    real NOT NULL DEFAULT 0,
                    [ModelAuc3]         real NOT NULL DEFAULT 0,
                    [WalkForwardAccuracy3] real NOT NULL DEFAULT 0,
                    [WalkForwardAuc3]      real NOT NULL DEFAULT 0,
                    [WalkForwardFoldCount3] int NOT NULL DEFAULT 0,
                    [LogRegAccuracy3]   real NOT NULL DEFAULT 0,
                    [BenchmarkBuyHold3] real NOT NULL DEFAULT 0,
                    [BenchmarkSma3]     real NOT NULL DEFAULT 0,
                    [TrainSamples3]     int NOT NULL DEFAULT 0,
                    [TestSamples3]      int NOT NULL DEFAULT 0,
                    [PredictedUp6]      bit NOT NULL DEFAULT 0,
                    [Probability6]      real NOT NULL DEFAULT 0,
                    [ModelAccuracy6]    real NOT NULL DEFAULT 0,
                    [ModelAuc6]         real NOT NULL DEFAULT 0,
                    [WalkForwardAccuracy6] real NOT NULL DEFAULT 0,
                    [WalkForwardAuc6]      real NOT NULL DEFAULT 0,
                    [WalkForwardFoldCount6] int NOT NULL DEFAULT 0,
                    [LogRegAccuracy6]   real NOT NULL DEFAULT 0,
                    [BenchmarkBuyHold6] real NOT NULL DEFAULT 0,
                    [BenchmarkSma6]     real NOT NULL DEFAULT 0,
                    [TrainSamples6]     int NOT NULL DEFAULT 0,
                    [TestSamples6]      int NOT NULL DEFAULT 0,
                    [TrainSamples]     int NOT NULL DEFAULT 0,
                    [TestSamples]      int NOT NULL DEFAULT 0,
                    [TotalCandles]     int NOT NULL DEFAULT 0,
                    [ErrorMessage]     nvarchar(max) NULL,
                    CONSTRAINT [PK_PredictionResults] PRIMARY KEY ([Symbol])
                )
            END
        ");

        Log.Information("[AutoMigration] Created missing tables using direct SQL");
    }

    /// <summary>
    /// Applies database-level performance optimizations (idempotent).
    /// </summary>
    private static void ApplyPerformanceOptimizations(KrakenDbContext db)
    {
        EnsureNewFeatureColumns(db);
        EnsurePredictionResultColumns(db);

        try
        {
            // Enable forced parameterization to improve query plan caching
            var dbName = db.Database.GetDbConnection().Database;
#pragma warning disable EF1003
            db.Database.ExecuteSqlRaw("ALTER DATABASE " + QuoteSqlIdentifier(dbName) + " SET PARAMETERIZATION FORCED");
#pragma warning restore EF1003
            Log.Information("[AutoMigration] Forced parameterization enabled");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not set forced parameterization (may require elevated permissions)");
        }

        try
        {
            // Covering index on DerivedKlines for faster asset lookups
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IXEF_DerivedKlines_Asset_INCLUDE' AND object_id = OBJECT_ID('DerivedKlines'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IXEF_DerivedKlines_Asset_INCLUDE]
                    ON [dbo].[DerivedKlines] ([Asset])
                    INCLUDE ([OpenTime], [Open], [High], [Low], [Close], [Volume],
                             [VolumeWeightedAveragePrice], [TradeCount], [Interval])
                    WITH (FILLFACTOR = 90)
                END
            ");
            Log.Information("[AutoMigration] DerivedKlines covering index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create DerivedKlines index");
        }
    }

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

    private static void EnsureNewFeatureColumns(KrakenDbContext db)
    {
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
                        [DriftMinPct]    decimal(38,2) NOT NULL DEFAULT 5,
                        [AutoExecute]    bit NOT NULL DEFAULT 0,
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
            // DcaRules — smart/conditional columns
            db.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('DcaRules', 'ConditionalEnabled') IS NULL
                    ALTER TABLE [DcaRules] ADD [ConditionalEnabled] bit NOT NULL CONSTRAINT [DF_DcaRules_ConditionalEnabled] DEFAULT 0;
                IF COL_LENGTH('DcaRules', 'ConditionalMaPeriod') IS NULL
                    ALTER TABLE [DcaRules] ADD [ConditionalMaPeriod] int NOT NULL CONSTRAINT [DF_DcaRules_ConditionalMaPeriod] DEFAULT 20;
            ");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not add DcaRules conditional columns");
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
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not ensure PredictionResults walk-forward columns");
        }
    }

    private static string QuoteSqlIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier cannot be empty.", nameof(identifier));

        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }
}
