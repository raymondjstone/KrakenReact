using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class PriceDataItem
{
    public string Symbol { get; set; } = "";
    public string SymbolNoSlash => TradingStateService.NormalizeAsset(Base) + TradingStateService.NormalizeAsset(CCY);
    public string SymbolNoSlashNoStaking => TradingStateService.NormalizeAsset(Base) + TradingStateService.NormalizeAsset(CCY);
    public string Base => (Symbol ?? "/").Split("/").FirstOrDefault() ?? "";
    public string CCY => (Symbol ?? "/").Split("/").LastOrDefault() ?? "";
    public bool SupportedPair { get; set; }
    public bool KrakenNewPricesLoadedEver { get; set; }
    public string KrakenNewPricesLoaded { get; set; } = "no";
    public DateTime KrakenNewPricesLoadedTime { get; set; } = DateTime.MinValue;
    public TickerDataItem? TickerData { get; set; }

    private readonly List<DerivedKline> _klineSnapshot = new(10000);
    private readonly object _klineLock = new();
    private const int MaxKlines = 10000;

    public string CoinType
    {
        get
        {
            var b = TradingStateService.NormalizeAsset(Base);
            if (TradingStateService.Blacklist.Contains(b)) return "Blacklist";
            if (TradingStateService.MajorCoin.Contains(b)) return "Main Coin";
            if (TradingStateService.Currency.Contains(b)) return "Currency";
            return "Minor Coin";
        }
    }

    public bool PriceOutdated
    {
        get
        {
            if (KrakenNewPricesLoaded == "loaded" && KrakenNewPricesLoadedTime > DateTime.UtcNow.AddMinutes(-20))
                return false;
            return !(SupportedPair && KrakenNewPricesLoadedEver);
        }
    }

    public void AddKline(DerivedKline kline)
    {
        if (kline == null) return;
        lock (_klineLock)
        {
            _klineSnapshot.Add(kline);
            if (_klineSnapshot.Count > MaxKlines)
                _klineSnapshot.RemoveRange(0, _klineSnapshot.Count - MaxKlines);
        }
        KrakenNewPricesLoadedTime = DateTime.UtcNow;
    }

    public void AddKlineHistory(List<DerivedKline> klines)
    {
        if (!klines.Any()) return;
        lock (_klineLock)
        {
            _klineSnapshot.InsertRange(0, klines);
            if (_klineSnapshot.Count > MaxKlines)
                _klineSnapshot.RemoveRange(0, _klineSnapshot.Count - MaxKlines);
        }
    }

    public List<DerivedKline> GetKlineSnapshot()
    {
        lock (_klineLock) { return _klineSnapshot.ToList(); }
    }

    public DerivedKline? LatestKline
    {
        get { lock (_klineLock) { return _klineSnapshot.LastOrDefault(); } }
    }

    public DerivedKline? MinKline
    {
        get { lock (_klineLock) { return _klineSnapshot.FirstOrDefault(l => l.OpenTime.Year > 1967); } }
    }

    public string Age
    {
        get
        {
            var min = MinKline;
            if (min == null) return "Unknown";
            var t = DateTime.Now - min.OpenTime;
            if (t.TotalDays > 36500) return "Unknown";
            if (t.TotalDays > 365) return "Old";
            if (t.TotalDays > 180) return "SixMonths";
            if (t.TotalDays > 90) return "ThreeMonths";
            if (t.TotalDays > 28) return "OneMonth";
            if (t.TotalDays > 13) return "TwoWeeks";
            if (t.TotalDays > 6) return "OneWeek";
            if (t.TotalDays > 2) return "FewDays";
            return "New";
        }
    }

    public decimal? ClosePriceDiff(int days)
    {
        var k = GetKlineSnapshot();
        if (k.Count < 2) return null;
        var last = k.LastOrDefault(l => l != null);
        if (last == null || last.OpenTime <= DateTime.MinValue) return null;
        var dayoldlist = k.Where(a => a.OpenTime > last.OpenTime.AddDays(-1 * days)).ToList();
        return last.Close - (dayoldlist.Any() ? dayoldlist.First().Close : last.Close);
    }

    public decimal CloseMovementDiff(int days)
    {
        var x = ClosePriceDiff(days);
        if (x == null) return 0m;
        var close = LatestKline?.Close ?? 0m;
        if (close == 0m) return 0m;
        return Math.Round((x.Value / (close / 100m)), 6);
    }

    public decimal? ClosePriceAverage(int days)
    {
        var k = GetKlineSnapshot();
        if (k.Count < 2) return null;
        var last = k.LastOrDefault(a => a.OpenTime > DateTime.MinValue);
        if (last == null) return null;
        var dayoldlist = k.Where(a => a.OpenTime > last.OpenTime.AddDays(-1 * days)).ToList();
        if (dayoldlist.Count <= 2) return null;
        return Math.Round(dayoldlist.Sum(a => a.Close) / dayoldlist.Count, 6);
    }

    public decimal? WeightedPrice
    {
        get
        {
            decimal total = 0m; int weight = 0;
            var avgDay = ClosePriceAverage(1);
            var avgWeek = ClosePriceAverage(7);
            var avgMonth = ClosePriceAverage(31);
            var avgYear = ClosePriceAverage(365);
            if (avgDay.HasValue) { total += avgDay.Value * 6; weight += 6; }
            if (avgWeek.HasValue) { total += avgWeek.Value * 4; weight += 4; }
            if (avgMonth.HasValue) { total += avgMonth.Value * 2; weight += 2; }
            if (avgYear.HasValue) { total += avgYear.Value * 1; weight += 1; }
            if (weight == 0) return null;
            return Math.Round(total / weight, 6);
        }
    }

    public decimal? WeightedPricePercentage
    {
        get
        {
            var close = LatestKline?.Close;
            var wp = WeightedPrice;
            if (!KrakenNewPricesLoadedEver || close == null || wp == null || close <= 0 || wp < 0) return null;
            if (Age != "Old") return 200.0m;
            return Math.Round((wp.Value / close.Value) * 100, 2);
        }
    }
}

public class TickerDataItem
{
    public decimal BestAskPrice { get; set; }
    public decimal BestBidPrice { get; set; }
    public decimal LastTradePrice { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal Volume { get; set; }
    public decimal VolumeWeightedAvgPrice { get; set; }
    public int TradeCount { get; set; }
}

public class TradingStateService
{
    // Default values - used as fallback if DB has no configuration
    public static readonly List<string> DefaultBaseCurrencies = new() { "ZUSD" };
    public static readonly List<string> DefaultBlacklist = new() { "TRUMP", "MELANIA", "MATIC", "K", "KILT", "MIRA", "MKR", "EOS", "XMR", "BCH" };
    public static readonly List<string> DefaultMajorCoin = new() { "XBT", "ETH", "SOL", "XRP" };
    public static readonly List<string> DefaultCurrency = new() { "GBP", "EUR", "USD", "USDT", "USDC", "USDQ" };
    public static readonly List<string> DefaultBadPairs = new() { "MATIC/USDT", "MATIC/GBP", "MATIC/USD", "XBT/USD", "TRUMP/USDT", "TRUMP/USD", "XDG/USD" };
    public static readonly string[] DefaultDefaultPairs = { "SOL/USD", "XBT/USD", "ETH/USD" };

    /// <summary>Pairs that must always be loaded for internal use (e.g. currency conversion), regardless of user config.</summary>
    public static readonly string[] RequiredPairs = { "GBP/USD" };

    // Runtime configuration - can be updated from database
    public static List<string> BaseCurrencies = new(DefaultBaseCurrencies);
    public static List<string> Blacklist = new(DefaultBlacklist);
    public static List<string> MajorCoin = new(DefaultMajorCoin);
    public static List<string> Currency = new(DefaultCurrency);
    public static List<string> BadPairs = new(DefaultBadPairs);
    public static string[] DefaultPairs = DefaultDefaultPairs;

    // Kraken uses X-prefix for legacy crypto and Z-prefix for fiat internally.
    // Balances can return either form, causing duplicate entries.
    private static Dictionary<string, string> AssetAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "XXBT", "XBT" }, { "BTC", "XBT" },
        { "XXRP", "XRP" }, { "XETH", "ETH" }, { "XXLM", "XLM" },
        { "XLTC", "LTC" }, { "XXMR", "XMR" }, { "XXDG", "XDG" }, { "XZEC", "ZEC" },
        { "XREP", "REP" }, { "XMLN", "MLN" }, { "XETC", "ETC" }, { "XDAO", "DAO" },
        { "XICN", "ICN" },
        { "ZUSD", "USD" }, { "ZEUR", "EUR" }, { "ZGBP", "GBP" },
        { "ZCAD", "CAD" }, { "ZJPY", "JPY" }, { "ZAUD", "AUD" }, { "ZCHF", "CHF" },
    };

    /// <summary>
    /// Normalizes a Kraken asset name by stripping staking suffixes (.F, .B)
    /// and resolving X/Z-prefixed aliases to their canonical form.
    /// </summary>
    public static string NormalizeAsset(string asset)
    {
        if (string.IsNullOrEmpty(asset)) return asset ?? "";
        var clean = asset.Replace(".F", "").Replace(".B", "").Replace(".S", "").Replace(".P", "");
        return AssetAliases.TryGetValue(clean, out var canonical) ? canonical : clean;
    }

    private readonly DelistedPriceService _delisted;

    public TradingStateService(DelistedPriceService delisted)
    {
        _delisted = delisted;
    }

    public bool InitialDataLoad { get; set; } = true;
    public string LastStatusMessage { get; set; } = "";
    public bool StakingNotifications { get; set; }
    public bool HideAlmostZeroBalances { get; set; }
    public bool OrderProximityNotifications { get; set; } = true;
    public decimal OrderProximityThreshold { get; set; } = 2.0m;
    public HashSet<string> SeenLedgerIds { get; } = new();

    public ConcurrentDictionary<string, PriceDataItem> Prices { get; } = new();
    public ConcurrentDictionary<string, OrderDto> Orders { get; } = new();
    public ConcurrentDictionary<string, BalanceDto> Balances { get; } = new();
    public ConcurrentDictionary<string, AutoTradeDto> AutoOrders { get; } = new();
    public ConcurrentDictionary<string, KrakenSymbol> Symbols { get; } = new();
    private readonly HashSet<string> _notifiedOrders = new();
    private readonly object _notifiedLock = new();
    private const int MaxNotifiedOrders = 5000;

    public bool HasNotified(string orderId)
    {
        lock (_notifiedLock) { return _notifiedOrders.Contains(orderId); }
    }

    public void AddNotified(string orderId)
    {
        lock (_notifiedLock)
        {
            if (_notifiedOrders.Count >= MaxNotifiedOrders)
                _notifiedOrders.Clear();
            _notifiedOrders.Add(orderId);
        }
    }

    public DerivedKline? LatestPrice(string asset)
    {
        var normalized = NormalizeAsset(asset);
        if (normalized == "USD") return new DerivedKline { Asset = "USD", Close = 1.0m, OpenTime = DateTime.UtcNow };

        // Try all matching symbols (there may be multiple, e.g. XBT/USD and BTC/USD for Bitcoin)
        var matchingSymbols = Symbols.Values.Where(a =>
            normalized == a.AlternateName || normalized == NormalizeAsset(a.BaseAsset) ||
            (a.QuoteAsset == "ZUSD" && (a.BaseAsset == normalized || a.AlternateName == normalized || a.WebsocketName == normalized + "/USD")));

        foreach (var symbol in matchingSymbols)
        {
            if (Prices.TryGetValue(symbol.WebsocketName, out var priceItem) && priceItem.LatestKline != null)
                return priceItem.LatestKline;
        }

        // Fallback: search Prices by normalizing the base part of each price entry's symbol
        var p = Prices.Values.FirstOrDefault(p =>
            p.SymbolNoSlashNoStaking == normalized + "USD" ||
            (NormalizeAsset(p.Base) == normalized && (p.CCY == "USD" || NormalizeAsset(p.CCY) == "USD")));
        if (p != null)
        {
            var k = p.GetKlineSnapshot();
            if (k.Any()) return k.Last();
        }

        // Also try direct key lookups with the normalized name
        if (Prices.TryGetValue(normalized + "/USD", out var directPrice) && directPrice.LatestKline != null)
            return directPrice.LatestKline;

        // Fallback: try delisted CSV for this asset
        var pairNoSlash = normalized + "USD";
        var symbolWithSlash = normalized + "/USD";
        var csvKlines = _delisted.GetKlines(pairNoSlash, symbolWithSlash);
        if (csvKlines != null && csvKlines.Any())
        {
            var pi = GetOrAddPrice(symbolWithSlash);
            pi.AddKlineHistory(csvKlines);
            pi.KrakenNewPricesLoaded = "loaded";
            pi.KrakenNewPricesLoadedEver = true;
            return csvKlines.Last();
        }

        return null;
    }

    /// <summary>Returns the USD-to-GBP conversion rate (e.g. 0.78), or 0 if not available.</summary>
    public decimal GetUsdGbpRate()
    {
        // Try GBP/USD pair (price = USD per GBP, e.g. 1.28) — invert to get GBP per USD
        if (Prices.TryGetValue("GBP/USD", out var gbpUsd))
        {
            var price = gbpUsd.LatestKline?.Close ?? gbpUsd.TickerData?.LastTradePrice ?? 0;
            if (price > 0) return Math.Round(1m / price, 6);
        }
        // Fallback: try USD/GBP pair directly (price = GBP per USD, e.g. 0.78)
        if (Prices.TryGetValue("USD/GBP", out var usdGbp))
        {
            var price = usdGbp.LatestKline?.Close ?? usdGbp.TickerData?.LastTradePrice ?? 0;
            if (price > 0) return price;
        }
        return 0m;
    }

    public PriceDataItem GetOrAddPrice(string symbol)
    {
        return Prices.GetOrAdd(symbol, s => new PriceDataItem { Symbol = s });
    }

    public List<PriceDataItem> GetPriceSnapshot() => Prices.Values.ToList();

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
            new() { Key = "OrderProximityThreshold", Value = "2.0", Description = "Percentage threshold for order proximity notifications (0.1 to 20.0)" }
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

    /// <summary>Ensures all default asset normalizations exist in the database, adding any missing ones.</summary>
    public async Task SyncAssetNormalizations(Data.KrakenDbContext db)
    {
        var existing = await db.AssetNormalizations.ToDictionaryAsync(a => a.KrakenName, a => a, StringComparer.OrdinalIgnoreCase);
        var changed = false;

        // Build the authoritative set from code defaults
        var defaults = new Dictionary<string, string>(AssetAliases, StringComparer.OrdinalIgnoreCase);

        // Add missing entries
        foreach (var kvp in defaults)
        {
            if (!existing.ContainsKey(kvp.Key))
            {
                db.AssetNormalizations.Add(new AssetNormalization { KrakenName = kvp.Key, NormalizedName = kvp.Value });
                changed = true;
            }
            else if (existing[kvp.Key].NormalizedName != kvp.Value)
            {
                // Update if the normalized name changed in code defaults
                existing[kvp.Key].NormalizedName = kvp.Value;
                changed = true;
            }
        }

        // Remove DB entries that are no longer in code defaults
        foreach (var kvp in existing)
        {
            if (!defaults.ContainsKey(kvp.Key))
            {
                db.AssetNormalizations.Remove(kvp.Value);
                changed = true;
            }
        }

        if (changed) await db.SaveChangesAsync();
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

        // Reload asset normalizations from DB (already synced by SyncAssetNormalizations)
        var normalizations = await db.AssetNormalizations.ToDictionaryAsync(a => a.KrakenName, a => a.NormalizedName);
        if (normalizations.Any())
        {
            AssetAliases = new Dictionary<string, string>(normalizations, StringComparer.OrdinalIgnoreCase);
        }
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
