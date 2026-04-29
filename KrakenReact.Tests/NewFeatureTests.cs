using KrakenReact.Server.Controllers;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class DcaModelTests
{
    [Fact]
    public void DcaRule_DefaultValues()
    {
        var rule = new DcaRule();
        Assert.Equal("", rule.Symbol);
        Assert.Equal(0m, rule.AmountUsd);
        Assert.Equal("0 9 * * 1", rule.CronExpression);
        Assert.True(rule.Active);
        Assert.Null(rule.LastRunAt);
        Assert.Equal("", rule.LastRunResult);
    }

    [Fact]
    public void OrderLadderRequest_DefaultValues()
    {
        var req = new OrderLadderRequest();
        Assert.Equal("", req.Symbol);
        Assert.Equal("Buy", req.Side);
        Assert.Equal(0m, req.TotalQty);
        Assert.Equal(0m, req.StartPrice);
        Assert.Equal(0m, req.EndPrice);
        Assert.Equal(5, req.Count);
    }
}

public class CorrelationControllerTests
{
    private static CorrelationController Create() =>
        new CorrelationController(MakeState());

    private static TradingStateService MakeState()
    {
        var log = new Mock<ILogger<DelistedPriceService>>();
        return new TradingStateService(new DelistedPriceService(log.Object));
    }

    [Fact]
    public void GetMatrix_NullSymbols_EmptyBalances_ReturnsBadRequest()
    {
        var ctrl = Create();
        var result = ctrl.GetMatrix(null, 30);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetMatrix_OneSymbol_ReturnsBadRequest()
    {
        var ctrl = Create();
        var result = ctrl.GetMatrix("XBT/USD", 30);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetMatrix_TwoSymbols_NoData_ReturnsOk()
    {
        // Two valid symbols but no kline data → returns Ok with empty/small matrix
        var ctrl = Create();
        var result = ctrl.GetMatrix("XBT/USD,ETH/USD", 30);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void GetMatrix_TwentyOneSymbols_ReturnsBadRequest()
    {
        var ctrl = Create();
        var symbols = string.Join(",", Enumerable.Range(1, 21).Select(i => $"A{i}/USD"));
        var result = ctrl.GetMatrix(symbols, 30);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetMatrix_DaysClampedToRange()
    {
        var ctrl = Create();
        // < 7 should be clamped to 7 (no crash)
        var result = ctrl.GetMatrix("XBT/USD,ETH/USD", 1);
        Assert.IsType<OkObjectResult>(result);
    }
}

public class PortfolioMetricsTests
{
    // Minimal controller test: just check it compiles and is wired up.
    // Real persistence tests would need an in-memory DB which is out of scope.

    [Fact]
    public void PortfolioSnapshot_HasExpectedProperties()
    {
        var s = new PortfolioSnapshot { Date = DateTime.Today, TotalUsd = 10000m, TotalGbp = 8000m };
        Assert.Equal(10000m, s.TotalUsd);
        Assert.Equal(8000m, s.TotalGbp);
        Assert.Equal(DateTime.Today, s.Date);
    }
}

public class FeatureEngineeringRegimeTests
{
    // Test the ADX + BB-width data used for market regime
    [Fact]
    public void AdxReturnsNonNegativeValues_WhenGivenOscillatingPrices()
    {
        var rng = new Random(42);
        int N = 60;
        var closes = new float[N];
        var highs = new float[N];
        var lows = new float[N];
        float p = 100f;
        for (int i = 0; i < N; i++)
        {
            var delta = (float)(rng.NextDouble() * 4 - 2);
            p = Math.Max(10, p + delta);
            closes[i] = p;
            highs[i] = p + 1f;
            lows[i] = p - 1f;
        }

        var adx = FeatureEngineering.ComputeAdx(highs, lows, closes);
        Assert.True(adx.Length > 0);
        Assert.All(adx, v => Assert.True(v >= 0));
    }

    [Fact]
    public void BollingerBands_WidthIsNonNegative()
    {
        var closes = Enumerable.Range(1, 60).Select(i => (float)(100 + Math.Sin(i * 0.3) * 5)).ToArray();
        var (upper, lower, mid) = FeatureEngineering.ComputeBollingerBands(closes);
        Assert.True(upper.Length > 0);
        for (int i = 0; i < upper.Length; i++)
            Assert.True(upper[i] >= lower[i]);
    }
}
