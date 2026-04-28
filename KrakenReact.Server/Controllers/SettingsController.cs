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
            // After initialization, all settings should exist in database
            var settings = new AppSettingsDto
            {
                BaseCurrencies = await GetSettingList("BaseCurrencies") ?? new List<string>(),
                Blacklist = await GetSettingList("Blacklist") ?? new List<string>(),
                MajorCoin = await GetSettingList("MajorCoin") ?? new List<string>(),
                Currency = await GetSettingList("Currency") ?? new List<string>(),
                BadPairs = await GetSettingList("BadPairs") ?? new List<string>(),
                DefaultPairs = await GetSettingList("DefaultPairs") ?? new List<string>(),
                AssetNormalizations = await _db.AssetNormalizations.ToDictionaryAsync(a => a.KrakenName, a => a.NormalizedName)
            };

            // Get Kraken API keys
            var krakenApiKey = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "KrakenApiKey");
            var krakenApiSecret = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "KrakenApiSecret");
            settings.KrakenApiKey = krakenApiKey?.Value ?? "";
            settings.KrakenApiSecret = krakenApiSecret != null ? MaskSecret(krakenApiSecret.Value) : "";

            // Get Pushover keys
            var pushoverUser = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PushoverUserKey");
            var pushoverToken = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PushoverAppToken");
            settings.PushoverUserKey = pushoverUser != null ? MaskSecret(pushoverUser.Value) : "";
            settings.PushoverApiToken = pushoverToken != null ? MaskSecret(pushoverToken.Value) : "";

            // Get boolean settings
            var stakingNotif = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "StakingNotifications");
            settings.StakingNotifications = stakingNotif != null && string.Equals(stakingNotif.Value, "true", StringComparison.OrdinalIgnoreCase);

            var hideAlmostZero = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "HideAlmostZeroBalances");
            settings.HideAlmostZeroBalances = hideAlmostZero != null && string.Equals(hideAlmostZero.Value, "true", StringComparison.OrdinalIgnoreCase);

            var orderProxNotif = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "OrderProximityNotifications");
            settings.OrderProximityNotifications = orderProxNotif == null || string.Equals(orderProxNotif.Value, "true", StringComparison.OrdinalIgnoreCase);

            var orderProxThreshold = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "OrderProximityThreshold");
            settings.OrderProximityThreshold = orderProxThreshold != null && decimal.TryParse(orderProxThreshold.Value, System.Globalization.CultureInfo.InvariantCulture, out var threshold)
                ? Math.Clamp(threshold, 0.1m, 20.0m) : 2.0m;

            var themeSetting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "Theme");
            settings.Theme = themeSetting?.Value ?? "dark";

            settings.OrderPriceOffsets = _state.OrderPriceOffsets;
            settings.OrderQtyPercentages = _state.OrderQtyPercentages;
            settings.AutoSellOnBuyFill = _state.AutoSellOnBuyFill;
            settings.AutoSellPercentage = _state.AutoSellPercentage;
            settings.AutoAddStakingToOrder = _state.AutoAddStakingToOrder;
            settings.OrderBookDepth = _state.OrderBookDepth;

            var priceDownloadTime = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PriceDownloadTime");
            settings.PriceDownloadTime = priceDownloadTime?.Value ?? "04:00";

            var predJobTime = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionJobTime");
            settings.PredictionJobTime = predJobTime?.Value ?? "05:00";

            var predSymbols = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionSymbols");
            settings.PredictionSymbols = predSymbols?.Value ?? "XBT/USD,ETH/USD,SOL/USD";

            var predInterval = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionInterval");
            settings.PredictionInterval = predInterval?.Value ?? "OneHour";

            var predMode = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionMode");
            settings.PredictionMode = predMode?.Value ?? "specific";

            var predCurrency = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionCurrency");
            settings.PredictionCurrency = predCurrency?.Value ?? "USD";

            var predAutoRefresh = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionAutoRefreshIntervalMinutes");
            settings.PredictionAutoRefreshIntervalMinutes = predAutoRefresh != null && int.TryParse(predAutoRefresh.Value, out var autoMins)
                ? autoMins : 15;

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
