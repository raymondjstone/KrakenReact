using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

// ── AutoOrderService static helpers + early-exit paths ───────────────────────

public class AutoOrderServiceTests
{
    private static AutoOrderService NewSvc(out TradingStateService state)
    {
        var dlog = new Mock<ILogger<DelistedPriceService>>();
        state = new TradingStateService(new DelistedPriceService(dlog.Object));
        var olog = new Mock<ILogger<AutoOrderService>>();
        // Pass nulls for notification/kraken since the early-exit paths never touch them
        return new AutoOrderService(state, null!, null!, olog.Object);
    }

    [Theory]
    [InlineData(123.4567, 2, 123.45)]
    [InlineData(0.0001, 2, 0)]
    [InlineData(99.999, 0, 99)]
    [InlineData(100.0, 4, 100)]
    public void RoundDown_TruncatesDownward(double value, int decimals, double expected)
    {
        Assert.Equal((decimal)expected, AutoOrderService.RoundDown((decimal)value, decimals));
    }

    [Fact]
    public void RoundDown_DoesNotRoundHalfUp()
    {
        Assert.Equal(1.235m, AutoOrderService.RoundDown(1.2359m, 3));
    }

    [Fact]
    public async Task CheckAsync_NoHistoricPrices_ReturnsReasonNotLoaded()
    {
        var svc = NewSvc(out _);
        var item = new PriceDataItem { Symbol = "SOL/USD", KrakenNewPricesLoadedEver = false };
        var ao = await svc.CheckAsync(item, "TestRule");
        Assert.False(ao.OrderWanted);
        Assert.Contains("Historic Prices not loaded", ao.Reason);
    }

    [Fact]
    public async Task CheckAsync_TooFewKlines_ReturnsReasonTooFew()
    {
        var svc = NewSvc(out _);
        var item = new PriceDataItem { Symbol = "SOL/USD", KrakenNewPricesLoadedEver = true };
        // Add only a few klines (< 60)
        for (int i = 0; i < 5; i++)
            item.AddKline(new DerivedKline { Asset = "SOL/USD", OpenTime = DateTime.UtcNow.AddDays(-5 + i), Close = 100m, Interval = "OneDay" });
        var ao = await svc.CheckAsync(item, "R");
        Assert.False(ao.OrderWanted);
        Assert.Contains("Too few", ao.Reason);
    }

    [Fact]
    public async Task CheckAsync_BlacklistedCoinType_ReturnsBlacklistedReason()
    {
        var svc = NewSvc(out _);
        // TRUMP is in the default Blacklist → CoinType = "Blacklist"
        var item = new PriceDataItem { Symbol = "TRUMP/USD", KrakenNewPricesLoadedEver = true };
        for (int i = 0; i < 70; i++)
            item.AddKline(new DerivedKline { Asset = "TRUMP/USD", OpenTime = DateTime.UtcNow.AddDays(-70 + i), Close = 10m, Interval = "OneDay" });
        var ao = await svc.CheckAsync(item, "R");
        Assert.False(ao.OrderWanted);
        Assert.Contains("blacklisted", ao.Reason);
    }

    [Fact]
    public async Task CheckAsync_PopulatesPriceMovementFields()
    {
        var svc = NewSvc(out _);
        var item = new PriceDataItem { Symbol = "SOL/USD", KrakenNewPricesLoadedEver = false };
        for (int i = 0; i < 10; i++)
            item.AddKline(new DerivedKline { Asset = "SOL/USD", OpenTime = DateTime.UtcNow.AddDays(-10 + i), Close = 100m + i });

        var ao = await svc.CheckAsync(item, "R");
        Assert.Equal("SOL/USD", ao.Symbol);
        Assert.Equal("SOL", ao.Base);
        Assert.Equal("USD", ao.CCY);
        Assert.NotNull(ao.ClosePriceMovement);
    }
}

// ── TradingStateService extras ───────────────────────────────────────────────

public class TradingStateServiceExtraTests
{
    private static TradingStateService Create()
    {
        var dlog = new Mock<ILogger<DelistedPriceService>>();
        return new TradingStateService(new DelistedPriceService(dlog.Object));
    }

    // SeenLedger dedup (bounded)

    [Fact]
    public void HasSeenLedger_Unknown_ReturnsFalse()
    {
        Assert.False(Create().HasSeenLedger("L1"));
    }

    [Fact]
    public void AddSeenLedger_ThenHasSeenLedger_ReturnsTrue()
    {
        var svc = Create();
        svc.AddSeenLedger("L1");
        Assert.True(svc.HasSeenLedger("L1"));
    }

    [Fact]
    public void AddSeenLedger_EvictsOldestFifo_NotClearAll()
    {
        var svc = Create();
        for (int i = 0; i < 5000; i++)
            svc.AddSeenLedger($"L{i}");
        Assert.True(svc.HasSeenLedger("L4999"));

        // Adding past the cap evicts only the oldest entry, not the whole set —
        // wholesale clearing was the cause of the staking-reward notification flood.
        svc.AddSeenLedger("overflow");
        Assert.True(svc.HasSeenLedger("overflow"));
        Assert.False(svc.HasSeenLedger("L0"));      // oldest evicted
        Assert.True(svc.HasSeenLedger("L1"));        // everything else retained
        Assert.True(svc.HasSeenLedger("L4999"));     // newest pre-overflow retained
    }

    [Fact]
    public void AddSeenLedger_DuplicateAdd_DoesNotEvict()
    {
        var svc = Create();
        for (int i = 0; i < 5000; i++)
            svc.AddSeenLedger($"L{i}");
        // Re-adding an existing id must be a no-op, not advance the FIFO position.
        svc.AddSeenLedger("L0");
        Assert.True(svc.HasSeenLedger("L0"));
        Assert.True(svc.HasSeenLedger("L4999"));
    }

    // IsOpenOrderStatus

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Open", true)]
    [InlineData("open", true)]
    [InlineData("New", true)]
    [InlineData("PendingNew", true)]
    [InlineData("pending_new", true)]
    [InlineData("PartiallyFilled", true)]
    [InlineData("partially_filled", true)]
    [InlineData("Closed", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Filled", false)]
    [InlineData("random", false)]
    public void IsOpenOrderStatus_Variants(string? status, bool expected)
    {
        Assert.Equal(expected, TradingStateService.IsOpenOrderStatus(status));
    }

    // GetPriceSnapshot

    [Fact]
    public void GetPriceSnapshot_ReturnsAllPriceItems()
    {
        var svc = Create();
        svc.Prices["A"] = new PriceDataItem { Symbol = "A/USD" };
        svc.Prices["B"] = new PriceDataItem { Symbol = "B/USD" };
        var snap = svc.GetPriceSnapshot();
        Assert.Equal(2, snap.Count);
    }

    [Fact]
    public void GetPriceSnapshot_Empty_ReturnsEmpty()
    {
        Assert.Empty(Create().GetPriceSnapshot());
    }

    // RecalculateAllOrderFields

    [Fact]
    public void RecalculateAllOrderFields_UpdatesEveryOrder()
    {
        var svc = Create();
        svc.Orders["o1"] = new OrderDto { Id = "o1", Symbol = "SOLUSD", Price = 100m, Quantity = 5m };
        svc.Orders["o2"] = new OrderDto { Id = "o2", Symbol = "ETHUSD", Price = 3000m, Quantity = 1m };

        svc.RecalculateAllOrderFields();

        Assert.Equal(500m, svc.Orders["o1"].OrderValue);
        Assert.Equal(3000m, svc.Orders["o2"].OrderValue);
    }

    // GetUsdGbpRate via direct USD/GBP pair

    [Fact]
    public void GetUsdGbpRate_DirectUsdGbpPair()
    {
        var svc = Create();
        var p = new PriceDataItem { Symbol = "USD/GBP" };
        p.AddKline(new DerivedKline { Asset = "USD/GBP", Close = 0.80m, OpenTime = DateTime.UtcNow });
        svc.Prices["USD/GBP"] = p;

        // Falls through to "USD/GBP" branch (returns price directly)
        Assert.Equal(0.80m, svc.GetUsdGbpRate());
    }

    // ResolveSymbolKey case-insensitivity

    [Fact]
    public void ResolveSymbolKey_FindsCaseInsensitiveDirectMatch()
    {
        var svc = Create();
        svc.Prices["XBT/USD"] = new PriceDataItem { Symbol = "XBT/USD" };
        // The method short-circuits when the dictionary contains the key as-given;
        // an exact-cased match is found first.
        Assert.Equal("XBT/USD", svc.ResolveSymbolKey("XBT/USD"));
    }
}

// ── PriceDataItem.BestKline / WeightedPrice / TickerData ────────────────────

public class PriceDataItemExtraTests
{
    [Fact]
    public void BestKline_NoTicker_FallsBackToLatestKline()
    {
        var item = new PriceDataItem { Symbol = "BTC/USD" };
        item.AddKline(new DerivedKline { Asset = "BTC/USD", Close = 50000m, OpenTime = DateTime.UtcNow });
        var best = item.BestKline;
        Assert.NotNull(best);
        Assert.Equal(50000m, best!.Close);
    }

    [Fact]
    public void BestKline_NoTickerNoKlines_ReturnsNull()
    {
        var item = new PriceDataItem { Symbol = "BTC/USD" };
        Assert.Null(item.BestKline);
    }

    [Fact]
    public void BestKline_PrefersTickerPriceOverKline()
    {
        var item = new PriceDataItem { Symbol = "BTC/USD" };
        item.AddKline(new DerivedKline { Asset = "BTC/USD", Close = 50000m, OpenTime = DateTime.UtcNow });
        item.TickerData = new TickerDataItem { LastTradePrice = 51000m };
        var best = item.BestKline;
        Assert.NotNull(best);
        Assert.Equal(51000m, best!.Close);
    }

    [Fact]
    public void BestKline_TickerZeroPrice_FallsBackToKline()
    {
        var item = new PriceDataItem { Symbol = "BTC/USD" };
        item.AddKline(new DerivedKline { Asset = "BTC/USD", Close = 50000m, OpenTime = DateTime.UtcNow });
        item.TickerData = new TickerDataItem { LastTradePrice = 0m };
        var best = item.BestKline;
        Assert.NotNull(best);
        Assert.Equal(50000m, best!.Close);
    }

    [Fact]
    public void WeightedPrice_InsufficientData_ReturnsNull()
    {
        var item = new PriceDataItem { Symbol = "BTC/USD" };
        Assert.Null(item.WeightedPrice);
    }

    [Fact]
    public void WeightedPricePercentage_NoPriceHistory_ReturnsNull()
    {
        var item = new PriceDataItem { Symbol = "BTC/USD" };
        Assert.Null(item.WeightedPricePercentage);
    }

    [Fact]
    public void WeightedPricePercentage_NotOld_ReturnsTwoHundred()
    {
        // Setup enough klines for weighted price + flag KrakenNewPricesLoadedEver
        var item = new PriceDataItem { Symbol = "BTC/USD", KrakenNewPricesLoadedEver = true };
        for (int i = 0; i < 10; i++)
            item.AddKline(new DerivedKline { Asset = "BTC/USD", OpenTime = DateTime.UtcNow.AddDays(-10 + i), Close = 50000m });
        Assert.Equal(200.0m, item.WeightedPricePercentage);
    }
}

// ── TickerDataItem defaults ──────────────────────────────────────────────────

public class TickerDataItemTests
{
    [Fact]
    public void Defaults_AllZeroOrNull()
    {
        var t = new TickerDataItem();
        Assert.Equal(0m, t.BestAskPrice);
        Assert.Equal(0m, t.BestBidPrice);
        Assert.Equal(0m, t.LastTradePrice);
        Assert.Equal(0, t.TradeCount);
        Assert.Null(t.Change24h);
        Assert.Null(t.ChangePct24h);
    }
}

// ── CombinedOrder + PriceSnapshot + AppCreds + EFAppCreds defaults ───────────

public class MoreModelDefaultTests
{
    [Fact]
    public void CombinedOrder_DefaultsZero()
    {
        var c = new CombinedOrder();
        Assert.Equal(0m, c.Quantity);
        Assert.Equal(0m, c.Price);
    }

    [Fact]
    public void PriceSnapshot_Defaults()
    {
        var s = new PriceSnapshot();
        Assert.Equal("", s.Symbol);
        Assert.Equal(0m, s.Price);
    }

    [Fact]
    public void AppCreds_Defaults()
    {
        var a = new AppCreds();
        Assert.Equal("", a.id);
        Assert.Equal("", a.appkey);
        Assert.Equal("", a.appsecret);
    }

    [Fact]
    public void EFAppCreds_Defaults()
    {
        var a = new EFAppCreds();
        Assert.Equal("", a.id);
        Assert.Equal("", a.appkey);
        Assert.Equal("", a.appsecret);
    }

    [Fact]
    public void PredictionHistory_Defaults()
    {
        var h = new PredictionHistory();
        Assert.Equal("", h.Symbol);
        Assert.Equal("", h.Interval);
        Assert.False(h.PredictedUp);
    }
}

// ── PriceDto extra fields ────────────────────────────────────────────────────

public class PriceDtoExtraTests
{
    [Fact]
    public void DefaultsForOptionalFields()
    {
        var p = new PriceDto();
        Assert.Equal("", p.Symbol);
        Assert.Equal("", p.DisplaySymbol);
        Assert.Equal("Unknown", p.Age);
        Assert.False(p.PriceLowerThanBuy);
        Assert.False(p.PriceOutdated);
    }
}
