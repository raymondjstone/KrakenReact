using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Kraken.Net.Objects.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class OrderPairCacheTests
{
    private static TradingStateService Make()
    {
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        state.Symbols["XBT/USD"] = new KrakenSymbol { WebsocketName = "XBT/USD", BaseAsset = "XBT", QuoteAsset = "ZUSD", AlternateName = "XBTUSD" };
        state.Symbols["ETH/USD"] = new KrakenSymbol { WebsocketName = "ETH/USD", BaseAsset = "XETH", QuoteAsset = "ZUSD", AlternateName = "ETHUSD" };
        return state;
    }

    [Theory]
    [InlineData("XBTUSD")]
    [InlineData("ETHUSD")]
    [InlineData("XBT/USD")]
    [InlineData("UNKNOWNZUSD")]
    public void GivesTheSameAnswerAsTheDirectNormalization(string symbol)
    {
        var state = Make();
        var order = new OrderDto { Id = "1", Symbol = symbol };

        var (b, q) = state.OrderPair(order);

        Assert.Equal(state.NormalizeOrderSymbolBase(symbol), b);
        Assert.Equal(state.NormalizeOrderSymbolQuote(symbol), q);
    }

    [Fact]
    public void ChangingTheOrdersSymbol_IsNotServedFromTheOldAnswer()
    {
        var state = Make();
        var order = new OrderDto { Id = "1", Symbol = "XBTUSD" };
        Assert.Equal("BTC", state.OrderPair(order).Base);

        order.Symbol = "ETHUSD";
        Assert.Equal(state.NormalizeOrderSymbolBase("ETHUSD"), state.OrderPair(order).Base);
    }

    [Fact]
    public void ReloadingTheSymbolList_RefreshesTheAnswer()
    {
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        var order = new OrderDto { Id = "1", Symbol = "FOOUSD" };
        var before = state.OrderPair(order);                       // symbols not loaded yet: only the suffix heuristic applies

        state.Symbols["FOO/USD"] = new KrakenSymbol { WebsocketName = "FOO/USD", BaseAsset = "FOO", QuoteAsset = "ZUSD", AlternateName = "FOOUSD" };

        Assert.Equal(state.NormalizeOrderSymbolBase("FOOUSD"), state.OrderPair(order).Base);
        Assert.Equal(state.NormalizeOrderSymbolQuote("FOOUSD"), state.OrderPair(order).Quote);
        _ = before;
    }
}
