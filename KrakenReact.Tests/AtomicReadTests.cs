using KrakenReact.Server.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class AtomicReadTests
{
    // Two values whose 128-bit representations differ in every word, so a torn read would be a third, never-written number
    private const decimal Small = 1m;
    private const decimal Big = 12345678901234567890123456789m;

    [Fact]
    public async Task AtomicDecimal_NeverReturnsATornValue()
    {
        var d = new AtomicDecimal(Small);
        var stop = false;
        var torn = 0;

        var writer = Task.Run(() => { while (!Volatile.Read(ref stop)) { d.Value = Big; d.Value = Small; } });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2_000_000; i++)
            {
                var v = d.Value;
                if (v != Small && v != Big) Interlocked.Increment(ref torn);
            }
        });
        await reader;
        Volatile.Write(ref stop, true);
        await writer;

        Assert.Equal(0, torn);
    }

    [Fact]
    public async Task TickerLastPrice_NeverReadTorn_WhileTheFeedWritesIt()
    {
        var t = new TickerDataItem();
        t.SetQuote(new TickerDataItem.QuoteSnapshot(Last: Small, BestAsk: Small, BestBid: Small));
        var stop = false;
        var torn = 0;

        var writer = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                t.SetQuote(new TickerDataItem.QuoteSnapshot(Last: Big, BestAsk: Big, BestBid: Big));
                t.SetQuote(new TickerDataItem.QuoteSnapshot(Last: Small, BestAsk: Small, BestBid: Small));
            }
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2_000_000; i++)
            {
                var q = t.Quote;   // one snapshot: all fields must belong to the same version
                if ((q.Last != Small && q.Last != Big) || q.Last != q.BestAsk || q.Last != q.BestBid) Interlocked.Increment(ref torn);
            }
        });
        await reader;
        Volatile.Write(ref stop, true);
        await writer;

        Assert.Equal(0, torn);
    }

    [Fact]
    public void QuoteAndStats_AreIndependent_AWriteToOneKeepsTheOther()
    {
        var t = new TickerDataItem();
        t.SetStats(new TickerDataItem.StatsSnapshot(1.5m, 2.5m));
        t.SetQuote(new TickerDataItem.QuoteSnapshot(Last: 100m));

        Assert.Equal(1.5m, t.Change24h);
        Assert.Equal(2.5m, t.ChangePct24h);
        Assert.Equal(100m, t.LastTradePrice);
    }

    [Fact]
    public void PropertySetters_KeepTheOtherFields()
    {
        var t = new TickerDataItem { BestAskPrice = 3m, BestBidPrice = 2m, LastTradePrice = 2.5m, TradeCount = 7 };
        t.LastTradePrice = 9m;

        Assert.Equal(3m, t.BestAskPrice);
        Assert.Equal(2m, t.BestBidPrice);
        Assert.Equal(9m, t.LastTradePrice);
        Assert.Equal(7, t.TradeCount);
    }

    [Fact]
    public async Task ConcurrentSettersOfDifferentFields_DoNotLoseEachOther()
    {
        var t = new TickerDataItem();
        var a = Task.Run(() => { for (var i = 1; i <= 20_000; i++) t.HighPrice = i; });
        var b = Task.Run(() => { for (var i = 1; i <= 20_000; i++) t.LowPrice = i; });
        await Task.WhenAll(a, b);

        Assert.Equal(20_000m, t.HighPrice);
        Assert.Equal(20_000m, t.LowPrice);
    }

    [Fact]
    public async Task EnsureTickerData_RacingCallers_ShareOneInstance()
    {
        for (var round = 0; round < 200; round++)
        {
            var item = new PriceDataItem { Symbol = "XBT/USD" };
            var seen = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => item.EnsureTickerData())));

            Assert.All(seen, s => Assert.Same(seen[0], s));
            Assert.Same(seen[0], item.TickerData);
        }
    }

    [Fact]
    public void Settings_ReadAndWriteThroughTheState()
    {
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        state.StopLossPct = 7.5m;
        state.TakeProfitPct = 20m;
        state.TrailingStopPct = 3m;
        state.AutoSellPercentage = 12m;
        state.OrderProximityThreshold = 1.25m;
        state.DrawdownAlertThreshold = 8m;

        Assert.Equal(7.5m, state.StopLossPct);
        Assert.Equal(20m, state.TakeProfitPct);
        Assert.Equal(3m, state.TrailingStopPct);
        Assert.Equal(12m, state.AutoSellPercentage);
        Assert.Equal(1.25m, state.OrderProximityThreshold);
        Assert.Equal(8m, state.DrawdownAlertThreshold);
    }
}
