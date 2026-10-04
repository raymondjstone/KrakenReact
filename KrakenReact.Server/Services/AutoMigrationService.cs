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
public static partial class AutoMigrationService
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
            catch (Exception ex) { Log.Debug(ex, "[Migration] AssetNormalizations table not readable - it will be created"); }

            // Check if PredictionResults table exists
            var canConnectToPredictions = false;
            try
            {
                canConnectToPredictions = db.PredictionResults.Any();
            }
            catch (Exception ex) { Log.Debug(ex, "[Migration] PredictionResults table not readable - it will be created"); }

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

    private static string QuoteSqlIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier cannot be empty.", nameof(identifier));

        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    private static string QuoteSqlLiteral(string value)
        => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
