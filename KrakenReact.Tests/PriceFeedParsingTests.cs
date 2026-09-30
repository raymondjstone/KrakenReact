using KrakenReact.Server.Data;
using KrakenReact.Server.Hubs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

/// <summary>The V1 ticker handler: raw Kraken frames in, price state and broadcasts out.</summary>
public class PriceFeedParsingTests
{
    private readonly TradingStateService _state = new(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
    private readonly Mock<IClientProxy> _all = new();
    private readonly KrakenWebSocketV1Service _feed;

    public PriceFeedParsingTests()
    {
        _all.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(_all.Object);
        var hub = new Mock<IHubContext<TradingHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var db = new DbMethods(new Mock<IDbContextFactory<KrakenDbContext>>().Object, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var notifications = new NotificationService(db, new Mock<IDbContextFactory<KrakenDbContext>>().Object, new Mock<ILogger<NotificationService>>().Object);
        var kraken = new KrakenRestService(db, _state, new Mock<ILogger<KrakenRestService>>().Object);
        var autoOrder = new AutoOrderService(_state, notifications, kraken, new Mock<ILogger<AutoOrderService>>().Object);
        _feed = new KrakenWebSocketV1Service(_state, autoOrder, notifications, hub.Object, new Mock<ILogger<KrakenWebSocketV1Service>>().Object);
    }

    /// <summary>A ticker frame as Kraken sends it: numbers are strings, values are arrays.</summary>
    private static string Frame(string pair, string last, string ask = "50001.0", string bid = "49999.0") =>
        "[340,{\"a\":[\"" + ask + "\",1,\"1.000\"],\"b\":[\"" + bid + "\",2,\"2.000\"],\"c\":[\"" + last + "\",\"0.1\"]," +
        "\"v\":[\"10\",\"20\"],\"p\":[\"50000.5\",\"50000.6\"],\"t\":[100,200],\"l\":[\"49000\",\"48000\"],\"h\":[\"51000\",\"52000\"],\"o\":[\"49500\",\"49400\"]},\"ticker\",\"" + pair + "\"]";

    [Fact]
    public void ATickerFrame_UpdatesThePriceAndTheQuotes()
    {
        _feed.ProcessMessage(Frame("XBT/USD", "50000.1"));

        var item = _state.Prices["XBT/USD"];
        Assert.Equal(50000.1m, item.TickerData!.LastTradePrice);
        Assert.Equal(50001.0m, item.TickerData.BestAskPrice);
        Assert.Equal(49999.0m, item.TickerData.BestBidPrice);
        Assert.Equal(50000.1m, item.LatestKline!.Close);
        Assert.Equal("OneMinute", item.LatestKline.Interval);
    }

    [Fact]
    public void EveryTick_MarksTheFeedAlive()
    {
        Assert.False(_state.IsPriceFeedAlive());
        _feed.ProcessMessage(Frame("XBT/USD", "50000"));
        Assert.True(_state.IsPriceFeedAlive());
    }

    [Fact]
    public void ManyTicks_LeaveASingleLiveKline_AndDailyHistoryIntact()
    {
        var item = _state.GetOrAddPrice("XBT/USD");
        item.AddKlineHistory(new List<DerivedKline> { new() { Interval = "OneDay", OpenTime = DateTime.UtcNow.AddDays(-400), Close = 10m } });

        for (var i = 0; i < 200; i++) _feed.ProcessMessage(Frame("XBT/USD", (50000 + i).ToString()));

        var klines = item.GetKlineSnapshot();
        Assert.Equal(2, klines.Count);                       // the year-old daily bar + exactly one live tick
        Assert.Equal(50199m, item.LatestKline!.Close);
        Assert.Contains(klines, k => k.Interval == "OneDay");
    }

    [Fact]
    public void ATick_KeepsThe24hChangeThatTheOtherFeedSet()
    {
        var item = _state.GetOrAddPrice("XBT/USD");
        item.TickerData = new TickerDataItem { ChangePct24h = -4.2m, Change24h = -2000m };

        _feed.ProcessMessage(Frame("XBT/USD", "50000"));

        Assert.Equal(-4.2m, item.TickerData!.ChangePct24h);  // the V1 payload has no such field; it must not be wiped
        Assert.Equal(50000m, item.TickerData.LastTradePrice);
    }

    [Fact]
    public void EachPairKeepsItsOwnPrice()
    {
        _feed.ProcessMessage(Frame("XBT/USD", "50000"));
        _feed.ProcessMessage(Frame("ETH/USD", "3000"));

        Assert.Equal(50000m, _state.Prices["XBT/USD"].TickerData!.LastTradePrice);
        Assert.Equal(3000m, _state.Prices["ETH/USD"].TickerData!.LastTradePrice);
    }

    [Fact]
    public void TickerBroadcasts_AreThrottledPerPair()
    {
        for (var i = 0; i < 10; i++) _feed.ProcessMessage(Frame("XBT/USD", (50000 + i).ToString()));

        _all.Verify(c => c.SendCoreAsync("TickerUpdate", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("{\"event\":\"heartbeat\"}")]                          // control message, not an array
    [InlineData("{\"event\":\"systemStatus\",\"status\":\"online\"}")]
    [InlineData("[340,{\"c\":[\"1\",\"1\"]},\"ohlc-1\",\"XBT/USD\"]")]  // some other channel
    [InlineData("[1,2]")]                                               // too short
    public void NonTickerFrames_AreIgnored(string frame)
    {
        _feed.ProcessMessage(frame);
        Assert.Empty(_state.Prices);
        Assert.False(_state.IsPriceFeedAlive());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[340,{\"c\":")]                                        // truncated
    [InlineData("[340,\"oops\",\"ticker\",\"XBT/USD\"]")]                // payload of the wrong shape
    public void BadInput_NeverThrows(string? frame)
    {
        var ex = Record.Exception(() => _feed.ProcessMessage(frame));
        Assert.Null(ex);
    }
}
