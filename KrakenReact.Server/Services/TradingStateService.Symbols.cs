using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public partial class TradingStateService
{
    public DerivedKline? LatestPrice(string asset)
    {
        var normalized = NormalizeAsset(asset);
        if (normalized == "USD") return new DerivedKline { Asset = "USD", Close = 1.0m, OpenTime = DateTime.UtcNow };

        // BTC is special: Kraken uses "XBT" in WebSocket names. Try XBT/USD directly first,
        // independent of AssetAliases, so a corrupted DB normalization never silences Bitcoin prices.
        if (asset.Equals("BTC", StringComparison.OrdinalIgnoreCase) ||
            asset.Equals("XBT", StringComparison.OrdinalIgnoreCase) ||
            asset.Equals("XXBT", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("BTC", StringComparison.OrdinalIgnoreCase))
        {
            if (Prices.TryGetValue("XBT/USD", out var xbtItem) && xbtItem.BestKline?.Close > 0)
                return xbtItem.BestKline;
        }

        // Try all matching symbols (there may be multiple, e.g. XBT/USD and BTC/USD for Bitcoin)
        var matchingSymbols = Symbols.Values.Where(a =>
            normalized == a.AlternateName || normalized == NormalizeAsset(a.BaseAsset) ||
            (a.QuoteAsset == "ZUSD" && (a.BaseAsset == normalized || a.AlternateName == normalized || a.WebsocketName == normalized + "/USD")));

        foreach (var symbol in matchingSymbols)
        {
            if (Prices.TryGetValue(symbol.WebsocketName, out var priceItem))
            {
                var best = priceItem.BestKline;
                if (best != null && best.Close > 0) return best;
            }
        }

        // Fallback: scan Prices by comparing raw base names to both the original and normalized asset,
        // so the result doesn't depend on AssetAliases being intact for every asset.
        var p = Prices.Values.FirstOrDefault(p =>
            p.SymbolNoSlashNoStaking == normalized + "USD" ||
            (NormalizeAsset(p.Base) == normalized && (p.CCY == "USD" || NormalizeAsset(p.CCY) == "USD")) ||
            (p.Base.Equals(asset, StringComparison.OrdinalIgnoreCase) && p.CCY == "USD") ||
            (p.Base.Equals(normalized, StringComparison.OrdinalIgnoreCase) && p.CCY == "USD"));
        if (p != null)
        {
            // Use BestKline first — it prefers the live ticker over the stale kline snapshot
            var best = p.BestKline;
            if (best != null && best.Close > 0) return best;
            var lastK = p.GetKlineSnapshot().LastOrDefault(x => x.Close > 0);
            if (lastK != null) return lastK;
        }

        // Also try direct key lookups with the normalized name
        if (Prices.TryGetValue(normalized + "/USD", out var directPrice))
        {
            var best = directPrice.BestKline;
            if (best != null && best.Close > 0) return best;
        }

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
    /// Extracts and normalizes the base asset from an order symbol (e.g. "XBTUSD" → "BTC", "SOLUSD" → "SOL").
    /// Uses the Symbols table to find the correct base asset, falling back to heuristic extraction.
    /// </summary>
    public string NormalizeOrderSymbolBase(string orderSymbol)
    {
        if (string.IsNullOrEmpty(orderSymbol)) return "";

        // If it contains a slash, just split
        if (orderSymbol.Contains('/'))
            return NormalizeAsset(orderSymbol.Split('/')[0]);

        // Try to match against known symbols (websocket names have slashes, order symbols don't).
        // Also handle Kraken's legacy REST pair names that add an "X" prefix to the wsname/altname
        // (e.g. "XPAXGUSD" where wsname gives "PAXGUSD" and altname gives "PAXGUSD").
        var match = Symbols.Values.FirstOrDefault(s =>
        {
            var ws = s.WebsocketName.Replace("/", "");
            var alt = s.AlternateName?.Replace(".", "");
            return ws == orderSymbol ||
                   (alt != null && alt == orderSymbol) ||
                   ("X" + ws) == orderSymbol ||
                   (alt != null && ("X" + alt) == orderSymbol);
        });
        if (match != null)
        {
            // Use the WebSocket name's base (e.g. "PAXG" from "PAXG/USD") rather than
            // raw BaseAsset (e.g. "XPAXG") — the WS base is the canonical key used in Prices.
            if (match.WebsocketName.Contains('/'))
                return NormalizeAsset(match.WebsocketName.Split('/')[0]);
            return NormalizeAsset(match.BaseAsset);
        }

        // Heuristic: strip known quote suffixes, then try a Symbols scan to resolve any
        // residual X-prefix that isn't covered by the alias table (e.g. XPAXG → PAXG).
        foreach (var suffix in new[] { "ZUSD", "USDT", "USDC", "USD", "ZEUR", "EUR", "ZGBP", "GBP" })
        {
            if (orderSymbol.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && orderSymbol.Length > suffix.Length)
            {
                var rawBase = orderSymbol[..^suffix.Length];
                var normalized = NormalizeAsset(rawBase);
                if (normalized != rawBase) return normalized; // alias resolved it — done

                // Alias didn't help (e.g. rawBase = "XPAXG").
                // Try Symbols: Kraken sometimes prefixes the base with "X" in REST pair names.
                var normQuote = NormalizeAsset(suffix);
                var sym = Symbols.Values.FirstOrDefault(s =>
                    NormalizeAsset(s.QuoteAsset) == normQuote &&
                    (s.BaseAsset.Equals(rawBase, StringComparison.OrdinalIgnoreCase) ||
                     rawBase.Equals("X" + s.BaseAsset, StringComparison.OrdinalIgnoreCase) ||
                     rawBase.Equals("X" + NormalizeAsset(s.BaseAsset), StringComparison.OrdinalIgnoreCase)));
                return sym != null ? NormalizeAsset(sym.BaseAsset) : normalized;
            }
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

    /// <summary>
    /// Resolves a symbol (which may use normalized names like "BTC/USD") to the
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
}
