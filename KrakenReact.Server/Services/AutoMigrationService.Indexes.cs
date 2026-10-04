using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Serilog;

namespace KrakenReact.Server.Services;

/// <summary>Performance tuning and the DerivedKlines index maintenance.</summary>
public static partial class AutoMigrationService
{
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
            // READ_COMMITTED_SNAPSHOT lets readers see the last committed row version instead of
            // waiting for write locks to release. Eliminates the 120-second read timeouts that
            // occur when prediction write batches hold row/page locks.
            var dbNameRcsi = db.Database.GetDbConnection().Database;
#pragma warning disable EF1003
            db.Database.ExecuteSqlRaw(@"
                IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = " + QuoteSqlLiteral(dbNameRcsi) + @") = 0
                BEGIN
                    ALTER DATABASE " + QuoteSqlIdentifier(dbNameRcsi) + @" SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE
                END");
#pragma warning restore EF1003
            Log.Information("[AutoMigration] READ_COMMITTED_SNAPSHOT isolation ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not enable READ_COMMITTED_SNAPSHOT (may require elevated permissions)");
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

        try
        {
            // Interval was nvarchar(max) which SQL Server rejects as an index key column.
            // Must drop the auto-generated default constraint and any index that INCLUDEs
            // the column before ALTER COLUMN can change its type.
            db.Database.ExecuteSqlRaw(@"
                IF EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('DerivedKlines')
                      AND name = 'Interval'
                      AND max_length = -1
                )
                BEGIN
                    -- Drop auto-generated default constraint (name is not deterministic)
                    DECLARE @dfName nvarchar(256)
                    SELECT @dfName = dc.name
                    FROM sys.default_constraints dc
                    JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
                    WHERE c.object_id = OBJECT_ID('DerivedKlines') AND c.name = 'Interval'
                    IF @dfName IS NOT NULL
                        EXEC('ALTER TABLE [dbo].[DerivedKlines] DROP CONSTRAINT [' + @dfName + ']')

                    -- Drop covering index that INCLUDEs Interval (will be recreated below)
                    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IXEF_DerivedKlines_Asset_INCLUDE' AND object_id = OBJECT_ID('DerivedKlines'))
                        DROP INDEX [IXEF_DerivedKlines_Asset_INCLUDE] ON [dbo].[DerivedKlines]

                    ALTER TABLE [dbo].[DerivedKlines] ALTER COLUMN [Interval] nvarchar(50) NOT NULL
                END
            ");

            // Composite index on (Asset, Interval) — queries that filter on both columns
            // were doing a full asset-range scan because Interval was only an INCLUDE column.
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DerivedKlines_Asset_Interval' AND object_id = OBJECT_ID('DerivedKlines'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_DerivedKlines_Asset_Interval]
                    ON [dbo].[DerivedKlines] ([Asset], [Interval])
                    INCLUDE ([OpenTime], [Open], [High], [Low], [Close], [Volume],
                             [VolumeWeightedAveragePrice], [TradeCount])
                    WITH (FILLFACTOR = 90)
                END
            ");
            Log.Information("[AutoMigration] DerivedKlines Asset+Interval index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create DerivedKlines Asset+Interval index");
        }

        try
        {
            // Index on ScheduledOrders(Status, ScheduledAt) — the job polls this every minute
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ScheduledOrders_Status_ScheduledAt' AND object_id = OBJECT_ID('ScheduledOrders'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_ScheduledOrders_Status_ScheduledAt]
                    ON [ScheduledOrders] ([Status], [ScheduledAt])
                END
            ");
            Log.Information("[AutoMigration] ScheduledOrders index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create ScheduledOrders index");
        }

        try
        {
            // AlertLogs(CreatedAt DESC) — COUNT(*) and TOP N ORDER BY CreatedAt DESC were doing full scans
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AlertLogs_CreatedAt' AND object_id = OBJECT_ID('AlertLogs'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_AlertLogs_CreatedAt]
                    ON [AlertLogs] ([CreatedAt] DESC)
                END
            ");
            Log.Information("[AutoMigration] AlertLogs index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create AlertLogs index");
        }

        try
        {
            // ProfitLadderRules(Active) — job filters WHERE Active = 1 on every tick
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ProfitLadderRules_Active' AND object_id = OBJECT_ID('ProfitLadderRules'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_ProfitLadderRules_Active]
                    ON [ProfitLadderRules] ([Active])
                END
            ");
            Log.Information("[AutoMigration] ProfitLadderRules index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create ProfitLadderRules index");
        }

        try
        {
            // AutoRepriceRules(Active) — job filters WHERE Active = 1 on every tick
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AutoRepriceRules_Active' AND object_id = OBJECT_ID('AutoRepriceRules'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_AutoRepriceRules_Active]
                    ON [AutoRepriceRules] ([Active])
                END
            ");
            Log.Information("[AutoMigration] AutoRepriceRules index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create AutoRepriceRules index");
        }

        try
        {
            // PriceAlerts(Active) — job filters WHERE Active = 1 on every tick
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PriceAlerts_Active' AND object_id = OBJECT_ID('PriceAlerts'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_PriceAlerts_Active]
                    ON [PriceAlerts] ([Active])
                END
            ");
            Log.Information("[AutoMigration] PriceAlerts index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create PriceAlerts index");
        }

        try
        {
            // PredictionHistories(Symbol) — COUNT(*) WHERE Symbol = @symbol was scanning the full table
            // Also ensure the composite (Symbol, ComputedAt) index exists for history queries
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PredictionHistories_Symbol' AND object_id = OBJECT_ID('PredictionHistories'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_PredictionHistories_Symbol]
                    ON [PredictionHistories] ([Symbol])
                END
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PredictionHistories_Symbol_ComputedAt' AND object_id = OBJECT_ID('PredictionHistories'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_PredictionHistories_Symbol_ComputedAt]
                    ON [PredictionHistories] ([Symbol], [ComputedAt])
                END
            ");
            Log.Information("[AutoMigration] PredictionHistories indexes ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create PredictionHistories indexes");
        }

        try
        {
            // PriceSnapshots(CapturedAt) — DELETE WHERE CapturedAt < cutoff scans without a
            // leading-CapturedAt index; the composite (Symbol,CapturedAt) has Symbol first so
            // it cannot be used for a range delete over CapturedAt alone.
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PriceSnapshots_CapturedAt' AND object_id = OBJECT_ID('PriceSnapshots'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_PriceSnapshots_CapturedAt]
                    ON [PriceSnapshots] ([CapturedAt])
                END
            ");
            Log.Information("[AutoMigration] PriceSnapshots CapturedAt index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create PriceSnapshots CapturedAt index");
        }

        EnsureDerivedKlineIndexes(db);
    }

    /// <summary>
    /// Reshapes the DerivedKlines indexes around the query the app actually runs.
    /// <para>
    /// Every read filters on Asset and Interval and orders by OpenTime, but no index led with that
    /// combination, so the server seeked on (Asset, Interval) and then jumped back to the clustered
    /// index for the OHLCV columns — measured at fifteen thousand key lookups. Two further indexes on
    /// Asset alone had recorded no seeks at all while occupying 286 MB and being maintained on every
    /// insert, which now happens continuously as minute candles arrive.
    /// </para>
    /// </summary>
    private static void EnsureDerivedKlineIndexes(KrakenDbContext db)
    {
        try
        {
            // Covering: the seek keys in order, and every column a read wants carried along, so the
            // query is answered from this index alone.
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DerivedKlines_Asset_Interval_OpenTime' AND object_id = OBJECT_ID('DerivedKlines'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [IX_DerivedKlines_Asset_Interval_OpenTime]
                    ON [DerivedKlines] ([Asset], [Interval], [OpenTime])
                    INCLUDE ([Open], [High], [Low], [Close], [Volume], [VolumeWeightedAveragePrice], [TradeCount])
                END
            ");
            Log.Information("[AutoMigration] DerivedKlines covering index ensured");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AutoMigration] Could not create DerivedKlines covering index");
        }

        // Only drop the redundant ones once the replacement is in place, so a failure part-way
        // through never leaves the table worse indexed than it started.
        //
        // IX_DerivedKlines_Asset_Interval is a strict prefix of the new index, so every seek it
        // served is served there too, and covered rather than needing a key lookup.
        // IX_DerivedKlines_Asset_OpenTime is deliberately kept: it is not a prefix of anything, and
        // GetKlineAsync filters on Asset alone while ordering by OpenTime, which it answers directly.
        foreach (var name in new[] { "IX_DerivedKlines_Asset", "IXEF_DerivedKlines_Asset_INCLUDE", "IX_DerivedKlines_Asset_Interval" })
        {
            try
            {
                // EF1002: `name` comes from the hard-coded array above, never from input, so interpolating it is safe.
#pragma warning disable EF1002
                db.Database.ExecuteSqlRaw($@"
                    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}' AND object_id = OBJECT_ID('DerivedKlines'))
                       AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DerivedKlines_Asset_Interval_OpenTime' AND object_id = OBJECT_ID('DerivedKlines'))
                    BEGIN
                        DROP INDEX [{name}] ON [DerivedKlines]
                    END
                ");
#pragma warning restore EF1002
                Log.Information("[AutoMigration] Dropped redundant index {Index}", name);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AutoMigration] Could not drop redundant index {Index}", name);
            }
        }
    }
}
