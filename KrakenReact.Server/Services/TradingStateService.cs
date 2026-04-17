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
            // Remove existing klines that overlap with new data (same OpenTime+Interval)
            var newDates = new HashSet<(DateTime, string)>(
                klines.Select(k => (k.OpenTime, k.Interval)));
            _klineSnapshot.RemoveAll(k => newDates.Contains((k.OpenTime, k.Interval)));

            _klineSnapshot.AddRange(klines);
            _klineSnapshot.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));

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
    public static readonly List<string> DefaultMajorCoin = new() { "BTC", "ETH", "SOL", "XRP" };
    public static readonly List<string> DefaultCurrency = new() { "GBP", "EUR", "USD", "USDT", "USDC", "USDQ" };
    public static readonly List<string> DefaultBadPairs = new() { "MATIC/USDT", "MATIC/GBP", "MATIC/USD",  "TRUMP/USDT", "TRUMP/USD", "XDG/USD" };
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
        { "XXBT", "BTC" }, { "XBT", "BTC" },
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
        var clean = asset.Replace(".F", "").Replace(".B", "").Replace(".S", "").Replace(".P", "").Replace(".M", "");
        // Chase alias chains (e.g. XXBT → XBT → BTC)
        for (int i = 0; i < 5; i++)
        {
            if (AssetAliases.TryGetValue(clean, out var canonical) && canonical != clean)
                clean = canonical;
            else
                break;
        }
        return clean;
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

    // Order book state
    public static readonly int[] ValidBookDepths = { 10, 25, 100, 500, 1000 };
    public int OrderBookDepth { get; set; } = 25;
    private string? _bookPair;
    private readonly object _bookLock = new();
    public event Action<string?, string?>? BookPairChanged; // (oldPair, newPair)

    public string? BookPair
    {
        get { lock (_bookLock) return _bookPair; }
        set
        {
            string? old;
            lock (_bookLock) { old = _bookPair; _bookPair = value; }
            if (old != value) BookPairChanged?.Invoke(old, value);
        }
    }

    public static readonly List<decimal> DefaultOrderPriceOffsets = new() { 2, 5, 10, 15 };
    public static readonly List<decimal> DefaultOrderQtyPercentages = new() { 5, 10, 20, 25, 50, 75, 100 };
    public List<decimal> OrderPriceOffsets { get; set; } = new(DefaultOrderPriceOffsets);
    public List<decimal> OrderQtyPercentages { get; set; } = new(DefaultOrderQtyPercentages);

    public bool AutoSellOnBuyFill { get; set; }
    public decimal AutoSellPercentage { get; set; } = 10m;
    public bool AutoAddStakingToOrder { get; set; }

    /// <summary>Cache of working Kraken API pair names. Key = internal symbol (e.g. "XBT/USD"), Value = API-accepted name (e.g. "BTCUSD").</summary>
    public ConcurrentDictionary<string, string> ApiPairNameCache { get; } = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Resolves a symbol (which may use normalized names like "BTC/USD") to the
    /// <summary>
    /// Extracts and normalizes the base asset from an order symbol (e.g. "XBTUSD" → "BTC", "SOLUSD" → "SOL").
    /// Uses the Symbols table to find the correct base asset, falling back to heuristic extraction.
    /// </summary>
    public string NormalizeOrderSymbolBase(string orderSymbol)
    {
        if (string.IsNullOrEmpty(orderSymbol)) return "";

        // If it contains a slash, just split
        if (orderSymbol.Contains('/'))
            return NormalizeAsset(orderSymbol.Split('/')[0]);

        // Try to match against known symbols (websocket names have slashes, order symbols don't)
        var match = Symbols.Values.FirstOrDefault(s =>
            s.WebsocketName.Replace("/", "") == orderSymbol ||
            (s.AlternateName != null && s.AlternateName.Replace(".", "") == orderSymbol));
        if (match != null)
            return NormalizeAsset(match.BaseAsset);

        // Heuristic: strip known quote suffixes
        foreach (var suffix in new[] { "ZUSD", "USDT", "USDC", "USD", "ZEUR", "EUR", "ZGBP", "GBP" })
        {
            if (orderSymbol.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && orderSymbol.Length > suffix.Length)
                return NormalizeAsset(orderSymbol[..^suffix.Length]);
        }

        return NormalizeAsset(orderSymbol);
    }

    /// <summary>
    /// Extracts and normalizes the quote currency from an order symbol (e.g. "XBTUSD" → "USD", "ETHEUR" → "EUR").
    /// Uses the Symbols table to find the correct quote asset, falling back to heuristic extraction.
    /// </summary>
    public string NormalizeOrderSymbolQuote(string orderSymbol)
    {
        if (string.IsNullOrEmpty(orderSymbol)) return "";

        // If it contains a slash, just split
        if (orderSymbol.Contains('/'))
            return NormalizeAsset(orderSymbol.Split('/')[1]);

        // Try to match against known symbols
        var match = Symbols.Values.FirstOrDefault(s =>
            s.WebsocketName.Replace("/", "") == orderSymbol ||
            (s.AlternateName != null && s.AlternateName.Replace(".", "") == orderSymbol));
        if (match != null)
            return NormalizeAsset(match.QuoteAsset);

        // Heuristic: match known quote suffixes
        foreach (var suffix in new[] { "ZUSD", "USDT", "USDC", "USD", "ZEUR", "EUR", "ZGBP", "GBP" })
        {
            if (orderSymbol.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && orderSymbol.Length > suffix.Length)
                return NormalizeAsset(suffix);
        }

        return "";
    }

    /// raw Kraken websocket key (e.g. "XBT/USD") used in the Prices dictionary.
    /// Returns the input unchanged if already a valid key or no match found.
    /// </summary>
    public string ResolveSymbolKey(string symbol)
    {
        // Direct match — already a valid key
        if (Prices.ContainsKey(symbol)) return symbol;

        // Try matching by normalizing both sides
        var parts = symbol.Split('/');
        if (parts.Length == 2)
        {
            var normalizedBase = NormalizeAsset(parts[0]);
            var normalizedCcy = NormalizeAsset(parts[1]);
            var match = Prices.Keys.FirstOrDefault(k =>
            {
                var kParts = k.Split('/');
                return kParts.Length == 2 &&
                       NormalizeAsset(kParts[0]) == normalizedBase &&
                       NormalizeAsset(kParts[1]) == normalizedCcy;
            });
            if (match != null) return match;
        }

        return symbol;
    }

    /// <summary>
    /// Generates candidate pair names for the Kraken REST API.
    /// Kraken accepts different name formats depending on the endpoint/era,
    /// e.g. "XBT/USD", "XBTUSD", "BTC/USD", "BTCUSD", "XXBTZUSD".
    /// </summary>
    public List<string> GetApiPairCandidates(string symbol)
    {
        var candidates = new List<string>();
        var parts = symbol.Split('/');
        if (parts.Length != 2) { candidates.Add(symbol); return candidates; }

        var rawBase = parts[0];
        var rawCcy = parts[1];
        var normBase = NormalizeAsset(rawBase);
        var normCcy = NormalizeAsset(rawCcy);

        // Build reverse map: normalized → all known raw names
        var baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rawBase, normBase };
        var ccyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rawCcy, normCcy };
        foreach (var kvp in AssetAliases)
        {
            if (kvp.Value.Equals(normBase, StringComparison.OrdinalIgnoreCase)) baseNames.Add(kvp.Key);
            if (kvp.Value.Equals(normCcy, StringComparison.OrdinalIgnoreCase)) ccyNames.Add(kvp.Key);
        }

        // Also check the Symbols table for AlternateName
        var symbolEntry = Symbols.Values.FirstOrDefault(s =>
            s.WebsocketName.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (symbolEntry?.AlternateName != null)
            candidates.Add(symbolEntry.AlternateName);

        // Generate all combinations: with slash, without slash
        foreach (var b in baseNames)
            foreach (var c in ccyNames)
            {
                candidates.Add($"{b}/{c}");
                candidates.Add($"{b}{c}");
            }

        // Deduplicate preserving order, put the original first
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        // Original symbol first
        if (seen.Add(symbol)) result.Add(symbol);
        foreach (var c in candidates)
            if (seen.Add(c)) result.Add(c);
        return result;
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
            new() { Key = "OrderProximityThreshold", Value = "2.0", Description = "Percentage threshold for order proximity notifications (0.1 to 20.0)" },
            new() { Key = "Theme", Value = "dark", Description = "UI theme (dark or light)" }
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

        // Add entries from code defaults that don't exist in DB yet
        var defaults = new Dictionary<string, string>(AssetAliases, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in defaults)
        {
            if (!existing.ContainsKey(kvp.Key))
            {
                db.AssetNormalizations.Add(new AssetNormalization { KrakenName = kvp.Key, NormalizedName = kvp.Value });
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
            ["Theme"] = ("dark", "UI theme (dark or light)"),
            ["OrderPriceOffsets"] = ("2,5,10,15", "Percentage offset buttons for the order dialog price field (comma-separated)"),
            ["OrderQtyPercentages"] = ("5,10,20,25,50,75,100", "Percentage buttons for the order dialog quantity field (comma-separated)"),
            ["AutoSellOnBuyFill"] = ("false", "Automatically create a sell order when a buy order fills"),
            ["AutoSellPercentage"] = ("10", "Percentage above buy price for the automatic sell order (1 to 500)"),
            ["AutoAddStakingToOrder"] = ("false", "Automatically add staking reward quantity to the newest open sell order for that asset"),
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

    /// <summary>Recalculates all calculated fields for a single order (LatestPrice, Distance, DistancePercentage, OrderValue)</summary>
    public void RecalculateOrderFields(OrderDto order)
    {
        // Get latest price using normalized base asset (order symbols like "XBTUSD" need base extraction first)
        var baseAsset = NormalizeOrderSymbolBase(order.Symbol);
        var latestPrice = LatestPrice(baseAsset);

        if (latestPrice != null)
        {
            order.LatestPrice = latestPrice.Close;
            order.Distance = order.Price - latestPrice.Close;
            order.DistancePercentage = latestPrice.Close != 0
                ? Math.Round((order.Price - latestPrice.Close) / (latestPrice.Close / 100), 2)
                : 100;
        }
        else
        {
            order.LatestPrice = 0;
            order.Distance = 0;
            order.DistancePercentage = 0;
        }

        // Recalculate order value (price * quantity)
        order.OrderValue = order.Price * order.Quantity;
    }

    /// <summary>Recalculates all calculated fields for all orders in state</summary>
    public void RecalculateAllOrderFields()
    {
        foreach (var order in Orders.Values)
            RecalculateOrderFields(order);
    }

    /// <summary>Recalculates covered/uncovered quantities for all balances based on current open orders.
    /// For crypto assets: uses open sell orders to calculate covered/uncovered quantities.
    /// For currency assets (USD, EUR, etc.): reduces available by the total cost of open buy orders in that currency.</summary>
    public void RecalculateBalanceCoveredAmounts()
    {
        foreach (var balance in Balances.Values)
        {
            balance.OrderCoveredQty = 0;
            balance.OrderUncoveredQty = 0;
            balance.OrderCoveredValue = 0;
            balance.OrderUncoveredValue = 0;
        }

        var openOrders = Orders.Values
            .Where(o => o.Status == "Open" || o.Status == "New" || o.Status == "PendingNew")
            .ToList();

        // Sell orders reduce crypto asset available amounts
        foreach (var order in openOrders.Where(o => o.Side == "Sell"))
        {
            var normalizedBase = NormalizeOrderSymbolBase(order.Symbol);
            if (string.IsNullOrEmpty(normalizedBase)) continue;

            var balance = Balances.Values.FirstOrDefault(b => b.Asset == normalizedBase);
            if (balance == null) continue;

            var orderQty = order.Quantity - order.QuantityFilled;
            balance.OrderCoveredQty += Math.Min(orderQty, balance.Total);
            balance.OrderUncoveredQty = Math.Max(balance.Total - balance.OrderCoveredQty, 0);
            balance.OrderCoveredValue += Math.Min(orderQty, balance.Total) * order.Price;
            balance.OrderUncoveredValue = balance.OrderUncoveredQty * (balance.LatestPrice > 0 ? balance.LatestPrice : order.Price);
        }

        // Buy orders reduce currency balance available amounts
        foreach (var order in openOrders.Where(o => o.Side == "Buy"))
        {
            var quoteCurrency = NormalizeOrderSymbolQuote(order.Symbol);
            if (string.IsNullOrEmpty(quoteCurrency) || !Currency.Contains(quoteCurrency)) continue;

            var balance = Balances.Values.FirstOrDefault(b => b.Asset == quoteCurrency);
            if (balance == null) continue;

            var remainingQty = order.Quantity - order.QuantityFilled;
            var orderCost = remainingQty * order.Price;
            balance.OrderCoveredQty += orderCost;
            balance.OrderCoveredValue += orderCost;
        }

        // Finalize currency balances: set available = total - covered (buy order costs)
        foreach (var balance in Balances.Values.Where(b => Currency.Contains(b.Asset)))
        {
            balance.Available = Math.Max(balance.Total - balance.OrderCoveredQty, 0);
            balance.OrderUncoveredQty = balance.Available;
            balance.OrderUncoveredValue = balance.Available;
        }
    }
}
