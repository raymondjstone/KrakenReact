using System.Collections.Concurrent;
using Kraken.Net.Objects.Models;

namespace KrakenReact.Server.Services;

/// <summary>One attempt to fetch candles for one pair name. <see cref="UnknownPair"/> is true only when Kraken definitively said the
/// pair does not exist - a timeout or rate limit is a failure, not proof the pair is bad.</summary>
public sealed record KlineFetch(IReadOnlyList<KrakenKline> Candles, bool UnknownPair)
{
    public static readonly KlineFetch Failed = new(Array.Empty<KrakenKline>(), false);
    public static readonly KlineFetch Unknown = new(Array.Empty<KrakenKline>(), true);
}

/// <summary>
/// Finds the Kraken pair name that has candles: the remembered name, then the pair as given, then a capped list of alternate
/// spellings. A pair every attempt of which is definitively "unknown" is remembered as bad for a while so a chart (or a job) asking
/// for a delisted or mistyped pair over and over does not spend up to a dozen exchange calls each time. A pair where any attempt
/// merely failed (timeout, rate limit) is never remembered as bad.
/// </summary>
public sealed class KlineLookup
{
    /// <summary>Most names tried for one lookup (the pair as given plus alternates) - each is a network call.</summary>
    public const int MaxCandidates = 6;
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);
    private const int MaxNegativeEntries = 1000;

    private readonly ConcurrentDictionary<string, DateTime> _unknownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;

    public KlineLookup(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public bool IsKnownBad(string pair) => _unknownUntil.TryGetValue(pair, out var until) && until > _now();

    public async Task<IReadOnlyList<KrakenKline>> ResolveAsync(
        string pair,
        Func<string, Task<KlineFetch>> fetch,
        ConcurrentDictionary<string, string> nameCache,
        Func<string, IEnumerable<string>> candidatesFor)
    {
        if (string.IsNullOrWhiteSpace(pair)) return Array.Empty<KrakenKline>();
        if (IsKnownBad(pair)) return Array.Empty<KrakenKline>();

        var allUnknown = true;

        // A name that worked before
        if (nameCache.TryGetValue(pair, out var cachedName))
        {
            var r = await fetch(cachedName);
            if (r.Candles.Count > 0) return r.Candles;
            if (!r.UnknownPair) allUnknown = false;
            nameCache.TryRemove(pair, out _);   // stale - resolve again
        }

        // The pair as given, then the alternates - each name once, and no more than the cap
        var names = new List<string> { pair };
        foreach (var c in candidatesFor(pair))
            if (!names.Contains(c, StringComparer.OrdinalIgnoreCase)) names.Add(c);

        foreach (var name in names.Take(MaxCandidates))
        {
            var r = await fetch(name);
            if (r.Candles.Count > 0)
            {
                nameCache[pair] = name;
                _unknownUntil.TryRemove(pair, out _);
                return r.Candles;
            }
            if (!r.UnknownPair) allUnknown = false;
        }

        if (allUnknown) MarkBad(pair);
        return Array.Empty<KrakenKline>();
    }

    private void MarkBad(string pair)
    {
        if (_unknownUntil.Count >= MaxNegativeEntries)
        {
            var now = _now();
            foreach (var kv in _unknownUntil.Where(k => k.Value <= now).ToList()) _unknownUntil.TryRemove(kv.Key, out _);
            if (_unknownUntil.Count >= MaxNegativeEntries) _unknownUntil.Clear();
        }
        _unknownUntil[pair] = _now() + NegativeTtl;
    }
}

/// <summary>
/// Short-lived cache of the chart endpoint's answer per (pair, interval). Opening a chart, switching tabs and the auto-refresh all
/// ask for the same candles within seconds; the exchange is asked once per window instead of every time.
/// </summary>
public sealed class KlineResponseCache<T>
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);
    private const int MaxEntries = 200;

    private readonly ConcurrentDictionary<string, (DateTime Expires, T Value)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;

    public KlineResponseCache(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public static string Key(string pair, string interval) => $"{pair}|{interval}";

    public bool TryGet(string key, out T value)
    {
        if (_entries.TryGetValue(key, out var e) && e.Expires > _now()) { value = e.Value; return true; }
        value = default!;
        return false;
    }

    public void Set(string key, T value)
    {
        if (_entries.Count >= MaxEntries)
        {
            var now = _now();
            foreach (var kv in _entries.Where(k => k.Value.Expires <= now).ToList()) _entries.TryRemove(kv.Key, out _);
            if (_entries.Count >= MaxEntries) _entries.Clear();
        }
        _entries[key] = (_now() + Ttl, value);
    }
}
