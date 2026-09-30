using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public partial class TradingStateService
{
    /// <summary>Seeds database with default settings on first run</summary>
    public async Task InitializeDefaultSettings(Data.KrakenDbContext db)
    {
        // Check if any settings exist
        var hasSettings = await db.AppSettings.AnyAsync();
        if (hasSettings) return; // Already initialized

        // Seed all default settings as actual database records
        var defaultSettings = new List<AppSettings>
        {
            new() { Key = "BaseCurrencies", Value = string.Join(",", DefaultBaseCurrencies), Description = "Base currencies for trading pairs" },
            new() { Key = "Blacklist", Value = string.Join(",", DefaultBlacklist), Description = "Blacklisted assets" },
            new() { Key = "MajorCoin", Value = string.Join(",", DefaultMajorCoin), Description = "Major coins" },
            new() { Key = "Currency", Value = string.Join(",", DefaultCurrency), Description = "Fiat currencies" },
            new() { Key = "BadPairs", Value = string.Join(",", DefaultBadPairs), Description = "Excluded trading pairs" },
            new() { Key = "DefaultPairs", Value = string.Join(",", DefaultDefaultPairs), Description = "Default trading pairs to load" },
            new() { Key = "KrakenApiKey", Value = "", Description = "Kraken API Key" },
            new() { Key = "KrakenApiSecret", Value = "", Description = "Kraken API Secret" },
            new() { Key = "PushoverUserKey", Value = "", Description = "Pushover User Key" },
            new() { Key = "PushoverAppToken", Value = "", Description = "Pushover App Token" },
            new() { Key = "StakingNotifications", Value = "false", Description = "Send Pushover notifications for staking reward payments" },
            new() { Key = "HideAlmostZeroBalances", Value = "false", Description = "Hide balance rows with less than 0.0001 units or less than $0.01 value" },
            new() { Key = "OrderProximityNotifications", Value = "true", Description = "Send Pushover notifications when an order is near the current price" },
            new() { Key = "OrderProximityThreshold", Value = "2.0", Description = "Percentage threshold for order proximity notifications (0.1 to 20.0)" },
            new() { Key = "Theme", Value = "dark", Description = "UI theme (dark or light)" },
            new() { Key = "PriceDownloadTime", Value = "04:00", Description = "Daily price download time (HH:MM, 24-hour)" }
        };

        await db.AppSettings.AddRangeAsync(defaultSettings);

        // Seed asset normalizations
        var normalizations = AssetAliases.Select(kvp => new AssetNormalization
        {
            KrakenName = kvp.Key,
            NormalizedName = kvp.Value
        }).ToList();

        await db.AssetNormalizations.AddRangeAsync(normalizations);

        await db.SaveChangesAsync();
    }

    /// <summary>Migrates API credentials from old EFAppCreds table to new AppSettings table on first run</summary>
    public async Task MigrateApiCredentials(Data.KrakenDbContext db)
    {
        // Get existing keys (they should exist after InitializeDefaultSettings)
        var krakenKey = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "KrakenApiKey");
        var krakenSecret = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "KrakenApiSecret");
        var pushoverUser = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PushoverUserKey");
        var pushoverToken = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PushoverAppToken");

        // If keys already have values, don't overwrite
        if (!string.IsNullOrEmpty(krakenKey?.Value) && !string.IsNullOrEmpty(pushoverUser?.Value))
            return;

        // Try to get credentials from old EFAppCreds table
        var krakenCreds = await db.AppCreds.FirstOrDefaultAsync(c => c.id == "kraken");
        var pushoverCreds = await db.AppCreds.FirstOrDefaultAsync(c => c.id == "pushover");

        // Migrate Kraken credentials if they exist and current values are empty
        if (krakenCreds != null && krakenKey != null && string.IsNullOrEmpty(krakenKey.Value))
        {
            krakenKey.Value = krakenCreds.appkey;
            krakenKey.Description = "Kraken API Key (migrated from EFAppCreds)";

            if (krakenSecret != null)
            {
                krakenSecret.Value = krakenCreds.appsecret;
                krakenSecret.Description = "Kraken API Secret (migrated from EFAppCreds)";
            }
        }

        // Migrate Pushover credentials if they exist and current values are empty
        if (pushoverCreds != null && pushoverUser != null && string.IsNullOrEmpty(pushoverUser.Value))
        {
            pushoverUser.Value = pushoverCreds.appkey;
            pushoverUser.Description = "Pushover User Key (migrated from EFAppCreds)";

            if (pushoverToken != null)
            {
                pushoverToken.Value = pushoverCreds.appsecret;
                pushoverToken.Description = "Pushover App Token (migrated from EFAppCreds)";
            }
        }

        // Save changes if any credentials were migrated
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Seeds missing default asset normalizations and flattens any alias chains in the database.</summary>
    public async Task SyncAssetNormalizations(Data.KrakenDbContext db)
    {
        var existing = await db.AssetNormalizations.ToDictionaryAsync(a => a.KrakenName, a => a, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        // Seed missing defaults AND fix any stale/corrupted default entries in the DB.
        // DefaultAssetAliases are authoritative (e.g. XXBT→BTC must not become XXBT→XBT).
        foreach (var kvp in DefaultAssetAliases)
        {
            if (!existing.TryGetValue(kvp.Key, out var existingEntry))
            {
                db.AssetNormalizations.Add(new AssetNormalization { KrakenName = kvp.Key, NormalizedName = kvp.Value });
                changed = true;
            }
            else if (!existingEntry.NormalizedName.Equals(kvp.Value, StringComparison.OrdinalIgnoreCase))
            {
                // DB has wrong value for a core alias — correct it
                existingEntry.NormalizedName = kvp.Value;
                changed = true;
            }
        }

        if (changed) await db.SaveChangesAsync();

        // Flatten any alias chains (e.g. XXBT→XBT, XBT→BTC  becomes  XXBT→BTC, XBT→BTC)
        var allEntries = await db.AssetNormalizations.ToListAsync();
        var lookup = allEntries.ToDictionary(a => a.KrakenName, a => a.NormalizedName, StringComparer.OrdinalIgnoreCase);
        var flattened = false;
        foreach (var entry in allEntries)
        {
            var resolved = entry.NormalizedName;
            for (int i = 0; i < 5; i++)
            {
                if (lookup.TryGetValue(resolved, out var next) && next != resolved)
                    resolved = next;
                else
                    break;
            }
            if (resolved != entry.NormalizedName)
            {
                entry.NormalizedName = resolved;
                flattened = true;
            }
        }
        if (flattened) await db.SaveChangesAsync();
    }

    /// <summary>Reloads configuration from database</summary>
    public async Task ReloadConfiguration(Data.KrakenDbContext db)
    {
        var baseCurrencies = await GetSettingList(db, "BaseCurrencies");
        if (baseCurrencies != null && baseCurrencies.Any()) BaseCurrencies = baseCurrencies;

        var blacklist = await GetSettingList(db, "Blacklist");
        if (blacklist != null && blacklist.Any()) Blacklist = blacklist;

        var majorCoin = await GetSettingList(db, "MajorCoin");
        if (majorCoin != null && majorCoin.Any()) MajorCoin = majorCoin;

        var currency = await GetSettingList(db, "Currency");
        if (currency != null && currency.Any()) Currency = currency;

        var badPairs = await GetSettingList(db, "BadPairs");
        if (badPairs != null && badPairs.Any()) BadPairs = badPairs;

        var defaultPairs = await GetSettingList(db, "DefaultPairs");
        if (defaultPairs != null && defaultPairs.Any()) DefaultPairs = defaultPairs.ToArray();

        // Reload boolean settings
        var stakingNotif = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "StakingNotifications");
        StakingNotifications = stakingNotif != null && string.Equals(stakingNotif.Value, "true", StringComparison.OrdinalIgnoreCase);

        var hideAlmostZero = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "HideAlmostZeroBalances");
        HideAlmostZeroBalances = hideAlmostZero != null && string.Equals(hideAlmostZero.Value, "true", StringComparison.OrdinalIgnoreCase);

        var orderProxNotif = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "OrderProximityNotifications");
        OrderProximityNotifications = orderProxNotif == null || string.Equals(orderProxNotif.Value, "true", StringComparison.OrdinalIgnoreCase);

        var orderProxThreshold = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "OrderProximityThreshold");
        if (orderProxThreshold != null && decimal.TryParse(orderProxThreshold.Value, System.Globalization.CultureInfo.InvariantCulture, out var threshold))
            OrderProximityThreshold = Math.Clamp(threshold, 0.1m, 20.0m);
        else
            OrderProximityThreshold = 2.0m;

        // Reload order dialog button configs
        var priceOffsets = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "OrderPriceOffsets");
        if (priceOffsets != null)
        {
            var parsed = priceOffsets.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => decimal.TryParse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null)
                .Where(v => v.HasValue && v.Value > 0).Select(v => v!.Value).ToList();
            if (parsed.Any()) OrderPriceOffsets = parsed;
        }

        var qtyPcts = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "OrderQtyPercentages");
        if (qtyPcts != null)
        {
            var parsed = qtyPcts.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => decimal.TryParse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null)
                .Where(v => v.HasValue && v.Value > 0 && v.Value <= 100).Select(v => v!.Value).ToList();
            if (parsed.Any()) OrderQtyPercentages = parsed;
        }

        // Reload auto-sell settings
        var autoSellEnabled = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AutoSellOnBuyFill");
        AutoSellOnBuyFill = autoSellEnabled != null && string.Equals(autoSellEnabled.Value, "true", StringComparison.OrdinalIgnoreCase);

        var autoSellPct = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AutoSellPercentage");
        if (autoSellPct != null && decimal.TryParse(autoSellPct.Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            AutoSellPercentage = Math.Clamp(pct, 1m, 500m);
        else
            AutoSellPercentage = 10m;

        var autoAddStaking = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AutoAddStakingToOrder");
        AutoAddStakingToOrder = autoAddStaking != null && string.Equals(autoAddStaking.Value, "true", StringComparison.OrdinalIgnoreCase);

        var bookDepth = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "OrderBookDepth");
        if (bookDepth != null && int.TryParse(bookDepth.Value, out var depth) && ValidBookDepths.Contains(depth))
            OrderBookDepth = depth;
        else
            OrderBookDepth = 25;

        // Reload asset normalizations from DB — merge into defaults so XXBT→BTC etc. are always present
        var normalizations = await db.AssetNormalizations.ToDictionaryAsync(a => a.KrakenName, a => a.NormalizedName);
        var merged = new Dictionary<string, string>(DefaultAssetAliases, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in normalizations) merged[kvp.Key] = kvp.Value;
        AssetAliases = merged;

        var predSymbols = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionSymbols");
        PredictionSymbols = predSymbols?.Value ?? "XBT/USD,ETH/USD,SOL/USD";

        var predInterval = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionInterval");
        PredictionInterval = predInterval?.Value ?? "OneHour";

        var predMode = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionMode");
        PredictionMode = predMode?.Value ?? "specific";

        var predCurrency = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionCurrency");
        PredictionCurrency = predCurrency?.Value ?? "USD";

        var predAutoRefresh = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PredictionAutoRefreshIntervalMinutes");
        PredictionAutoRefreshIntervalMinutes = predAutoRefresh != null && int.TryParse(predAutoRefresh.Value, out var mins) && mins >= 5
            ? mins : 15;

        var slEnabled = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "StopLossEnabled");
        StopLossEnabled = slEnabled != null && string.Equals(slEnabled.Value, "true", StringComparison.OrdinalIgnoreCase);

        var slPct = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "StopLossPct");
        if (slPct != null && decimal.TryParse(slPct.Value, System.Globalization.CultureInfo.InvariantCulture, out var slv))
            StopLossPct = Math.Clamp(slv, 1m, 99.9m);

        var tpEnabled = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "TakeProfitEnabled");
        TakeProfitEnabled = tpEnabled != null && string.Equals(tpEnabled.Value, "true", StringComparison.OrdinalIgnoreCase);

        var tpPct = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "TakeProfitPct");
        if (tpPct != null && decimal.TryParse(tpPct.Value, System.Globalization.CultureInfo.InvariantCulture, out var tpv))
            TakeProfitPct = Math.Clamp(tpv, 1m, 500m);

        var ddEnabled = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "DrawdownAlertEnabled");
        DrawdownAlertEnabled = ddEnabled != null && string.Equals(ddEnabled.Value, "true", StringComparison.OrdinalIgnoreCase);

        var ddThreshold = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "DrawdownAlertThreshold");
        if (ddThreshold != null && decimal.TryParse(ddThreshold.Value, System.Globalization.CultureInfo.InvariantCulture, out var ddv))
            DrawdownAlertThreshold = Math.Clamp(ddv, 1m, 90m);

        var dryRunJobs = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "DryRunJobs");
        DryRunJobs = dryRunJobs != null && string.Equals(dryRunJobs.Value, "true", StringComparison.OrdinalIgnoreCase);

        var tsEnabled = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "TrailingStopEnabled");
        TrailingStopEnabled = tsEnabled != null && string.Equals(tsEnabled.Value, "true", StringComparison.OrdinalIgnoreCase);
        var tsPct = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "TrailingStopPct");
        if (tsPct != null && decimal.TryParse(tsPct.Value, System.Globalization.CultureInfo.InvariantCulture, out var tsv))
            TrailingStopPct = Math.Clamp(tsv, 0.5m, 50m);

        var acEnabled = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AutoCancelEnabled");
        AutoCancelEnabled = acEnabled != null && string.Equals(acEnabled.Value, "true", StringComparison.OrdinalIgnoreCase);
        var acDays = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AutoCancelDays");
        if (acDays != null && int.TryParse(acDays.Value, out var acd))
            AutoCancelDays = Math.Clamp(acd, 1, 365);
        var acBuys = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AutoCancelBuys");
        AutoCancelBuys = acBuys == null || string.Equals(acBuys.Value, "true", StringComparison.OrdinalIgnoreCase);
        var acSells = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "AutoCancelSells");
        AutoCancelSells = acSells != null && string.Equals(acSells.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ensures required settings exist in DB for databases created before new settings were added</summary>
    public async Task EnsureRequiredSettings(Data.KrakenDbContext db)
    {
        var requiredSettings = new Dictionary<string, (string Value, string Description)>
        {
            ["StakingNotifications"] = ("false", "Send Pushover notifications for staking reward payments"),
            ["HideAlmostZeroBalances"] = ("false", "Hide balance rows with less than 0.0001 units or less than $0.01 value"),
            ["OrderProximityNotifications"] = ("true", "Send Pushover notifications when an order is near the current price"),
            ["OrderProximityThreshold"] = ("2.0", "Percentage threshold for order proximity notifications (0.1 to 20.0)"),
            ["Theme"] = ("dark", "UI theme (dark or light)"),
            ["OrderPriceOffsets"] = ("2,5,10,15", "Percentage offset buttons for the order dialog price field (comma-separated)"),
            ["OrderQtyPercentages"] = ("5,10,20,25,50,75,100", "Percentage buttons for the order dialog quantity field (comma-separated)"),
            ["AutoSellOnBuyFill"] = ("false", "Automatically create a sell order when a buy order fills"),
            ["AutoSellPercentage"] = ("10", "Percentage above buy price for the automatic sell order (1 to 500)"),
            ["AutoAddStakingToOrder"] = ("false", "Automatically add staking reward quantity to the newest open sell order for that asset"),
            ["PriceDownloadTime"] = ("04:00", "Daily price download time (HH:MM, 24-hour)"),
            ["PredictionSymbols"] = ("XBT/USD,ETH/USD,SOL/USD", "Comma-separated symbols for ML prediction job"),
            ["PredictionInterval"] = ("OneHour", "Kline interval for ML training data (OneMinute/FifteenMinutes/OneHour/FourHour/OneDay)"),
            ["PredictionMode"] = ("specific", "Prediction symbol mode: 'specific' (list) or 'all' (all active pairs for a currency)"),
            ["PredictionCurrency"] = ("USD", "Quote currency to use when PredictionMode is 'all'"),
            ["PredictionJobTime"] = ("05:00", "Daily ML prediction job time (HH:MM, 24-hour)"),
            ["PredictionAutoRefreshIntervalMinutes"] = ("15", "How often (minutes) the stale-prediction auto-refresh job runs (minimum 5)"),
            ["HangfireFailedJobRetentionDays"] = ("14", "How long to keep Hangfire records of failed jobs before purging (1 to 365 days)"),
            ["MinuteCandleCollectionEnabled"] = ("true", "Collect and retain one-minute candles for traded pairs (needed for minute-resolution analysis)"),
            ["MinuteCandleLookbackMonths"] = ("5", "Collect minute candles for pairs traded within this many months (1 to 120)"),
            ["MinuteCandleRetentionDays"] = ("400", "How long to keep one-minute candles before pruning (7 to 3650 days)"),
            ["MinuteCandleIntervalMinutes"] = ("10", "How often (minutes) the minute-candle collector runs (5 to 240; must stay well under 12 hours or history is lost)"),
            ["DryRunJobs"] = ("false", "Dry-run mode: scheduled jobs simulate orders and send Pushover notifications instead of placing real orders"),
            ["TrailingStopEnabled"] = ("false", "Enable trailing stop-loss: sell when price falls TrailingStopPct% from its peak since position open"),
            ["TrailingStopPct"] = ("5", "Trailing stop-loss percentage drop from high (0.5–50)"),
            ["AutoCancelEnabled"] = ("false", "Automatically cancel open orders older than AutoCancelDays"),
            ["AutoCancelDays"] = ("30", "Age in days before an open order is auto-cancelled"),
            ["AutoCancelBuys"] = ("true", "Auto-cancel applies to buy orders"),
            ["AutoCancelSells"] = ("false", "Auto-cancel applies to sell orders"),
        };

        var changed = false;
        foreach (var (key, (value, description)) in requiredSettings)
        {
            if (!await db.AppSettings.AnyAsync(s => s.Key == key))
            {
                db.AppSettings.Add(new AppSettings { Key = key, Value = value, Description = description });
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync();
    }

    private static async Task<List<string>?> GetSettingList(Data.KrakenDbContext db, string key)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null) return null;
        return setting.Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }
}
