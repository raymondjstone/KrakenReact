using System.Collections.Concurrent;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class KlineLookupTests
{
    private static readonly IReadOnlyList<KrakenKline> OneCandle = new[] { new KrakenKline() };

    private static KlineFetch Candles() => new(OneCandle, false);

    private sealed class Harness
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public readonly KlineLookup Lookup;
        public readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = new();
        public Dictionary<string, KlineFetch> Answers = new(StringComparer.OrdinalIgnoreCase);
        public KlineFetch Default = KlineFetch.Unknown;
        public string[] Candidates = ["A/USD", "AUSD", "XA/USD"];

        public Harness() => Lookup = new KlineLookup(() => Now);

        public Task<IReadOnlyList<KrakenKline>> Resolve(string pair) => Lookup.ResolveAsync(pair,
            name => { Calls.Add(name); return Task.FromResult(Answers.TryGetValue(name, out var a) ? a : Default); },
            Names, _ => Candidates);
    }

    [Fact]
    public async Task ThePairAsGiven_IsTriedFirst_AndRemembered()
    {
        var h = new Harness { Answers = { ["A/USD"] = Candles() } };
        var r = await h.Resolve("A/USD");

        Assert.Single(r);
        Assert.Equal(["A/USD"], h.Calls);
        Assert.Equal("A/USD", h.Names["A/USD"]);
    }

    [Fact]
    public async Task AnAlternateSpelling_IsFoundAndRemembered_AndUsedDirectlyNextTime()
    {
        var h = new Harness { Answers = { ["XA/USD"] = Candles() } };
        await h.Resolve("A/USD");
        Assert.Equal("XA/USD", h.Names["A/USD"]);

        h.Calls.Clear();
        await h.Resolve("A/USD");
        Assert.Equal(["XA/USD"], h.Calls);   // straight to the remembered name
    }

    [Fact]
    public async Task EveryNameUnknown_MarksThePairBad_SoTheNextAskCostsNoCalls()
    {
        var h = new Harness();
        Assert.Empty(await h.Resolve("A/USD"));
        Assert.True(h.Lookup.IsKnownBad("A/USD"));

        h.Calls.Clear();
        Assert.Empty(await h.Resolve("A/USD"));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task TheBadMark_ExpiresAfterTheTtl()
    {
        var h = new Harness();
        await h.Resolve("A/USD");

        h.Now += KlineLookup.NegativeTtl + TimeSpan.FromSeconds(1);
        Assert.False(h.Lookup.IsKnownBad("A/USD"));

        h.Calls.Clear();
        await h.Resolve("A/USD");
        Assert.NotEmpty(h.Calls);   // asked the exchange again
    }

    [Fact]
    public async Task ATransientFailure_NeverMarksThePairBad()
    {
        // One name times out, the rest are "unknown pair": the pair may well exist, so it must not be written off
        var h = new Harness { Answers = { ["AUSD"] = KlineFetch.Failed } };
        Assert.Empty(await h.Resolve("A/USD"));

        Assert.False(h.Lookup.IsKnownBad("A/USD"));
    }

    [Fact]
    public async Task AllNamesFailing_NeverMarksThePairBad()
    {
        var h = new Harness { Default = KlineFetch.Failed };
        await h.Resolve("A/USD");
        Assert.False(h.Lookup.IsKnownBad("A/USD"));
    }

    [Fact]
    public async Task AKnownPairWithNoCandlesInTheWindow_IsNotBad()
    {
        var h = new Harness { Default = new KlineFetch(Array.Empty<KrakenKline>(), false) };
        await h.Resolve("A/USD");
        Assert.False(h.Lookup.IsKnownBad("A/USD"));
    }

    [Fact]
    public async Task CandidatesAreCapped_AndDuplicatesTriedOnce()
    {
        var h = new Harness { Candidates = ["a/usd", "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8"] };
        await h.Resolve("A/USD");

        Assert.Equal(KlineLookup.MaxCandidates, h.Calls.Count);
        Assert.Single(h.Calls, c => c.Equals("A/USD", StringComparison.OrdinalIgnoreCase));   // "a/usd" was not tried again
    }

    [Fact]
    public async Task AStaleRememberedName_IsDropped_AndTheSearchStartsOver()
    {
        var h = new Harness { Answers = { ["XA/USD"] = Candles() } };
        h.Names["A/USD"] = "OLD/USD";   // remembered, but no longer works

        var r = await h.Resolve("A/USD");

        Assert.Single(r);
        Assert.Equal("XA/USD", h.Names["A/USD"]);
    }

    [Fact]
    public async Task ASuccessfulLookup_ClearsAPreviousBadMark()
    {
        var h = new Harness();
        await h.Resolve("A/USD");
        Assert.True(h.Lookup.IsKnownBad("A/USD") );

        h.Now += KlineLookup.NegativeTtl + TimeSpan.FromSeconds(1);
        h.Answers["A/USD"] = Candles();
        Assert.Single(await h.Resolve("A/USD"));
        Assert.False(h.Lookup.IsKnownBad("A/USD"));
    }

    [Fact]
    public async Task ABlankPair_CostsNothing()
    {
        var h = new Harness();
        Assert.Empty(await h.Resolve("  "));
        Assert.Empty(h.Calls);
    }
}

public class KlineResponseCacheTests
{
    [Fact]
    public void AnEntry_IsServedWithinTheWindow_AndNotAfter()
    {
        var now = DateTime.UtcNow;
        var cache = new KlineResponseCache<string>(() => now);
        var key = KlineResponseCache<string>.Key("XBT/USD", "OneDay");
        cache.Set(key, "chart");

        Assert.True(cache.TryGet(key, out var v));
        Assert.Equal("chart", v);

        now += KlineResponseCache<string>.Ttl + TimeSpan.FromSeconds(1);
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void PairAndInterval_AreSeparateKeys()
    {
        var cache = new KlineResponseCache<string>();
        cache.Set(KlineResponseCache<string>.Key("XBT/USD", "OneDay"), "day");
        cache.Set(KlineResponseCache<string>.Key("XBT/USD", "OneHour"), "hour");

        Assert.True(cache.TryGet(KlineResponseCache<string>.Key("xbt/usd", "OneDay"), out var d));
        Assert.Equal("day", d);
        Assert.True(cache.TryGet(KlineResponseCache<string>.Key("XBT/USD", "OneHour"), out var h));
        Assert.Equal("hour", h);
        Assert.False(cache.TryGet(KlineResponseCache<string>.Key("ETH/USD", "OneDay"), out _));
    }

    [Fact]
    public void TheCacheStaysBounded()
    {
        var cache = new KlineResponseCache<int>();
        for (var i = 0; i < 1000; i++) cache.Set($"p{i}|OneDay", i);

        var alive = Enumerable.Range(0, 1000).Count(i => cache.TryGet($"p{i}|OneDay", out _));
        Assert.InRange(alive, 1, 200);
    }
}
