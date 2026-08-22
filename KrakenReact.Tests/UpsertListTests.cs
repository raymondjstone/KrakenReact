using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

/// <summary>
/// UpsertListAsync sits on the write path for trades, ledgers, closed orders and balances, so the
/// batching rewrite has to preserve its behaviour exactly: insert what is new, update what changed,
/// and never lose a row.
/// </summary>
public class UpsertListTests
{
    private static DbMethods Make(out InMemoryDbFactory factory)
    {
        factory = new InMemoryDbFactory($"upsert-{Guid.NewGuid()}");
        return new DbMethods(factory, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
    }

    private static DerivedKline Kline(string key, decimal close) => new()
    {
        Key = key, Asset = "XBT/USD", Interval = "OneDay",
        OpenTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Open = 1m, High = 2m, Low = 0.5m, Close = close, Volume = 10m,
    };

    [Fact]
    public async Task Upsert_InsertsRowsThatAreNew()
    {
        var db = Make(out var factory);
        await db.UpsertListAsync([Kline("a", 1m), Kline("b", 2m)], k => k.Key);

        await using var ctx = factory.CreateDbContext();
        Assert.Equal(2, await ctx.DerivedKlines.CountAsync());
    }

    [Fact]
    public async Task Upsert_UpdatesRowsThatAlreadyExist()
    {
        var db = Make(out var factory);
        await db.UpsertListAsync([Kline("a", 1m)], k => k.Key);
        await db.UpsertListAsync([Kline("a", 99m)], k => k.Key);

        await using var ctx = factory.CreateDbContext();
        var row = Assert.Single(await ctx.DerivedKlines.ToListAsync());
        Assert.Equal(99m, row.Close);
    }

    [Fact]
    public async Task Upsert_HandlesAMixOfNewAndExisting()
    {
        var db = Make(out var factory);
        await db.UpsertListAsync([Kline("a", 1m)], k => k.Key);
        await db.UpsertListAsync([Kline("a", 5m), Kline("b", 6m)], k => k.Key);

        await using var ctx = factory.CreateDbContext();
        var rows = await ctx.DerivedKlines.OrderBy(k => k.Key).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(5m, rows[0].Close);
        Assert.Equal(6m, rows[1].Close);
    }

    [Fact]
    public async Task Upsert_SurvivesTheSameKeyTwiceInOneBatch()
    {
        // The batched form matches against a dictionary, so a duplicate inside one batch must resolve
        // to a single row rather than two inserts colliding on the primary key.
        var db = Make(out var factory);
        await db.UpsertListAsync([Kline("a", 1m), Kline("a", 2m)], k => k.Key);

        await using var ctx = factory.CreateDbContext();
        var row = Assert.Single(await ctx.DerivedKlines.ToListAsync());
        Assert.Equal(2m, row.Close);
    }

    [Fact]
    public async Task Upsert_WritesEveryRowAcrossSeveralBatches()
    {
        // Batches are capped at fifty rows to avoid lock escalation; nothing may be dropped at a
        // boundary.
        var db = Make(out var factory);
        var many = Enumerable.Range(0, 137).Select(i => Kline($"k{i:D4}", i)).ToList();
        await db.UpsertListAsync(many, k => k.Key);

        await using var ctx = factory.CreateDbContext();
        Assert.Equal(137, await ctx.DerivedKlines.CountAsync());
    }

    [Fact]
    public async Task Upsert_UpdatesEveryRowAcrossSeveralBatches()
    {
        var db = Make(out var factory);
        var many = Enumerable.Range(0, 137).Select(i => Kline($"k{i:D4}", i)).ToList();
        await db.UpsertListAsync(many, k => k.Key);
        await db.UpsertListAsync(many.Select(k => Kline(k.Key, 1000m)).ToList(), k => k.Key);

        await using var ctx = factory.CreateDbContext();
        Assert.Equal(137, await ctx.DerivedKlines.CountAsync());
        Assert.All(await ctx.DerivedKlines.ToListAsync(), k => Assert.Equal(1000m, k.Close));
    }

    [Fact]
    public async Task Upsert_DoesNothingWithAnEmptyList()
    {
        var db = Make(out var factory);
        await db.UpsertListAsync(new List<DerivedKline>(), k => k.Key);

        await using var ctx = factory.CreateDbContext();
        Assert.Equal(0, await ctx.DerivedKlines.CountAsync());
    }

    [Fact]
    public async Task Upsert_HonoursACustomUpdateThatKeepsExistingValues()
    {
        // The custom updater is how a caller protects fields the incoming row should not clobber.
        var db = Make(out var factory);
        await db.UpsertListAsync([Kline("a", 1m)], k => k.Key);
        await db.UpsertListAsync([Kline("a", 99m)], k => k.Key,
            updateValues: (existing, incoming) => existing.Volume = incoming.Volume);

        await using var ctx = factory.CreateDbContext();
        var row = Assert.Single(await ctx.DerivedKlines.ToListAsync());
        Assert.Equal(1m, row.Close);      // left alone by the custom updater
    }

    // ── Transaction history cache ───────────────────────────────────────────

    [Fact]
    public async Task Cache_ServesTheSameTradesWithoutGoingBackToTheDatabase()
    {
        var db = Make(out var factory);
        await db.AddTradesAsync([new KrakenUserTrade { Id = "T1", Symbol = "XBTUSD", Timestamp = DateTime.UtcNow }]);

        var first = await db.GetTradesAsync();

        // Write behind the cache's back. Nothing in the real app can do this — every write goes
        // through DbMethods — but it proves the second read came from memory, not the table.
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Trades.Add(new KrakenUserTrade { Id = "SNEAKY", Symbol = "ETHUSD", Timestamp = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var second = await db.GetTradesAsync();
        Assert.Single(first);
        Assert.Single(second);
    }

    [Fact]
    public async Task Cache_IsDroppedWhenTradesAreWritten()
    {
        // Staleness here would feed a wrong tax report and a wrong P&L, so a write must invalidate.
        var db = Make(out _);
        await db.AddTradesAsync([new KrakenUserTrade { Id = "T1", Symbol = "XBTUSD", Timestamp = DateTime.UtcNow }]);
        Assert.Single(await db.GetTradesAsync());

        await db.AddTradesAsync([new KrakenUserTrade { Id = "T2", Symbol = "ETHUSD", Timestamp = DateTime.UtcNow }]);
        Assert.Equal(2, (await db.GetTradesAsync()).Count);
    }

    [Fact]
    public async Task Cache_IsDroppedWhenLedgersAreWritten()
    {
        var db = Make(out _);
        await db.AddLedgersAsync([new KrakenLedgerEntry { Id = "L1", Asset = "XBT", Timestamp = DateTime.UtcNow }]);
        Assert.Single(await db.GetLedgersAsync());

        await db.AddLedgersAsync([new KrakenLedgerEntry { Id = "L2", Asset = "ETH", Timestamp = DateTime.UtcNow }]);
        Assert.Equal(2, (await db.GetLedgersAsync()).Count);
    }

    [Fact]
    public async Task Cache_IsDroppedWhenClosedOrdersAreWritten()
    {
        var db = Make(out _);
        await db.AddCombinedOrdersAsync([new CombinedOrder { Id = "O1", Symbol = "XBTUSD" }]);
        Assert.Single(await db.GetCombinedOrdersAsync());

        await db.AddCombinedOrdersAsync([new CombinedOrder { Id = "O2", Symbol = "ETHUSD" }]);
        Assert.Equal(2, (await db.GetCombinedOrdersAsync()).Count);
    }

    [Fact]
    public async Task Cache_ReflectsAnUpdateToAnExistingRow()
    {
        // An upsert that changes a row in place is still a write, and must invalidate just the same
        // as one that inserts.
        var db = Make(out _);
        await db.AddTradesAsync([new KrakenUserTrade { Id = "T1", Symbol = "XBTUSD", Quantity = 1m, Timestamp = DateTime.UtcNow }]);
        Assert.Equal(1m, (await db.GetTradesAsync())[0].Quantity);

        await db.AddTradesAsync([new KrakenUserTrade { Id = "T1", Symbol = "XBTUSD", Quantity = 99m, Timestamp = DateTime.UtcNow }]);
        Assert.Equal(99m, (await db.GetTradesAsync())[0].Quantity);
    }

    [Fact]
    public async Task Cache_KeepsTheTablesIndependent()
    {
        // Writing trades must not throw away the ledger copy; they are separate caches.
        var db = Make(out var factory);
        await db.AddLedgersAsync([new KrakenLedgerEntry { Id = "L1", Asset = "XBT", Timestamp = DateTime.UtcNow }]);
        await db.GetLedgersAsync();

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Ledgers.Add(new KrakenLedgerEntry { Id = "SNEAKY", Asset = "ETH", Timestamp = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await db.AddTradesAsync([new KrakenUserTrade { Id = "T1", Symbol = "XBTUSD", Timestamp = DateTime.UtcNow }]);
        Assert.Single(await db.GetLedgersAsync());   // still the cached ledger copy
    }

    [Fact]
    public async Task Cache_CanBeDroppedExplicitly()
    {
        var db = Make(out var factory);
        await db.AddTradesAsync([new KrakenUserTrade { Id = "T1", Symbol = "XBTUSD", Timestamp = DateTime.UtcNow }]);
        await db.GetTradesAsync();

        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Trades.Add(new KrakenUserTrade { Id = "T2", Symbol = "ETHUSD", Timestamp = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        db.InvalidateTransactionCaches();
        Assert.Equal(2, (await db.GetTradesAsync()).Count);
    }

    [Fact]
    public async Task Cache_LoadsOnceUnderConcurrentFirstReads()
    {
        // Startup is exactly when a cold cache gets hit from several directions at once.
        var db = Make(out _);
        await db.AddTradesAsync([new KrakenUserTrade { Id = "T1", Symbol = "XBTUSD", Timestamp = DateTime.UtcNow }]);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => db.GetTradesAsync()));
        Assert.All(results, r => Assert.Single(r));
    }

    [Fact]
    public async Task Upsert_WorksForAStringKeyedEntity()
    {
        // Trades are keyed by a string id, so the IN predicate must build for that too.
        var db = Make(out var factory);
        var trades = new List<KrakenUserTrade>
        {
            new() { Id = "T1", Symbol = "XBTUSD", Timestamp = DateTime.UtcNow },
            new() { Id = "T2", Symbol = "ETHUSD", Timestamp = DateTime.UtcNow },
        };
        await db.AddTradesAsync(trades);

        await using var ctx = factory.CreateDbContext();
        Assert.Equal(2, await ctx.Trades.CountAsync());
    }
}
