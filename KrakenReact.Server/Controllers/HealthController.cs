using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;

    private readonly Func<Hangfire.Storage.IMonitoringApi> _monitoringApi;

    /// <param name="monitoringApi">Seam for tests; defaults to the live Hangfire storage, which throws when none is configured.</param>
    public HealthController(KrakenDbContext db, TradingStateService state, Func<Hangfire.Storage.IMonitoringApi>? monitoringApi = null)
    {
        _db = db;
        _state = state;
        _monitoringApi = monitoringApi ?? (() => Hangfire.JobStorage.Current.GetMonitoringApi());
    }

    /// <summary>Summarises Hangfire's newest failed jobs (as returned by FailedJobs) for the last 24 hours.</summary>
    public static (bool Ok, string Detail) SummariseFailedJobs(IEnumerable<KeyValuePair<string, Hangfire.Storage.Monitoring.FailedJobDto>> failed, DateTime nowUtc, int pageSize = 50)
    {
        var list = failed.ToList();
        var cutoff = nowUtc.AddHours(-24);
        var recent = list.Where(j => j.Value?.FailedAt >= cutoff).ToList();
        if (recent.Count == 0) return (true, "No failed jobs in the last 24h");
        var last = recent[0].Value;
        var count = recent.Count >= pageSize ? $"{pageSize}+" : recent.Count.ToString();
        return (false, $"{count} failed in the last 24h - latest: {last.ExceptionType} {last.ExceptionMessage}".Trim());
    }

    /// <summary>
    /// Minute candles can never be backfilled (Kraken serves ~12 hours), so a stalled collector means permanent holes.
    /// Judged from the job's own last completed pass; the interval is clamped to at most 4 hours, so 6 hours without a pass is a stall.
    /// </summary>
    public static (bool Ok, string Detail) SummariseMinuteCollection(DateTime? lastCompletedUtc, int gapPairs, DateTime nowUtc, TimeSpan uptime)
    {
        if (lastCompletedUtc is null)
            return uptime < TimeSpan.FromMinutes(30)
                ? (true, "Waiting for the first collection pass since startup")
                : (false, $"No collection pass has completed in the {uptime.TotalHours:F1}h since startup - candles Kraken no longer serves are being lost");
        var age = nowUtc - lastCompletedUtc.Value;
        if (age > TimeSpan.FromHours(6))
            return (false, $"Last pass finished {age.TotalHours:F1}h ago - Kraken only serves ~12h, so older gaps cannot be recovered");
        if (gapPairs > 0)
            return (false, $"Last pass finished {age.TotalMinutes:F0} min ago but {gapPairs} pair(s) have an unrecoverable gap");
        return (true, $"Last pass finished {age.TotalMinutes:F0} min ago");
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var checks = new List<object>();

        // 1. Database connectivity
        bool dbOk = false;
        string dbMsg = "";
        try
        {
            var count = await _db.AppSettings.CountAsync();
            dbOk = true;
            dbMsg = $"{count} settings rows";
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Health check (database) failed"); dbMsg = "Check failed - see the server log"; }
        checks.Add(new { name = "Database", ok = dbOk, detail = dbMsg });

        // 2. Symbols loaded
        var symbolCount = _state.Symbols.Count;
        checks.Add(new { name = "Symbols", ok = symbolCount > 0, detail = $"{symbolCount} symbols loaded" });

        // 3. Live price feed — judged by the ticker heartbeat, the same signal the trading jobs use to decide whether they may act.
        // (This used to look at the newest KrakenNewPricesLoadedTime of any price, which the daily and startup loaders also
        // bump, so it reported "live" for ten minutes after a loader ran even with the websocket dead.)
        var feedAge = _state.FeedAge;
        var priceOk = _state.IsPriceFeedAlive();
        checks.Add(new
        {
            name = "Live Prices",
            ok = priceOk,
            detail = priceOk
                ? $"Last tick {feedAge!.Value.TotalSeconds:F0}s ago"
                : feedAge == null
                    ? "No price ticks received yet — trading jobs are paused until the feed delivers"
                    : $"Stale — last tick {feedAge.Value.TotalMinutes:F1} min ago. Stop-loss, take-profit, MicroTrade, DCA and other price-driven jobs are paused until it recovers"
        });

        // 4. Balances loaded
        var balCount = _state.Balances.Count;
        checks.Add(new { name = "Balances", ok = balCount > 0, detail = $"{balCount} balance entries" });

        // 5. ML predictions freshness — check DB for recent PredictionResults
        bool predOk = false;
        string predMsg = "";
        try
        {
            var latestPred = await _db.PredictionResults.AsNoTracking()
                .OrderByDescending(p => p.ComputedAt)
                .Select(p => new { p.Symbol, p.ComputedAt })
                .FirstOrDefaultAsync();
            if (latestPred == null)
            {
                predMsg = "No predictions found";
            }
            else
            {
                var ageH = (DateTime.UtcNow - latestPred.ComputedAt).TotalHours;
                predOk = ageH < 26;
                predMsg = $"{latestPred.Symbol} {ageH:F1}h ago";
            }
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Health check (predictions) failed"); predMsg = "Check failed - see the server log"; }
        checks.Add(new { name = "ML Predictions", ok = predOk, detail = predMsg });

        // 6. Portfolio snapshots — latest snapshot within 26 hours
        bool snapOk = false;
        string snapMsg = "";
        try
        {
            var latest = await _db.PortfolioSnapshots.AsNoTracking()
                .OrderByDescending(s => s.Date)
                .Select(s => new { s.Date, s.TotalUsd })
                .FirstOrDefaultAsync();
            if (latest == null)
            {
                snapMsg = "No snapshots found";
            }
            else
            {
                var ageH = (DateTime.UtcNow.Date - latest.Date).TotalHours;
                snapOk = ageH < 26;
                snapMsg = $"${latest.TotalUsd:N0} on {latest.Date:yyyy-MM-dd} ({ageH:F0}h ago)";
            }
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Health check (snapshots) failed"); snapMsg = "Check failed - see the server log"; }
        checks.Add(new { name = "Portfolio Snapshot", ok = snapOk, detail = snapMsg });

        // Keys are the first thing a new install lacks, and without them every trading and sync job fails
        var krakenKeys = false;
        var pushoverKeys = false;
        try
        {
            var settings = await _db.AppSettings.AsNoTracking()
                .Where(s => s.Key == "KrakenApiKey" || s.Key == "KrakenApiSecret" || s.Key == "PushoverUserKey" || s.Key == "PushoverAppToken")
                .ToDictionaryAsync(s => s.Key, s => s.Value);
            bool Has(string key) => settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v);
            krakenKeys = (Has("KrakenApiKey") && Has("KrakenApiSecret")) || await _db.AppCreds.AnyAsync(c => c.id == "KrakenDefault");
            pushoverKeys = Has("PushoverUserKey") && Has("PushoverAppToken");
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Health check (credentials) failed"); }
        checks.Add(new
        {
            name = "Kraken API Keys",
            ok = krakenKeys,
            detail = krakenKeys ? "Saved" : "Not set - add them on the Settings page; trading and sync jobs cannot run without them"
        });
        // Optional: alerts still appear in the in-app alert log without Pushover, so this never fails the overall check
        checks.Add(new
        {
            name = "Pushover",
            ok = true,
            detail = pushoverKeys ? "Configured" : "Not configured - alerts are kept in the in-app log only"
        });

        // Background jobs: failed Hangfire jobs are otherwise only visible on the dashboard. Newest 50 is one cheap
        // query, and "50+" is already as alarming as it needs to be. Skipped when Hangfire storage isn't available.
        try
        {
            var (jobsOk, jobsDetail) = SummariseFailedJobs(_monitoringApi().FailedJobs(0, 50), DateTime.UtcNow);
            checks.Add(new { name = "Background Jobs", ok = jobsOk, detail = jobsDetail });
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "Health check (Hangfire jobs) skipped"); }

        // Minute candles: only when collection is switched on
        try
        {
            var minuteSetting = await _db.AppSettings.AsNoTracking()
                .Where(s => s.Key == "MinuteCandleCollectionEnabled").Select(s => s.Value).FirstOrDefaultAsync();
            if (!string.Equals(minuteSetting, "false", StringComparison.OrdinalIgnoreCase))
            {
                var uptime = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
                var (minOk, minDetail) = SummariseMinuteCollection(MinuteCandleJob.LastCompletedUtc, MinuteCandleJob.LastGapPairCount, DateTime.UtcNow, uptime);
                checks.Add(new { name = "Minute Candles", ok = minOk, detail = minDetail });
            }
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Health check (minute candles) failed"); }

        // 7. Initial data load complete
        checks.Add(new
        {
            name = "Initial Load",
            ok = !_state.InitialDataLoad,
            detail = _state.InitialDataLoad ? "Still loading…" : "Complete"
        });

        var allOk = checks.Cast<dynamic>().All(c => (bool)c.ok);
        return Ok(new
        {
            ok = allOk,
            checkedAt = DateTime.UtcNow,
            checks
        });
    }
}
