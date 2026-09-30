using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

/// <summary>Where the portfolio stands relative to its recent peak.</summary>
/// <param name="CurrentPct">How far the latest value is below the running peak, as a percentage (0 at a new high).</param>
public sealed record DrawdownStatus(decimal Peak, DateTime PeakDate, decimal Latest, DateTime LatestDate, decimal CurrentPct);

public class DrawdownAlertJob
{
    /// <summary>AppSettings key holding the drawdown % (0 if none) at which the user was last alerted.</summary>
    public const string LastAlertKey = "DrawdownLastAlertPct";

    /// <summary>How much further the drawdown must deepen before a still-open drawdown alerts again.</summary>
    public const decimal RealertAfterDeepeningBy = 5m;

    private readonly TradingStateService _state;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly INotifier _notify;
    private readonly ILogger<DrawdownAlertJob> _logger;

    public DrawdownAlertJob(TradingStateService state, IDbContextFactory<KrakenDbContext> dbFactory, INotifier notify, ILogger<DrawdownAlertJob> logger)
    {
        _state = state;
        _dbFactory = dbFactory;
        _notify = notify;
        _logger = logger;
    }

    /// <summary>
    /// The CURRENT drawdown: the latest snapshot against the highest value seen before it. (This used to report the deepest
    /// peak-to-trough fall anywhere in the window, so a dip that happened weeks ago and had fully recovered kept alerting
    /// every morning until it aged out.) Snapshots of zero are ignored — they are missing data, not a portfolio that vanished.
    /// </summary>
    internal static DrawdownStatus? Current(IEnumerable<(DateTime Date, decimal TotalUsd)> snapshots)
    {
        var ordered = snapshots.Where(s => s.TotalUsd > 0).OrderBy(s => s.Date).ToList();
        if (ordered.Count == 0) return null;

        var peak = ordered[0];
        foreach (var s in ordered)
            if (s.TotalUsd > peak.TotalUsd) peak = s;

        var latest = ordered[^1];
        var pct = peak.TotalUsd > 0 ? (peak.TotalUsd - latest.TotalUsd) / peak.TotalUsd * 100m : 0m;
        return new DrawdownStatus(peak.TotalUsd, peak.Date, latest.TotalUsd, latest.Date, Math.Max(pct, 0m));
    }

    /// <summary>
    /// Alert once when the drawdown crosses the threshold, and again only if it deepens by
    /// <see cref="RealertAfterDeepeningBy"/> points; never while merely staying in the same drawdown. <paramref name="lastAlertedPct"/>
    /// is null when the portfolio has been back under the threshold (the alert is re-armed).
    /// </summary>
    internal static bool ShouldAlert(decimal currentPct, decimal threshold, decimal? lastAlertedPct)
    {
        if (currentPct < threshold) return false;
        return lastAlertedPct == null || currentPct >= lastAlertedPct.Value + RealertAfterDeepeningBy;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        if (!_state.DrawdownAlertEnabled) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var since = DateTime.UtcNow.Date.AddDays(-90);
        var snapshots = (await db.PortfolioSnapshots
                .Where(s => s.Date >= since)
                .Select(s => new { s.Date, s.TotalUsd })
                .ToListAsync(ct))
            .Select(s => (s.Date, s.TotalUsd)).ToList();

        if (snapshots.Count(s => s.TotalUsd > 0) < 5) return;

        var status = Current(snapshots);
        if (status == null) return;

        var threshold = _state.DrawdownAlertThreshold;
        _logger.LogInformation("[DrawdownAlert] Current drawdown: {Dd:F1}% from the {Peak:yyyy-MM-dd} peak (threshold {T:F1}%)", status.CurrentPct, status.PeakDate, threshold);

        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == LastAlertKey, ct);
        decimal? last = row != null && decimal.TryParse(row.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var l) && l > 0 ? l : null;

        if (status.CurrentPct < threshold)
        {
            // Back under the threshold: re-arm, so the next fall alerts again
            if (last != null && row != null)
            {
                row.Value = "0";
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        if (!ShouldAlert(status.CurrentPct, threshold, last)) return;

        await _notify.Pushover(
            $"Portfolio Drawdown Alert — {status.CurrentPct:F1}%",
            $"Portfolio is {status.CurrentPct:F1}% below its peak of ${status.Peak:N0} on {status.PeakDate:yyyy-MM-dd} (now ${status.Latest:N0}). Threshold: {threshold:F1}%. " +
            "Note: withdrawals also lower this figure.");

        var text = status.CurrentPct.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (row == null) db.AppSettings.Add(new AppSettings { Key = LastAlertKey, Value = text, Description = "Drawdown % at which the last alert was sent (managed automatically)" });
        else row.Value = text;
        await db.SaveChangesAsync(ct);
    }
}
