using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Kraken.Net.Objects.Models;
using Moq;
using Microsoft.Extensions.Logging;

namespace KrakenReact.Tests;

public class TradingStateServiceTests
{
    private static TradingStateService CreateService()
    {
        var delistedLogger = new Mock<ILogger<DelistedPriceService>>();
        var delisted = new DelistedPriceService(delistedLogger.Object);
        return new TradingStateService(delisted);
    }

    // --- NormalizeOrderSymbolBase ---

    [Theory]
    [InlineData("XBT/USD", "BTC")]
    [InlineData("ETH/USD", "ETH")]
    [InlineData("SOL/GBP", "SOL")]
    public void NormalizeOrderSymbolBase_SlashedSymbol_SplitsAndNormalizes(string input, string expected)
    {
        var svc = CreateService();
        Assert.Equal(expected, svc.NormalizeOrderSymbolBase(input));
    }

    [Fact]
    public void NormalizeOrderSymbolBase_MatchesSymbolsTable()
    {
        var svc = CreateService();
        svc.Symbols["XBTUSD"] = new KrakenSymbol
        {
            WebsocketName = "XBT/USD",
            AlternateName = "XBTUSD",
            BaseAsset = "XXBT",
            QuoteAsset = "ZUSD"
        };

        Assert.Equal("BTC", svc.NormalizeOrderSymbolBase("XBTUSD"));
    }

    [Theory]
    [InlineData("SOLUSD", "SOL")]
    [InlineData("ETHUSDT", "ETH")]
    [InlineData("DOTUSDC", "DOT")]
    [InlineData("BTCEUR", "BTC")]
    [InlineData("ADAGBP", "ADA")]
    [InlineData("XRPZUSD", "XRP")]
    [InlineData("ETHZEUR", "ETH")]
    public void NormalizeOrderSymbolBase_HeuristicStripping(string input, string expected)
    {
        var svc = CreateService();
        Assert.Equal(expected, svc.NormalizeOrderSymbolBase(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeOrderSymbolBase_HandlesNullAndEmpty(string? input, string expected)
    {
        var svc = CreateService();
        Assert.Equal(expected, svc.NormalizeOrderSymbolBase(input!));
    }

    // --- ResolveSymbolKey ---

    [Fact]
    public void ResolveSymbolKey_DirectMatch_ReturnsInput()
    {
        var svc = CreateService();
        svc.Prices.TryAdd("XBT/USD", new PriceDataItem { Symbol = "XBT/USD" });

        Assert.Equal("XBT/USD", svc.ResolveSymbolKey("XBT/USD"));
    }

    [Fact]
    public void ResolveSymbolKey_NormalizedMatch_ReturnsOriginalKey()
    {
        var svc = CreateService();
        svc.Prices.TryAdd("XBT/USD", new PriceDataItem { Symbol = "XBT/USD" });

        // BTC/USD should resolve to XBT/USD since NormalizeAsset("XBT") == NormalizeAsset("BTC") == "BTC"
        Assert.Equal("XBT/USD", svc.ResolveSymbolKey("BTC/USD"));
    }

    [Fact]
    public void ResolveSymbolKey_NoMatch_ReturnsInput()
    {
        var svc = CreateService();
        Assert.Equal("UNKNOWN/USD", svc.ResolveSymbolKey("UNKNOWN/USD"));
    }

    // --- GetApiPairCandidates ---

    [Fact]
    public void GetApiPairCandidates_GeneratesExpectedCombinations()
    {
        var svc = CreateService();
        var candidates = svc.GetApiPairCandidates("XBT/USD");

        // Should include the original
        Assert.Contains("XBT/USD", candidates);
        // Should include normalized form
        Assert.Contains("BTC/USD", candidates);
        // Should include no-slash variants
        Assert.Contains("XBTUSD", candidates);
        Assert.Contains("BTCUSD", candidates);
        // Should include XXBT variant (reverse alias)
        Assert.Contains("XXBT/USD", candidates);
        Assert.Contains("XXBTUSD", candidates);
        // Should include ZUSD variants
        Assert.Contains("XBT/ZUSD", candidates);
        Assert.Contains("XBTZUSD", candidates);
    }

    [Fact]
    public void GetApiPairCandidates_OriginalIsFirst()
    {
        var svc = CreateService();
        var candidates = svc.GetApiPairCandidates("SOL/USD");
        Assert.Equal("SOL/USD", candidates[0]);
    }

    [Fact]
    public void GetApiPairCandidates_NoSlash_ReturnsSingleItem()
    {
        var svc = CreateService();
        var candidates = svc.GetApiPairCandidates("SOLUSD");
        Assert.Single(candidates);
        Assert.Equal("SOLUSD", candidates[0]);
    }

    [Fact]
    public void GetApiPairCandidates_NoDuplicates()
    {
        var svc = CreateService();
        var candidates = svc.GetApiPairCandidates("XBT/USD");
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void GetApiPairCandidates_IncludesAlternateName()
    {
        var svc = CreateService();
        svc.Symbols["XBTUSD"] = new KrakenSymbol
        {
            WebsocketName = "XBT/USD",
            AlternateName = "XBTUSD",
            BaseAsset = "XXBT",
            QuoteAsset = "ZUSD"
        };

        var candidates = svc.GetApiPairCandidates("XBT/USD");
        Assert.Contains("XBTUSD", candidates);
    }

    // --- GetUsdGbpRate ---

    [Fact]
    public void GetUsdGbpRate_WithGbpUsdPair_ReturnsInverse()
    {
        var svc = CreateService();
        var price = new PriceDataItem { Symbol = "GBP/USD" };
        price.AddKline(new DerivedKline { Close = 1.28m, OpenTime = DateTime.UtcNow });
        svc.Prices.TryAdd("GBP/USD", price);

        var rate = svc.GetUsdGbpRate();
        // 1 / 1.28 ≈ 0.78125
        Assert.True(rate > 0.78m && rate < 0.79m);
    }

    [Fact]
    public void GetUsdGbpRate_NoPrices_ReturnsZero()
    {
        var svc = CreateService();
        Assert.Equal(0m, svc.GetUsdGbpRate());
    }

    // --- HasNotified / AddNotified ---

    [Fact]
    public void HasNotified_ReturnsFalse_WhenNotAdded()
    {
        var svc = CreateService();
        Assert.False(svc.HasNotified("order1"));
    }

    [Fact]
    public void AddNotified_ThenHasNotified_ReturnsTrue()
    {
        var svc = CreateService();
        svc.AddNotified("order1");
        Assert.True(svc.HasNotified("order1"));
    }

    [Fact]
    public void AddNotified_ClearsOnOverflow()
    {
        var svc = CreateService();
        // Add 5000 entries to trigger clear
        for (int i = 0; i < 5000; i++)
            svc.AddNotified($"order{i}");

        Assert.True(svc.HasNotified("order4999"));

        // Adding one more should clear and add only the new one
        svc.AddNotified("overflowOrder");
        Assert.True(svc.HasNotified("overflowOrder"));
        Assert.False(svc.HasNotified("order0"));
    }

    // --- RecalculateOrderFields ---

    [Fact]
    public void RecalculateOrderFields_SetsDistanceAndValue()
    {
        var svc = CreateService();
        var price = new PriceDataItem { Symbol = "SOL/USD" };
        price.AddKline(new DerivedKline { Asset = "SOL/USD", Close = 100m, OpenTime = DateTime.UtcNow });
        svc.Prices.TryAdd("SOL/USD", price);

        // RecalculateOrderFields calls LatestPrice(order.Symbol) which needs to resolve
        // "SOL" (from the order symbol) to find the SOL/USD price entry.
        // Add symbol so LatestPrice can match via Symbols table.
        svc.Symbols["SOLUSD"] = new KrakenSymbol
        {
            WebsocketName = "SOL/USD",
            BaseAsset = "SOL",
            QuoteAsset = "ZUSD"
        };

        // Use realistic order symbol "SOLUSD" — RecalculateOrderFields now uses
        // NormalizeOrderSymbolBase to extract "SOL" before looking up price.
        var order = new OrderDto { Symbol = "SOLUSD", Price = 110m, Quantity = 5m };
        svc.RecalculateOrderFields(order);

        Assert.Equal(100m, order.LatestPrice);
        Assert.Equal(10m, order.Distance);
        Assert.Equal(10m, order.DistancePercentage);
        Assert.Equal(550m, order.OrderValue);
    }

    [Fact]
    public void RecalculateOrderFields_AlwaysCalculatesOrderValue()
    {
        var svc = CreateService();
        // Even without matching price, OrderValue should be calculated
        var order = new OrderDto { Symbol = "UNKNOWN", Price = 50m, Quantity = 10m };
        svc.RecalculateOrderFields(order);

        Assert.Equal(500m, order.OrderValue);
        Assert.Equal(0m, order.LatestPrice); // No price found
    }

    // --- RecalculateBalanceCoveredAmounts ---

    [Fact]
    public void RecalculateBalanceCoveredAmounts_CalculatesCoveredAndUncovered()
    {
        var svc = CreateService();

        // Set up a balance
        svc.Balances.TryAdd("SOL", new BalanceDto
        {
            Asset = "SOL",
            Total = 10m,
            Available = 10m,
            LatestPrice = 100m
        });

        // Set up a sell order covering 3 SOL
        svc.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1",
            Symbol = "SOLUSD",
            Side = "Sell",
            Status = "Open",
            Price = 110m,
            Quantity = 3m,
            QuantityFilled = 0m
        });

        svc.RecalculateBalanceCoveredAmounts();

        var bal = svc.Balances["SOL"];
        Assert.Equal(3m, bal.OrderCoveredQty);
        Assert.Equal(7m, bal.OrderUncoveredQty);
        Assert.Equal(330m, bal.OrderCoveredValue); // 3 * 110
    }

    [Fact]
    public void RecalculateBalanceCoveredAmounts_IgnoresBuyOrders()
    {
        var svc = CreateService();
        svc.Balances.TryAdd("ETH", new BalanceDto { Asset = "ETH", Total = 5m, Available = 5m });
        svc.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "ETHUSD", Side = "Buy", Status = "Open",
            Price = 3000m, Quantity = 1m
        });

        svc.RecalculateBalanceCoveredAmounts();

        // Buy orders don't affect covered/uncovered calculations — both stay at 0
        // (uncovered is only set to Total - CoveredQty when there ARE sell orders covering some of the balance)
        Assert.Equal(0m, svc.Balances["ETH"].OrderCoveredQty);
        Assert.Equal(0m, svc.Balances["ETH"].OrderUncoveredQty);
    }

    [Fact]
    public void RecalculateBalanceCoveredAmounts_IgnoresClosedOrders()
    {
        var svc = CreateService();
        svc.Balances.TryAdd("SOL", new BalanceDto { Asset = "SOL", Total = 10m, Available = 10m });
        svc.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Sell", Status = "Closed",
            Price = 100m, Quantity = 5m
        });

        svc.RecalculateBalanceCoveredAmounts();

        Assert.Equal(0m, svc.Balances["SOL"].OrderCoveredQty);
    }

    [Fact]
    public void RecalculateBalanceCoveredAmounts_MultipleOrders_Accumulate()
    {
        var svc = CreateService();
        svc.Balances.TryAdd("SOL", new BalanceDto { Asset = "SOL", Total = 10m, Available = 10m, LatestPrice = 100m });

        svc.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Sell", Status = "Open",
            Price = 110m, Quantity = 3m, QuantityFilled = 0m
        });
        svc.Orders.TryAdd("o2", new OrderDto
        {
            Id = "o2", Symbol = "SOLUSD", Side = "Sell", Status = "Open",
            Price = 120m, Quantity = 4m, QuantityFilled = 0m
        });

        svc.RecalculateBalanceCoveredAmounts();

        var bal = svc.Balances["SOL"];
        Assert.Equal(7m, bal.OrderCoveredQty);
        Assert.Equal(3m, bal.OrderUncoveredQty);
    }

    // --- NormalizeOrderSymbolQuote ---

    [Fact]
    public void NormalizeOrderSymbolQuote_WithSlash_ReturnsQuote()
    {
        var svc = CreateService();
        Assert.Equal("USD", svc.NormalizeOrderSymbolQuote("XBT/USD"));
        Assert.Equal("EUR", svc.NormalizeOrderSymbolQuote("ETH/EUR"));
    }

    [Fact]
    public void NormalizeOrderSymbolQuote_ViaSymbolsTable()
    {
        var svc = CreateService();
        svc.Symbols["XBTUSD"] = new KrakenSymbol
        {
            WebsocketName = "XBT/USD",
            AlternateName = "XBTUSD",
            BaseAsset = "XXBT",
            QuoteAsset = "ZUSD"
        };

        // "ZUSD" normalizes to "USD"
        Assert.Equal("USD", svc.NormalizeOrderSymbolQuote("XBTUSD"));
    }

    [Fact]
    public void NormalizeOrderSymbolQuote_Heuristic_USD()
    {
        var svc = CreateService();
        Assert.Equal("USD", svc.NormalizeOrderSymbolQuote("SOLUSD"));
    }

    [Fact]
    public void NormalizeOrderSymbolQuote_Heuristic_EUR()
    {
        var svc = CreateService();
        Assert.Equal("EUR", svc.NormalizeOrderSymbolQuote("SOLEUR"));
    }

    [Fact]
    public void NormalizeOrderSymbolQuote_EmptyInput_ReturnsEmpty()
    {
        var svc = CreateService();
        Assert.Equal("", svc.NormalizeOrderSymbolQuote(""));
        Assert.Equal("", svc.NormalizeOrderSymbolQuote(null!));
    }

    // --- RecalculateBalanceCoveredAmounts (buy orders reduce currency available) ---

    [Fact]
    public void RecalculateBalanceCoveredAmounts_BuyOrderReducesCurrencyAvailable()
    {
        var svc = CreateService();
        svc.Symbols["SOLUSD"] = new KrakenSymbol
        {
            WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD"
        };

        svc.Balances.TryAdd("USD", new BalanceDto { Asset = "USD", Total = 10000m, Available = 10000m });
        svc.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Buy", Status = "Open",
            Price = 100m, Quantity = 20m, QuantityFilled = 0m
        });

        svc.RecalculateBalanceCoveredAmounts();

        var usd = svc.Balances["USD"];
        // Buy order costs 20 * 100 = 2000 USD
        Assert.Equal(2000m, usd.OrderCoveredQty);
        Assert.Equal(8000m, usd.Available);
    }

    [Fact]
    public void RecalculateBalanceCoveredAmounts_MultipleBuyOrdersReduceCurrency()
    {
        var svc = CreateService();
        svc.Symbols["SOLUSD"] = new KrakenSymbol
        {
            WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD"
        };
        svc.Symbols["ETHUSD"] = new KrakenSymbol
        {
            WebsocketName = "ETH/USD", BaseAsset = "XETH", QuoteAsset = "ZUSD"
        };

        svc.Balances.TryAdd("USD", new BalanceDto { Asset = "USD", Total = 5000m, Available = 5000m });
        svc.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Buy", Status = "Open",
            Price = 100m, Quantity = 10m, QuantityFilled = 0m
        });
        svc.Orders.TryAdd("o2", new OrderDto
        {
            Id = "o2", Symbol = "ETHUSD", Side = "Buy", Status = "Open",
            Price = 3000m, Quantity = 1m, QuantityFilled = 0m
        });

        svc.RecalculateBalanceCoveredAmounts();

        var usd = svc.Balances["USD"];
        // 10*100 + 1*3000 = 4000
        Assert.Equal(4000m, usd.OrderCoveredQty);
        Assert.Equal(1000m, usd.Available);
    }

    [Fact]
    public void RecalculateBalanceCoveredAmounts_PartiallyFilledBuyOrder()
    {
        var svc = CreateService();
        svc.Symbols["SOLUSD"] = new KrakenSymbol
        {
            WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD"
        };

        svc.Balances.TryAdd("USD", new BalanceDto { Asset = "USD", Total = 10000m, Available = 10000m });
        svc.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Buy", Status = "Open",
            Price = 100m, Quantity = 20m, QuantityFilled = 5m
        });

        svc.RecalculateBalanceCoveredAmounts();

        var usd = svc.Balances["USD"];
        // Only remaining 15 * 100 = 1500 should be covered
        Assert.Equal(1500m, usd.OrderCoveredQty);
        Assert.Equal(8500m, usd.Available);
    }

    [Fact]
    public void RecalculateBalanceCoveredAmounts_SellOrderDoesNotAffectCurrency()
    {
        var svc = CreateService();
        svc.Symbols["SOLUSD"] = new KrakenSymbol
        {
            WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD"
        };

        svc.Balances.TryAdd("USD", new BalanceDto { Asset = "USD", Total = 10000m, Available = 10000m });
        svc.Balances.TryAdd("SOL", new BalanceDto { Asset = "SOL", Total = 50m, Available = 50m, LatestPrice = 100m });
        svc.Orders.TryAdd("o1", new OrderDto
        {
            Id = "o1", Symbol = "SOLUSD", Side = "Sell", Status = "Open",
            Price = 110m, Quantity = 10m, QuantityFilled = 0m
        });

        svc.RecalculateBalanceCoveredAmounts();

        // USD should be unaffected by sell orders
        Assert.Equal(10000m, svc.Balances["USD"].Available);
        // SOL should have covered qty
        Assert.Equal(10m, svc.Balances["SOL"].OrderCoveredQty);
    }

    // --- LatestPrice ---

    [Fact]
    public void LatestPrice_USD_ReturnsOne()
    {
        var svc = CreateService();
        var result = svc.LatestPrice("USD");
        Assert.NotNull(result);
        Assert.Equal(1.0m, result!.Close);
    }

    [Fact]
    public void LatestPrice_ZUSD_ReturnsOne()
    {
        var svc = CreateService();
        var result = svc.LatestPrice("ZUSD");
        Assert.NotNull(result);
        Assert.Equal(1.0m, result!.Close);
    }

    [Fact]
    public void LatestPrice_UnknownAsset_ReturnsNull()
    {
        var svc = CreateService();
        Assert.Null(svc.LatestPrice("NONEXISTENT"));
    }

    [Fact]
    public void LatestPrice_FindsByNormalizedBase()
    {
        var svc = CreateService();
        var price = new PriceDataItem { Symbol = "XBT/USD" };
        price.AddKline(new DerivedKline { Asset = "XBT/USD", Close = 50000m, OpenTime = DateTime.UtcNow });
        svc.Prices.TryAdd("XBT/USD", price);
        svc.Symbols["XBTUSD"] = new KrakenSymbol
        {
            WebsocketName = "XBT/USD",
            BaseAsset = "XXBT",
            QuoteAsset = "ZUSD"
        };

        // Looking up "BTC" should find XBT/USD since XXBT normalizes to BTC
        var result = svc.LatestPrice("BTC");
        Assert.NotNull(result);
        Assert.Equal(50000m, result!.Close);
    }

    // --- GetOrAddPrice ---

    [Fact]
    public void GetOrAddPrice_CreatesNewEntry()
    {
        var svc = CreateService();
        var price = svc.GetOrAddPrice("NEW/USD");
        Assert.NotNull(price);
        Assert.Equal("NEW/USD", price.Symbol);
        Assert.True(svc.Prices.ContainsKey("NEW/USD"));
    }

    [Fact]
    public void GetOrAddPrice_ReturnsSameInstance()
    {
        var svc = CreateService();
        var first = svc.GetOrAddPrice("SOL/USD");
        var second = svc.GetOrAddPrice("SOL/USD");
        Assert.Same(first, second);
    }
}
