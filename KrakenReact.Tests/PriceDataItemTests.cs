using KrakenReact.Server.Models;
using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class PriceDataItemTests
{
    private static DerivedKline MakeKline(DateTime time, decimal close, decimal open = 0, decimal volume = 0)
    {
        return new DerivedKline
        {
            Asset = "TEST/USD",
            OpenTime = time,
            Open = open == 0 ? close : open,
            High = close + 1,
            Low = close - 1,
            Close = close,
            Volume = volume
        };
    }

    // --- Symbol parsing ---

    [Fact]
    public void Base_And_CCY_ParsedFromSymbol()
    {
        var item = new PriceDataItem { Symbol = "XBT/USD" };
        Assert.Equal("XBT", item.Base);
        Assert.Equal("USD", item.CCY);
    }

    [Fact]
    public void SymbolNoSlash_NormalizesAndConcatenates()
    {
        var item = new PriceDataItem { Symbol = "XBT/USD" };
        Assert.Equal("BTCUSD", item.SymbolNoSlash);
    }

    // --- CoinType ---

    [Theory]
    [InlineData("BTC/USD", "Main Coin")]
    [InlineData("XBT/USD", "Main Coin")]  // XBT normalizes to BTC
    [InlineData("ETH/USD", "Main Coin")]
    [InlineData("SOL/USD", "Main Coin")]
    [InlineData("XRP/USD", "Main Coin")]
    [InlineData("GBP/USD", "Currency")]
    [InlineData("EUR/USD", "Currency")]
    [InlineData("USDT/USD", "Currency")]
    [InlineData("ADA/USD", "Minor Coin")]
    [InlineData("DOT/USD", "Minor Coin")]
    public void CoinType_CategorizesCorrectly(string symbol, string expectedType)
    {
        var item = new PriceDataItem { Symbol = symbol };
        Assert.Equal(expectedType, item.CoinType);
    }

    [Fact]
    public void CoinType_Blacklisted()
    {
        var item = new PriceDataItem { Symbol = "TRUMP/USD" };
        Assert.Equal("Blacklist", item.CoinType);
    }

    // --- Kline management ---

    [Fact]
    public void AddKline_StoresAndRetrievesKlines()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        var k = MakeKline(DateTime.UtcNow, 100m);
        item.AddKline(k);

        var snapshot = item.GetKlineSnapshot();
        Assert.Single(snapshot);
        Assert.Equal(100m, snapshot[0].Close);
    }

    [Fact]
    public void AddKline_NullIsIgnored()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.AddKline(null!);
        Assert.Empty(item.GetKlineSnapshot());
    }

    [Fact]
    public void AddKline_CapsAt10000()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        for (int i = 0; i < 10050; i++)
            item.AddKline(MakeKline(DateTime.UtcNow.AddMinutes(i), 100m + i));

        Assert.Equal(10000, item.GetKlineSnapshot().Count);
    }

    [Fact]
    public void AddKlineHistory_InsertsAtFront()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.AddKline(MakeKline(DateTime.UtcNow, 200m));

        var history = new List<DerivedKline>
        {
            MakeKline(DateTime.UtcNow.AddDays(-2), 100m),
            MakeKline(DateTime.UtcNow.AddDays(-1), 150m)
        };
        item.AddKlineHistory(history);

        var snapshot = item.GetKlineSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(100m, snapshot[0].Close);
        Assert.Equal(200m, snapshot[2].Close);
    }

    [Fact]
    public void LatestKline_ReturnsLast()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.AddKline(MakeKline(DateTime.UtcNow.AddHours(-1), 100m));
        item.AddKline(MakeKline(DateTime.UtcNow, 150m));

        Assert.Equal(150m, item.LatestKline!.Close);
    }

    [Fact]
    public void LatestKline_EmptyReturnsNull()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        Assert.Null(item.LatestKline);
    }

    [Fact]
    public void MinKline_ReturnsFirst_WithValidDate()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.AddKline(MakeKline(new DateTime(1960, 1, 1), 10m)); // Too old
        item.AddKline(MakeKline(DateTime.UtcNow.AddDays(-30), 100m));
        item.AddKline(MakeKline(DateTime.UtcNow, 150m));

        Assert.Equal(100m, item.MinKline!.Close);
    }

    // --- GetKlineSnapshot thread-safety ---

    [Fact]
    public void GetKlineSnapshot_ReturnsCopy()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.AddKline(MakeKline(DateTime.UtcNow, 100m));

        var snapshot = item.GetKlineSnapshot();
        snapshot.Clear();

        Assert.Single(item.GetKlineSnapshot());
    }

    // --- Age ---

    [Theory]
    [InlineData(0, "New")]
    [InlineData(3, "FewDays")]
    [InlineData(8, "OneWeek")]
    [InlineData(15, "TwoWeeks")]
    [InlineData(35, "OneMonth")]
    [InlineData(100, "ThreeMonths")]
    [InlineData(200, "SixMonths")]
    [InlineData(400, "Old")]
    public void Age_CategorizesCorrectly(int daysOld, string expectedAge)
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.AddKline(MakeKline(DateTime.UtcNow.AddDays(-daysOld), 50m));
        item.AddKline(MakeKline(DateTime.UtcNow, 100m));
        Assert.Equal(expectedAge, item.Age);
    }

    [Fact]
    public void Age_Unknown_WhenNoKlines()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        Assert.Equal("Unknown", item.Age);
    }

    // --- ClosePriceDiff ---

    [Fact]
    public void ClosePriceDiff_ReturnsNull_WhenLessThanTwoKlines()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        Assert.Null(item.ClosePriceDiff(1));

        item.AddKline(MakeKline(DateTime.UtcNow, 100m));
        Assert.Null(item.ClosePriceDiff(1));
    }

    [Fact]
    public void ClosePriceDiff_CalculatesCorrectly()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.AddKline(MakeKline(DateTime.UtcNow.AddHours(-12), 90m));
        item.AddKline(MakeKline(DateTime.UtcNow, 100m));

        var diff = item.ClosePriceDiff(1);
        Assert.NotNull(diff);
        Assert.Equal(10m, diff!.Value);
    }

    // --- CloseMovementDiff ---

    [Fact]
    public void CloseMovementDiff_ReturnsZero_WhenInsufficient()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        Assert.Equal(0m, item.CloseMovementDiff(1));
    }

    [Fact]
    public void CloseMovementDiff_CalculatesPercentage()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.AddKline(MakeKline(DateTime.UtcNow.AddHours(-12), 100m));
        item.AddKline(MakeKline(DateTime.UtcNow, 110m));

        // diff = 10, close = 110, movement = 10 / (110/100) = 10/1.1 ≈ 9.090909
        var movement = item.CloseMovementDiff(1);
        Assert.True(movement > 9m && movement < 10m);
    }

    // --- ClosePriceAverage ---

    [Fact]
    public void ClosePriceAverage_ReturnsNull_WhenInsufficientData()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        Assert.Null(item.ClosePriceAverage(1));
    }

    [Fact]
    public void ClosePriceAverage_CalculatesCorrectly()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        for (int i = 0; i < 10; i++)
            item.AddKline(MakeKline(DateTime.UtcNow.AddHours(-10 + i), 100m + i));

        var avg = item.ClosePriceAverage(1);
        Assert.NotNull(avg);
        // Average of the last ~24h of klines
        Assert.True(avg > 100m);
    }

    // --- PriceOutdated ---

    [Fact]
    public void PriceOutdated_True_WhenNeverLoaded()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        Assert.True(item.PriceOutdated);
    }

    [Fact]
    public void PriceOutdated_False_WhenRecentlyLoaded()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.KrakenNewPricesLoaded = "loaded";
        item.KrakenNewPricesLoadedTime = DateTime.UtcNow;
        Assert.False(item.PriceOutdated);
    }

    [Fact]
    public void PriceOutdated_False_WhenSupportedAndEverLoaded()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        item.SupportedPair = true;
        item.KrakenNewPricesLoadedEver = true;
        Assert.False(item.PriceOutdated);
    }

    // --- WeightedPrice ---

    [Fact]
    public void WeightedPrice_Null_WhenNoData()
    {
        var item = new PriceDataItem { Symbol = "SOL/USD" };
        Assert.Null(item.WeightedPrice);
    }
}
