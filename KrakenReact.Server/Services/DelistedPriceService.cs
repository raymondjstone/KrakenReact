using System.Globalization;
using KrakenReact.Server.Models;

namespace KrakenReact.Server.Services;

/// <summary>
/// Provides historical kline data from kraken_delisted.csv for pairs that are no longer
/// available via the Kraken API. Data is loaded lazily — only pairs that are actually
/// requested are parsed from the CSV file.
/// </summary>
public class DelistedPriceService
{
    private readonly string _csvPath;
    private readonly ILogger<DelistedPriceService> _logger;

    // pair altname (upper, no slash e.g. "MATICUSD") → list of klines
    private readonly Dictionary<string, List<DerivedKline>> _loadedPairs = new(StringComparer.OrdinalIgnoreCase);

    // Set of all pair names found in CSV header scan (upper, no slash)
    private readonly HashSet<string> _availablePairs = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexBuilt;
    private readonly object _lock = new();

    public DelistedPriceService(ILogger<DelistedPriceService> logger)
    {
        _logger = logger;
        _csvPath = Path.Combine(AppContext.BaseDirectory, "Assets", "kraken_delisted.csv");
    }

    /// <summary>True if the CSV file exists and can be used.</summary>
    public bool IsAvailable => File.Exists(_csvPath);

    /// <summary>Returns all pair names available in the delisted CSV.</summary>
    public IEnumerable<string> GetAvailablePairs()
    {
        EnsureIndex();
        return _availablePairs.ToList();
    }

    /// <summary>
    /// Builds an index of all pair names in the CSV without loading candle data.
    /// Called once on first access.
    /// </summary>
    private void EnsureIndex()
    {
        if (_indexBuilt) return;
        lock (_lock)
        {
            if (_indexBuilt) return;
            if (!File.Exists(_csvPath))
            {
                _logger.LogWarning("[Delisted] CSV not found at {Path}", _csvPath);
                _indexBuilt = true;
                return;
            }

            try
            {
                using var reader = new StreamReader(_csvPath);
                reader.ReadLine(); // skip header
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var commaIdx = line.IndexOf(',');
                    if (commaIdx > 0)
                        _availablePairs.Add(line[..commaIdx].Trim().ToUpperInvariant());
                }
                _logger.LogInformation("[Delisted] Indexed {Count} pair names from CSV", _availablePairs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Delisted] Error indexing CSV");
            }
            _indexBuilt = true;
        }
    }

    /// <summary>
    /// Checks whether the CSV contains data for a given pair.
    /// The pair should be in altname format without slash (e.g. "MATICUSD", "EOSEUR").
    /// </summary>
    public bool HasPair(string pairAltname)
    {
        EnsureIndex();
        return _availablePairs.Contains(pairAltname.ToUpperInvariant());
    }

    /// <summary>
    /// Returns kline data for the given pair, loading it from CSV on first request.
    /// Returns null if the pair is not in the CSV.
    /// The pair should be in altname format without slash (e.g. "MATICUSD").
    /// </summary>
    public List<DerivedKline>? GetKlines(string pairAltname, string symbolWithSlash)
    {
        var key = pairAltname.ToUpperInvariant();
        EnsureIndex();

        if (!_availablePairs.Contains(key))
            return null;

        lock (_lock)
        {
            if (_loadedPairs.TryGetValue(key, out var cached))
                return cached;
        }

        // Load this specific pair from CSV
        var klines = LoadPairFromCsv(key, symbolWithSlash);
        if (klines == null) return null;

        lock (_lock)
        {
            _loadedPairs[key] = klines;
        }

        _logger.LogInformation("[Delisted] Loaded {Count} klines for {Pair} from CSV", klines.Count, key);
        return klines;
    }

    private List<DerivedKline>? LoadPairFromCsv(string upperPairKey, string symbolWithSlash)
    {
        if (!File.Exists(_csvPath)) return null;

        var result = new List<DerivedKline>();
        try
        {
            using var reader = new StreamReader(_csvPath);
            reader.ReadLine(); // skip header

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var commaIdx = line.IndexOf(',');
                if (commaIdx <= 0) continue;

                var pair = line[..commaIdx].Trim();
                if (!string.Equals(pair, upperPairKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Parse: pair,timestamp,open,high,low,close,vwap,volume,count
                var parts = line.Split(',');
                if (parts.Length < 6) continue;

                if (!long.TryParse(parts[1].Trim(), out var ts)) continue;
                if (!decimal.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
                if (!decimal.TryParse(parts[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
                if (!decimal.TryParse(parts[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
                if (!decimal.TryParse(parts[5].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;

                decimal volume = 0, vwap = 0;
                int tradeCount = 0;
                if (parts.Length > 6) decimal.TryParse(parts[6].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out vwap);
                if (parts.Length > 7) decimal.TryParse(parts[7].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out volume);
                if (parts.Length > 8) int.TryParse(parts[8].Trim(), out tradeCount);

                var openTime = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
                var kline = new DerivedKline
                {
                    Asset = symbolWithSlash,
                    OpenTime = openTime,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = volume,
                    VolumeWeightedAveragePrice = vwap,
                    TradeCount = tradeCount,
                    Interval = "OneDay",
                    Key = $"{symbolWithSlash}OneDay{openTime.Ticks}"
                };
                result.Add(kline);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Delisted] Error loading pair {Pair} from CSV", upperPairKey);
            return null;
        }

        result.Sort((a, b) => a.OpenTime.CompareTo(b.OpenTime));
        return result.Count > 0 ? result : null;
    }
}
