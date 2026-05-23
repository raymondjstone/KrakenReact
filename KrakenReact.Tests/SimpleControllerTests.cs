using Kraken.Net.Objects.Models;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

// ── SymbolsController ─────────────────────────────────────────────────────────

public class SymbolsControllerTests
{
    private static (SymbolsController controller, TradingStateService state) Create()
    {
        var log = new Mock<ILogger<DelistedPriceService>>();
        var state = new TradingStateService(new DelistedPriceService(log.Object));
        return (new SymbolsController(state), state);
    }

    [Fact]
    public void GetAll_EmptyState_ReturnsEmpty()
    {
        var (ctrl, _) = Create();
        var ok = Assert.IsType<OkObjectResult>(ctrl.GetAll().Result);
        var list = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ok.Value);
        Assert.Empty(list.Cast<object>());
    }

    [Fact]
    public void GetAll_FiltersByBaseCurrencies()
    {
        var (ctrl, state) = Create();
        state.Symbols["XBTUSD"] = new KrakenSymbol { WebsocketName = "XBT/USD", BaseAsset = "XXBT", QuoteAsset = "ZUSD" };
        state.Symbols["XBTRUB"] = new KrakenSymbol { WebsocketName = "XBT/RUB", BaseAsset = "XXBT", QuoteAsset = "ZRUB" };

        var ok = Assert.IsType<OkObjectResult>(ctrl.GetAll().Result);
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        // Only ZUSD passes through TradingStateService.BaseCurrencies filter
        Assert.Single(list);
    }

    [Fact]
    public void GetAll_OrdersByWebsocketName()
    {
        var (ctrl, state) = Create();
        state.Symbols["A"] = new KrakenSymbol { WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD" };
        state.Symbols["B"] = new KrakenSymbol { WebsocketName = "ETH/USD", BaseAsset = "XETH", QuoteAsset = "ZUSD" };
        state.Symbols["C"] = new KrakenSymbol { WebsocketName = "ADA/USD", BaseAsset = "ADA", QuoteAsset = "ZUSD" };

        var ok = Assert.IsType<OkObjectResult>(ctrl.GetAll().Result);
        var first = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().First();
        // First entry should be the alphabetically smallest WebsocketName: ADA/USD
        var nameProp = first.GetType().GetProperty("WebsocketName")!;
        Assert.Equal("ADA/USD", (string?)nameProp.GetValue(first));
    }
}

// ── AutoTradeController ───────────────────────────────────────────────────────

public class AutoTradeControllerTests
{
    private static (AutoTradeController controller, TradingStateService state) Create()
    {
        var log = new Mock<ILogger<DelistedPriceService>>();
        var state = new TradingStateService(new DelistedPriceService(log.Object));
        return (new AutoTradeController(state), state);
    }

    [Fact]
    public void GetAll_Empty_ReturnsEmpty()
    {
        var (ctrl, _) = Create();
        var ok = Assert.IsType<OkObjectResult>(ctrl.GetAll().Result);
        var list = Assert.IsType<List<AutoTradeDto>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public void GetAll_ReturnsAllEntries()
    {
        var (ctrl, state) = Create();
        state.AutoOrders["SOL/USD"] = new AutoTradeDto { Symbol = "SOL/USD", Base = "SOL", OrderWanted = true };
        state.AutoOrders["ETH/USD"] = new AutoTradeDto { Symbol = "ETH/USD", Base = "ETH", OrderRanking = 5 };

        var ok = Assert.IsType<OkObjectResult>(ctrl.GetAll().Result);
        var list = Assert.IsType<List<AutoTradeDto>>(ok.Value);
        Assert.Equal(2, list.Count);
    }
}

// ── DelistedPairsController ──────────────────────────────────────────────────

public class DelistedPairsControllerTests
{
    private static (DelistedPairsController controller, TradingStateService state, DelistedPriceService delisted) Create()
    {
        var log = new Mock<ILogger<DelistedPriceService>>();
        var delisted = new DelistedPriceService(log.Object);
        var state = new TradingStateService(delisted);
        return (new DelistedPairsController(state, delisted), state, delisted);
    }

    [Fact]
    public void GetDelistedPairs_EmptyState_ReturnsEmptyOrCsvOnly()
    {
        var (ctrl, _, _) = Create();
        var ok = Assert.IsType<OkObjectResult>(ctrl.GetDelistedPairs().Result);
        var list = Assert.IsType<List<DelistedPairDto>>(ok.Value);
        // Should not throw; result depends on whether csv file is bundled with tests
        Assert.NotNull(list);
    }

    [Fact]
    public void GetDelistedPairs_ActivePair_MarkedActive()
    {
        var (ctrl, state, _) = Create();
        state.Symbols["SOLUSD"] = new KrakenSymbol { WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD" };

        var price = new PriceDataItem { Symbol = "SOL/USD" };
        price.AddKline(new DerivedKline { Asset = "SOL/USD", Close = 100m, OpenTime = DateTime.UtcNow });
        state.Prices["SOL/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(ctrl.GetDelistedPairs().Result);
        var list = Assert.IsType<List<DelistedPairDto>>(ok.Value);
        var sol = list.FirstOrDefault(p => p.Symbol == "SOL/USD");
        Assert.NotNull(sol);
        Assert.Equal("active", sol!.Status);
        Assert.Equal(100m, sol.LastPrice);
    }

    [Fact]
    public void GetDelistedPairs_PriceWithoutMatchingSymbol_MarkedDelisted()
    {
        var (ctrl, state, _) = Create();
        // No entry in state.Symbols → considered delisted
        var price = new PriceDataItem { Symbol = "OLDCOIN/USD" };
        price.AddKline(new DerivedKline { Asset = "OLDCOIN/USD", Close = 5m, OpenTime = DateTime.UtcNow.AddDays(-100) });
        state.Prices["OLDCOIN/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(ctrl.GetDelistedPairs().Result);
        var list = Assert.IsType<List<DelistedPairDto>>(ok.Value);
        var old = list.FirstOrDefault(p => p.Symbol == "OLDCOIN/USD");
        Assert.NotNull(old);
        Assert.Equal("delisted", old!.Status);
    }

    [Fact]
    public void GetDelistedPairs_ResultIsOrderedByStatusThenSymbol()
    {
        var (ctrl, state, _) = Create();
        state.Symbols["BBBUSD"] = new KrakenSymbol { WebsocketName = "BBB/USD", BaseAsset = "BBB", QuoteAsset = "ZUSD" };
        state.Symbols["AAAUSD"] = new KrakenSymbol { WebsocketName = "AAA/USD", BaseAsset = "AAA", QuoteAsset = "ZUSD" };

        foreach (var sym in new[] { "AAA/USD", "BBB/USD", "CCC/USD" })
        {
            var p = new PriceDataItem { Symbol = sym };
            p.AddKline(new DerivedKline { Asset = sym, Close = 1m, OpenTime = DateTime.UtcNow });
            state.Prices[sym] = p;
        }

        var ok = Assert.IsType<OkObjectResult>(ctrl.GetDelistedPairs().Result);
        var list = Assert.IsType<List<DelistedPairDto>>(ok.Value);
        // Status sort puts 'active' before 'delisted' alphabetically
        var statuses = list.Where(p => new[] { "AAA/USD", "BBB/USD", "CCC/USD" }.Contains(p.Symbol)).Select(p => p.Status).ToList();
        // First two should be active, last should be delisted
        Assert.Equal("active", statuses[0]);
        Assert.Equal("active", statuses[1]);
        Assert.Equal("delisted", statuses[2]);
    }
}

// ── DelistedPriceService ─────────────────────────────────────────────────────

public class DelistedPriceServiceTests
{
    private static DelistedPriceService Create()
    {
        var log = new Mock<ILogger<DelistedPriceService>>();
        return new DelistedPriceService(log.Object);
    }

    [Fact]
    public void HasPair_UnknownPair_ReturnsFalse()
    {
        Assert.False(Create().HasPair("THISWILLNEVEREXIST_XYZ"));
    }

    [Fact]
    public void GetKlines_UnknownPair_ReturnsNull()
    {
        Assert.Null(Create().GetKlines("THISWILLNEVEREXIST_XYZ", "XYZ/USD"));
    }

    [Fact]
    public void GetAvailablePairs_ReturnsAList()
    {
        // Whether CSV is present or not, this should return a list without throwing
        var pairs = Create().GetAvailablePairs();
        Assert.NotNull(pairs);
    }

    [Fact]
    public void HasPair_CaseInsensitive()
    {
        var svc = Create();
        // Even if no CSV is bundled, lookups are case-insensitive — checking same lookup with mixed cases
        // returns same false result without throwing
        Assert.Equal(svc.HasPair("matIcusd"), svc.HasPair("MATICUSD"));
    }
}
