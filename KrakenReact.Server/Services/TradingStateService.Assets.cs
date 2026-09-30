using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public partial class TradingStateService
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
    private static readonly Dictionary<string, string> DefaultAssetAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "XXBT", "BTC" }, { "XBT", "BTC" },
        { "XXRP", "XRP" }, { "XETH", "ETH" }, { "XXLM", "XLM" },
        { "XLTC", "LTC" }, { "XXMR", "XMR" }, { "XXDG", "XDG" }, { "XZEC", "ZEC" },
        { "XREP", "REP" }, { "XMLN", "MLN" }, { "XETC", "ETC" }, { "XDAO", "DAO" },
        { "XICN", "ICN" },
        { "ZUSD", "USD" }, { "ZEUR", "EUR" }, { "ZGBP", "GBP" },
        { "ZCAD", "CAD" }, { "ZJPY", "JPY" }, { "ZAUD", "AUD" }, { "ZCHF", "CHF" },
    };

    private static Dictionary<string, string> AssetAliases = new(DefaultAssetAliases, StringComparer.OrdinalIgnoreCase);

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
}
