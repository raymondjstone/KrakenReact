using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class TransactionCacheRaceTests
{
    /// <summary>A factory that runs a hook the first time a context is opened - i.e. while the cache is loading.</summary>
    private sealed class HookedFactory(InMemoryDbFactory inner, Action onFirstOpen) : IDbContextFactory<KrakenDbContext>
    {
        private int _opened;
        public KrakenDbContext CreateDbContext()
        {
            if (Interlocked.Increment(ref _opened) == 1) onFirstOpen();
            return inner.CreateDbContext();
        }
    }

    [Fact]
    public async Task AnInvalidationDuringALoad_IsNotOverwrittenByTheOlderRows()
    {
        var inner = new InMemoryDbFactory($"race-{Guid.NewGuid()}");
        DbMethods? db = null;
        var factory = new HookedFactory(inner, () => db!.InvalidateTransactionCaches());
        db = new DbMethods(factory, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());

        // The first read starts loading; a sync invalidates the cache while it is still running
        var first = await db.GetTradesAsync();
        Assert.Empty(first);

        // A trade lands after that. If the older (empty) result had been cached, this read would still be empty.
        await using (var ctx = inner.CreateDbContext())
        {
            ctx.Trades.Add(new KrakenUserTrade { Id = "NEW", Symbol = "XBTUSD", Timestamp = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Assert.Single(await db.GetTradesAsync());
    }

    [Fact]
    public async Task WithNoInvalidation_TheLoadIsStillCached()
    {
        var inner = new InMemoryDbFactory($"race-{Guid.NewGuid()}");
        var db = new DbMethods(inner, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        await db.GetTradesAsync();

        await using (var ctx = inner.CreateDbContext())
        {
            ctx.Trades.Add(new KrakenUserTrade { Id = "SNEAKY", Symbol = "XBTUSD", Timestamp = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Assert.Empty(await db.GetTradesAsync());   // served from memory, as designed
    }
}
