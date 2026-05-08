using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;
    private readonly IRecurringJobManager _jobManager;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(KrakenDbContext db, TradingStateService state, IRecurringJobManager jobManager, ILogger<SettingsController> logger)
    {
        _db = db;
        _state = state;
        _jobManager = jobManager;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<AppSettingsDto>> GetSettings()
    {
        try
        {
            var allSettings = await _db.AppSettings.AsNoTracking()
                .ToDictionaryAsync(s => s.Key, s => s.Value);
            string? Get(string key) => allSettings.TryGetValue(key, out var v) ? v : null;
            List<string> GetList(string key) => Get(key)
                ?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList()
                ?? new List<string>();
            bool GetBool(string key, bool defaultVal = false) =>
                Get(key) is { } v ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : defaultVal;

            var settings = new AppSettingsDto
            {
                BaseCurrencies = GetList("BaseCurrencies"),
                Blacklist = GetList("Blacklist"),
                MajorCoin = GetList("MajorCoin"),
                Currency = GetList("Currency"),
                BadPairs = GetList("BadPairs"),
                DefaultPairs = GetList("DefaultPairs"),
                AssetNormalizations = await _db.AssetNormalizations.AsNoTracking()
                    .ToDictionaryAsync(a => a.KrakenName, a => a.NormalizedName),
                KrakenApiKey = Get("KrakenApiKey") ?? "",
                KrakenApiSecret = Get("KrakenApiSecret") is { } sec ? MaskSecret(sec) : "",
                PushoverUserKey = Get("PushoverUserKey") is { } pu ? MaskSecret(pu) : "",
                PushoverApiToken = Get("PushoverAppToken") is { } pt ? MaskSecret(pt) : "",
                StakingNotifications = GetBool("StakingNotifications"),
                HideAlmostZeroBalances = GetBool("HideAlmostZeroBalances"),
                OrderProximityNotifications = Get("OrderProximityNotifications") is null || GetBool("OrderProximityNotifications", true),
                OrderProximityThreshold = decimal.TryParse(Get("OrderProximityThreshold"), System.Globalization.CultureInfo.InvariantCulture, out var threshold)
                    ? Math.Clamp(threshold, 0.1m, 20.0m) : 2.0m,
                Theme = Get("Theme") ?? "dark",
                OrderPriceOffsets = _state.OrderPriceOffsets,
                OrderQtyPercentages = _state.OrderQtyPercentages,
                AutoSellOnBuyFill = _state.AutoSellOnBuyFill,
                AutoSellPercentage = _state.AutoSellPercentage,
                AutoAddStakingToOrder = _state.AutoAddStakingToOrder,
                OrderBookDepth = _state.OrderBookDepth,
                StopLossEnabled = _state.StopLossEnabled,
                StopLossPct = _state.StopLossPct,
                TakeProfitEnabled = _state.TakeProfitEnabled,
                TakeProfitPct = _state.TakeProfitPct,
                DrawdownAlertEnabled = _state.DrawdownAlertEnabled,
                DrawdownAlertThreshold = _state.DrawdownAlertThreshold,
                DryRunJobs = _state.DryRunJobs,
                TrailingStopEnabled = _state.TrailingStopEnabled,
                TrailingStopPct = _state.TrailingStopPct,
                AutoCancelEnabled = _state.AutoCancelEnabled,
                AutoCancelDays = _state.AutoCancelDays,
                AutoCancelBuys = _state.AutoCancelBuys,
                AutoCancelSells = _state.AutoCancelSells,
                PriceDownloadTime = Get("PriceDownloadTime") ?? "04:00",
                PredictionJobTime = Get("PredictionJobTime") ?? "05:00",
                PredictionSymbols = Get("PredictionSymbols") ?? "XBT/USD,ETH/USD,SOL/USD",
                PredictionInterval = Get("PredictionInterval") ?? "OneHour",
                PredictionMode = Get("PredictionMode") ?? "specific",
                PredictionCurrency = Get("PredictionCurrency") ?? "USD",
                PredictionAutoRefreshIntervalMinutes = int.TryParse(Get("PredictionAutoRefreshIntervalMinutes"), out var autoMins) ? autoMins : 15,
            };

            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings");
            return StatusCode(500, "Error loading settings");
        }
    }

    [HttpPost]
    public async Task<ActionResult> SaveSettings([FromBody] SaveSettingsRequest request)
    {
        try
        {
            // Save Kraken API keys if provided (save to new AppSettings table)
            if (!string.IsNullOrEmpty(request.KrakenApiKey) && !request.KrakenApiKey.Contains("***"))
            {
                await SaveOrUpdateSetting("KrakenApiKey", request.KrakenApiKey, "Kraken API Key");
            }
            if (!string.IsNullOrEmpty(request.KrakenApiSecret) && !request.KrakenApiSecret.Contains("***"))
            {
                await SaveOrUpdateSetting("KrakenApiSecret", request.KrakenApiSecret, "Kraken API Secret");
            }

            // Save Pushover keys if provided
            if (!string.IsNullOrEmpty(request.PushoverUserKey) && !request.PushoverUserKey.Contains("***"))
            {
                await SaveOrUpdateSetting("PushoverUserKey", request.PushoverUserKey, "Pushover User Key");
            }
            if (!string.IsNullOrEmpty(request.PushoverApiToken) && !request.PushoverApiToken.Contains("***"))
            {
                await SaveOrUpdateSetting("PushoverAppToken", request.PushoverApiToken, "Pushover App Token");
            }

            // Save lists
            if (request.BaseCurrencies != null)
                await SaveSettingList("BaseCurrencies", request.BaseCurrencies);
            if (request.Blacklist != null)
                await SaveSettingList("Blacklist", request.Blacklist);
            if (request.MajorCoin != null)
                await SaveSettingList("MajorCoin", request.MajorCoin);
            if (request.Currency != null)
                await SaveSettingList("Currency", request.Currency);
            if (request.BadPairs != null)
                await SaveSettingList("BadPairs", request.BadPairs);
            if (request.DefaultPairs != null)
                await SaveSettingList("DefaultPairs", request.DefaultPairs);

            // Save boolean settings
            if (request.StakingNotifications.HasValue)
            {
                await SaveOrUpdateSetting("StakingNotifications", request.StakingNotifications.Value.ToString().ToLower(), "Send Pushover notifications for staking reward payments");
            }
            if (request.HideAlmostZeroBalances.HasValue)
            {
                await SaveOrUpdateSetting("HideAlmostZeroBalances", request.HideAlmostZeroBalances.Value.ToString().ToLower(), "Hide balance rows with less than 0.0001 units");
            }
            if (request.OrderProximityNotifications.HasValue)
            {
                await SaveOrUpdateSetting("OrderProximityNotifications", request.OrderProximityNotifications.Value.ToString().ToLower(), "Send Pushover notifications when an order is near the current price");
            }
            if (request.OrderProximityThreshold.HasValue)
            {
                var clamped = Math.Clamp(request.OrderProximityThreshold.Value, 0.1m, 20.0m);
                await SaveOrUpdateSetting("OrderProximityThreshold", clamped.ToString(System.Globalization.CultureInfo.InvariantCulture), "Percentage threshold for order proximity notifications (0.1 to 20.0)");
            }
            if (!string.IsNullOrEmpty(request.Theme))
            {
                var validTheme = request.Theme == "light" ? "light" : "dark";
                await SaveOrUpdateSetting("Theme", validTheme, "UI theme (dark or light)");
            }

            // Save order dialog button configs
            if (request.OrderPriceOffsets != null)
            {
                var value = string.Join(",", request.OrderPriceOffsets.Where(v => v > 0).Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                await SaveOrUpdateSetting("OrderPriceOffsets", value, "Percentage offset buttons for the order dialog price field");
            }
            if (request.OrderQtyPercentages != null)
            {
                var value = string.Join(",", request.OrderQtyPercentages.Where(v => v > 0 && v <= 100).Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                await SaveOrUpdateSetting("OrderQtyPercentages", value, "Percentage buttons for the order dialog quantity field");
            }

            // Save auto-sell settings
            if (request.AutoSellOnBuyFill.HasValue)
            {
                await SaveOrUpdateSetting("AutoSellOnBuyFill", request.AutoSellOnBuyFill.Value.ToString().ToLower(), "Automatically create a sell order when a buy order fills");
            }
            if (request.AutoSellPercentage.HasValue)
            {
                var clamped = Math.Clamp(request.AutoSellPercentage.Value, 1m, 500m);
                await SaveOrUpdateSetting("AutoSellPercentage", clamped.ToString(System.Globalization.CultureInfo.InvariantCulture), "Percentage above buy price for the automatic sell order (1 to 500)");
            }

            if (request.AutoAddStakingToOrder.HasValue)
            {
                await SaveOrUpdateSetting("AutoAddStakingToOrder", request.AutoAddStakingToOrder.Value.ToString().ToLower(), "Automatically add staking reward quantity to the newest open sell order for that asset");
            }

            if (request.StopLossEnabled.HasValue)
            {
                _state.StopLossEnabled = request.StopLossEnabled.Value;
                await SaveOrUpdateSetting("StopLossEnabled", request.StopLossEnabled.Value.ToString().ToLower(), "Automatically place a market sell when price drops below cost basis by StopLossPct");
            }
            if (request.StopLossPct.HasValue)
            {
                var pct = Math.Clamp(request.StopLossPct.Value, 1m, 99.9m);
                _state.StopLossPct = pct;
                await SaveOrUpdateSetting("StopLossPct", pct.ToString(System.Globalization.CultureInfo.InvariantCulture), "Stop-loss trigger percentage below average cost (1–99.9)");
            }
            if (request.TakeProfitEnabled.HasValue)
            {
                _state.TakeProfitEnabled = request.TakeProfitEnabled.Value;
                await SaveOrUpdateSetting("TakeProfitEnabled", request.TakeProfitEnabled.Value.ToString().ToLower(), "Automatically place a limit sell when price rises above cost basis by TakeProfitPct");
            }
            if (request.TakeProfitPct.HasValue)
            {
                var pct = Math.Clamp(request.TakeProfitPct.Value, 1m, 500m);
                _state.TakeProfitPct = pct;
                await SaveOrUpdateSetting("TakeProfitPct", pct.ToString(System.Globalization.CultureInfo.InvariantCulture), "Take-profit trigger percentage above average cost (1–500)");
            }
            if (request.DrawdownAlertEnabled.HasValue)
            {
                _state.DrawdownAlertEnabled = request.DrawdownAlertEnabled.Value;
                await SaveOrUpdateSetting("DrawdownAlertEnabled", request.DrawdownAlertEnabled.Value.ToString().ToLower(), "Send Pushover alert when portfolio drawdown exceeds threshold");
            }
            if (request.DrawdownAlertThreshold.HasValue)
            {
                var thr = Math.Clamp(request.DrawdownAlertThreshold.Value, 1m, 90m);
                _state.DrawdownAlertThreshold = thr;
                await SaveOrUpdateSetting("DrawdownAlertThreshold", thr.ToString(System.Globalization.CultureInfo.InvariantCulture), "Drawdown alert threshold percentage (1–90)");
            }
            if (request.DryRunJobs.HasValue)
            {
                _state.DryRunJobs = request.DryRunJobs.Value;
                await SaveOrUpdateSetting("DryRunJobs", request.DryRunJobs.Value.ToString().ToLower(), "Dry-run mode: scheduled jobs simulate orders and send Pushover notifications instead of placing real orders");
            }
            if (request.TrailingStopEnabled.HasValue)
            {
                _state.TrailingStopEnabled = request.TrailingStopEnabled.Value;
                await SaveOrUpdateSetting("TrailingStopEnabled", request.TrailingStopEnabled.Value.ToString().ToLower(), "Enable trailing stop-loss");
            }
            if (request.TrailingStopPct.HasValue)
            {
                var pct = Math.Clamp(request.TrailingStopPct.Value, 0.5m, 50m);
                _state.TrailingStopPct = pct;
                await SaveOrUpdateSetting("TrailingStopPct", pct.ToString(System.Globalization.CultureInfo.InvariantCulture), "Trailing stop-loss percentage drop from high (0.5–50)");
            }
            if (request.AutoCancelEnabled.HasValue)
            {
                _state.AutoCancelEnabled = request.AutoCancelEnabled.Value;
                await SaveOrUpdateSetting("AutoCancelEnabled", request.AutoCancelEnabled.Value.ToString().ToLower(), "Automatically cancel open orders older than AutoCancelDays");
            }
            if (request.AutoCancelDays.HasValue)
            {
                var days = Math.Clamp(request.AutoCancelDays.Value, 1, 365);
                _state.AutoCancelDays = days;
                await SaveOrUpdateSetting("AutoCancelDays", days.ToString(), "Age in days before an open order is auto-cancelled");
            }
            if (request.AutoCancelBuys.HasValue)
            {
                _state.AutoCancelBuys = request.AutoCancelBuys.Value;
                await SaveOrUpdateSetting("AutoCancelBuys", request.AutoCancelBuys.Value.ToString().ToLower(), "Auto-cancel applies to buy orders");
            }
            if (request.AutoCancelSells.HasValue)
            {
                _state.AutoCancelSells = request.AutoCancelSells.Value;
                await SaveOrUpdateSetting("AutoCancelSells", request.AutoCancelSells.Value.ToString().ToLower(), "Auto-cancel applies to sell orders");
            }

            if (request.OrderBookDepth.HasValue && TradingStateService.ValidBookDepths.Contains(request.OrderBookDepth.Value))
            {
                await SaveOrUpdateSetting("OrderBookDepth", request.OrderBookDepth.Value.ToString(), "Number of price levels shown in the order book (10, 25, 100, 500, 1000)");
            }

            // Save price download schedule and update Hangfire job
            if (!string.IsNullOrWhiteSpace(request.PriceDownloadTime))
            {
                var parts = request.PriceDownloadTime.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
                    && h is >= 0 and <= 23 && m is >= 0 and <= 59)
                {
                    var timeStr = $"{h:D2}:{m:D2}";
                    await SaveOrUpdateSetting("PriceDownloadTime", timeStr, "Daily price download time (HH:MM, 24-hour)");
                    var cron = $"{m} {h} * * *";
                    _jobManager.AddOrUpdate<DailyPriceRefreshJob>(
                        "daily-price-download",
                        job => job.ExecuteAsync(CancellationToken.None),
                        cron,
                        new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
                }
            }

            // Save prediction job schedule and update Hangfire job
            if (!string.IsNullOrWhiteSpace(request.PredictionJobTime))
            {
                var parts = request.PredictionJobTime.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
                    && h is >= 0 and <= 23 && m is >= 0 and <= 59)
                {
                    var timeStr = $"{h:D2}:{m:D2}";
                    await SaveOrUpdateSetting("PredictionJobTime", timeStr, "Daily ML prediction job time (HH:MM, 24-hour)");
                    var cron = $"{m} {h} * * *";
                    _jobManager.AddOrUpdate<PredictionJob>(
                        "daily-prediction",
                        job => job.ExecuteAsync(CancellationToken.None),
                        cron,
                        new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });
                }
            }

            // Save ML prediction settings
            if (!string.IsNullOrWhiteSpace(request.PredictionSymbols))
                await SaveOrUpdateSetting("PredictionSymbols", request.PredictionSymbols, "Comma-separated symbols for ML prediction job");

            var validIntervals = new[] { "OneMinute", "FiveMinutes", "FifteenMinutes", "ThirtyMinutes", "OneHour", "FourHour", "OneDay" };
            if (!string.IsNullOrWhiteSpace(request.PredictionInterval) && validIntervals.Contains(request.PredictionInterval))
                await SaveOrUpdateSetting("PredictionInterval", request.PredictionInterval, "Kline interval for ML training data");

            if (request.PredictionMode is "all" or "specific" or "existing")
                await SaveOrUpdateSetting("PredictionMode", request.PredictionMode, "Prediction symbol mode: 'specific', 'all', or 'existing'");

            if (!string.IsNullOrWhiteSpace(request.PredictionCurrency))
                await SaveOrUpdateSetting("PredictionCurrency", request.PredictionCurrency.Trim().ToUpper(), "Quote currency for 'all' prediction mode");

            if (request.PredictionAutoRefreshIntervalMinutes.HasValue)
            {
                var interval = Math.Max(5, request.PredictionAutoRefreshIntervalMinutes.Value);
                await SaveOrUpdateSetting("PredictionAutoRefreshIntervalMinutes", interval.ToString(),
                    "How often (minutes) the stale-prediction auto-refresh job runs (minimum 5)");
                _jobManager.AddOrUpdate<StalePredictionRefreshJob>(
                    "stale-prediction-refresh",
                    job => job.ExecuteAsync(CancellationToken.None),
                    IntervalToCron(interval),
                    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            }

            // Save asset normalizations
            if (request.AssetNormalizations != null)
            {
                var existing = await _db.AssetNormalizations.ToListAsync();
                _db.AssetNormalizations.RemoveRange(existing);
                foreach (var kvp in request.AssetNormalizations)
                {
                    _db.AssetNormalizations.Add(new AssetNormalization
                    {
                        KrakenName = kvp.Key,
                        NormalizedName = kvp.Value
                    });
                }
            }

            await _db.SaveChangesAsync();

            // Reload configuration in TradingStateService
            await _state.ReloadConfiguration(_db);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            return StatusCode(500, "Error saving settings");
        }
    }

    /// <summary>Builds a cron that fires at minute 1 and repeats every <paramref name="intervalMins"/> minutes within each hour.</summary>
    private static string IntervalToCron(int intervalMins)
    {
        intervalMins = Math.Max(1, intervalMins);
        var minutes = new List<int>();
        for (var m = 1; m < 60; m += intervalMins) minutes.Add(m);
        return $"{string.Join(",", minutes)} * * * *";
    }

    private async Task<List<string>?> GetSettingList(string key)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null) return null;
        return setting.Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    private async Task SaveSettingList(string key, List<string> values)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            setting = new AppSettings { Key = key };
            _db.AppSettings.Add(setting);
        }
        setting.Value = string.Join(",", values);
    }

    private async Task SaveOrUpdateSetting(string key, string value, string? description = null)
    {
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            setting = new AppSettings { Key = key, Description = description };
            _db.AppSettings.Add(setting);
        }
        setting.Value = value;
        if (description != null) setting.Description = description;
    }

    [HttpGet("pinned-pairs")]
    public async Task<ActionResult> GetPinnedPairs()
    {
        var list = await GetSettingList("PinnedPairs");
        return Ok(list ?? new List<string> { "XBT/USD", "ETH/USD", "SOL/USD" });
    }

    [HttpPut("pinned-pairs")]
    public async Task<ActionResult> SavePinnedPairs([FromBody] List<string> pairs)
    {
        await SaveSettingList("PinnedPairs", pairs);
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static string MaskSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 8) return "***";
        return secret[..4] + "***" + secret[^4..];
    }
}
