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
                    [LogRegAccuracy]   real NOT NULL DEFAULT 0,
                    [BenchmarkBuyHold] real NOT NULL DEFAULT 0,
                    [BenchmarkSma]     real NOT NULL DEFAULT 0,
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
        try
        {
            // Enable forced parameterization to improve query plan caching
            var dbName = db.Database.GetDbConnection().Database;
            db.Database.ExecuteSqlRaw($"ALTER DATABASE [{dbName}] SET PARAMETERIZATION FORCED");
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
}
