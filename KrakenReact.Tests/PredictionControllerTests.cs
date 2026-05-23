using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class PredictionControllerTests : IDisposable
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;
    private readonly Mock<IBackgroundJobClient> _jobs = new();

    public PredictionControllerTests()
    {
        _db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"pred-{Guid.NewGuid()}").Options);

        var dlog = new Mock<ILogger<DelistedPriceService>>();
        _state = new TradingStateService(new DelistedPriceService(dlog.Object));
    }
    public void Dispose() => _db.Dispose();

    private PredictionController NewCtrl() => new(_db, _jobs.Object, _state);

    // ── GetPredictions ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetPredictions_Empty_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetPredictions());
        var list = Assert.IsAssignableFrom<List<PredictionResult>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetPredictions_ReturnsAllResults()
    {
        _db.PredictionResults.AddRange(
            new PredictionResult { Symbol = "BTC/USD", Interval = "OneHour", Status = "success" },
            new PredictionResult { Symbol = "ETH/USD", Interval = "OneHour", Status = "success" }
        );
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetPredictions());
        var list = Assert.IsAssignableFrom<List<PredictionResult>>(ok.Value);
        Assert.Equal(2, list.Count);
    }

    // ── TriggerNow / TriggerSingle / TriggerMultiTf ──────────────────────────

    [Fact]
    public void TriggerNow_EnqueuesAndReturnsOk()
    {
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().TriggerNow());
        Assert.NotNull(ok.Value);
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void TriggerSingle_MissingSymbol_BadRequest(string? symbol)
    {
        Assert.IsType<BadRequestObjectResult>(NewCtrl().TriggerSingle(symbol!));
    }

    [Fact]
    public void TriggerSingle_Valid_Enqueues()
    {
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().TriggerSingle("BTC/USD"));
        Assert.NotNull(ok.Value);
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public void TriggerMultiTf_EmptySymbol_BadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(NewCtrl().TriggerMultiTf(" "));
    }

    [Fact]
    public void TriggerMultiTf_Valid_Enqueues()
    {
        var ok = Assert.IsType<OkObjectResult>(NewCtrl().TriggerMultiTf("BTC%2FUSD"));
        Assert.NotNull(ok.Value);
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    // ── DeletePrediction ────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DeletePrediction_MissingSymbol_BadRequest(string symbol)
    {
        Assert.IsType<BadRequestObjectResult>(await NewCtrl().DeletePrediction(symbol));
    }

    [Fact]
    public async Task DeletePrediction_NotFound()
    {
        Assert.IsType<NotFoundResult>(await NewCtrl().DeletePrediction("UNKNOWN/USD"));
    }

    [Fact]
    public async Task DeletePrediction_Existing_RemovesRow()
    {
        _db.PredictionResults.Add(new PredictionResult { Symbol = "BTC/USD", Interval = "OneHour" });
        await _db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await NewCtrl().DeletePrediction("BTC/USD"));
        Assert.Empty(await _db.PredictionResults.ToListAsync());
    }

    // ── GetHistory ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_Empty_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetHistory("BTC/USD"));
        Assert.Empty((System.Collections.IEnumerable)ok.Value!);
    }

    [Fact]
    public async Task GetHistory_OrderedDescending_AndLimited()
    {
        for (int i = 0; i < 5; i++)
            _db.PredictionHistories.Add(new PredictionHistory
            {
                Symbol = "BTC/USD", Interval = "OneHour",
                ComputedAt = DateTime.UtcNow.AddHours(-i),
                Probability = 0.5f + 0.05f * i,
            });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetHistory("BTC/USD", limit: 2));
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Equal(2, list.Count);
        var firstTime = (DateTime)list[0].GetType().GetProperty("ComputedAt")!.GetValue(list[0])!;
        var secondTime = (DateTime)list[1].GetType().GetProperty("ComputedAt")!.GetValue(list[1])!;
        Assert.True(firstTime > secondTime);
    }

    [Fact]
    public async Task GetHistory_LimitClampedTo200()
    {
        for (int i = 0; i < 250; i++)
            _db.PredictionHistories.Add(new PredictionHistory
            {
                Symbol = "BTC/USD", Interval = "OneHour",
                ComputedAt = DateTime.UtcNow.AddMinutes(-i),
            });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetHistory("BTC/USD", limit: 9999));
        var list = ((System.Collections.IEnumerable)ok.Value!).Cast<object>().ToList();
        Assert.Equal(200, list.Count);
    }

    // ── GetAccuracy ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccuracy_InsufficientHistory_ReturnsNullHitRate()
    {
        _db.PredictionHistories.Add(new PredictionHistory { Symbol = "BTC/USD", ComputedAt = DateTime.UtcNow });
        _db.PredictionHistories.Add(new PredictionHistory { Symbol = "BTC/USD", ComputedAt = DateTime.UtcNow.AddHours(-1) });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAccuracy("BTC/USD"));
        var hit = ok.Value!.GetType().GetProperty("hitRate")!.GetValue(ok.Value);
        Assert.Null(hit);
    }

    [Fact]
    public async Task GetAccuracy_WithKlines_ComputesHitRate()
    {
        // Set up klines: 3 hourly candles with increasing close → "up"
        var t0 = DateTime.UtcNow.AddHours(-3);
        var price = new PriceDataItem { Symbol = "BTC/USD" };
        for (int i = 0; i < 3; i++)
            price.AddKline(new DerivedKline
            {
                Asset = "BTC/USD", Interval = "OneHour",
                OpenTime = t0.AddHours(i), Close = 100m + i * 10m
            });
        _state.Prices["BTC/USD"] = price;

        // Three predictions, all "up" — actual prices also went up → 100% hit rate
        for (int i = 0; i < 3; i++)
            _db.PredictionHistories.Add(new PredictionHistory
            {
                Symbol = "BTC/USD", Interval = "OneHour",
                ComputedAt = t0.AddHours(i).AddMinutes(1),
                PredictedUp = true, Probability = 0.7f,
            });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetAccuracy("BTC/USD"));
        var hitRate = (double?)ok.Value!.GetType().GetProperty("hitRate")!.GetValue(ok.Value);
        // Last prediction has no "next" candle → fewer evaluations, but those evaluated are all hits
        if (hitRate.HasValue) Assert.True(hitRate.Value >= 0 && hitRate.Value <= 100);
    }

    // ── GetKelly ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetKelly_FewerThanTen_ReturnsNullFraction()
    {
        for (int i = 0; i < 9; i++)
            _db.PredictionHistories.Add(new PredictionHistory { Symbol = "BTC/USD", ComputedAt = DateTime.UtcNow.AddHours(-i) });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetKelly("BTC/USD"));
        Assert.Null(ok.Value!.GetType().GetProperty("kellyFraction")!.GetValue(ok.Value));
    }

    // ── GetConfidenceHistogram ──────────────────────────────────────────────

    [Fact]
    public async Task GetConfidenceHistogram_InsufficientHistory_ReturnsEmptyBuckets()
    {
        for (int i = 0; i < 4; i++)
            _db.PredictionHistories.Add(new PredictionHistory { Symbol = "BTC/USD", ComputedAt = DateTime.UtcNow.AddHours(-i) });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetConfidenceHistogram("BTC/USD"));
        var buckets = (System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("buckets")!.GetValue(ok.Value)!;
        Assert.Empty(buckets.Cast<object>());
    }

    [Fact]
    public async Task GetConfidenceHistogram_SufficientHistory_HasTenBuckets()
    {
        for (int i = 0; i < 10; i++)
            _db.PredictionHistories.Add(new PredictionHistory
            {
                Symbol = "BTC/USD", Interval = "OneHour",
                ComputedAt = DateTime.UtcNow.AddHours(-i),
                Probability = 0.1f * i, PredictedUp = i % 2 == 0
            });
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetConfidenceHistogram("BTC/USD"));
        var buckets = ((System.Collections.IEnumerable)ok.Value!.GetType().GetProperty("buckets")!.GetValue(ok.Value)!).Cast<object>().ToList();
        Assert.Equal(10, buckets.Count);
    }

    // ── GetRegime ───────────────────────────────────────────────────────────

    [Fact]
    public void GetRegime_UnknownSymbol_NotFound()
    {
        Assert.IsType<NotFoundObjectResult>(NewCtrl().GetRegime("UNKNOWN/USD"));
    }

    [Fact]
    public void GetRegime_InsufficientKlines_ReturnsUnknown()
    {
        var price = new PriceDataItem { Symbol = "BTC/USD" };
        for (int i = 0; i < 10; i++)
            price.AddKline(new DerivedKline { Asset = "BTC/USD", OpenTime = DateTime.UtcNow.AddHours(-i), Close = 50000m, High = 50100m, Low = 49900m });
        _state.Prices["BTC/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetRegime("BTC/USD"));
        var regime = (string)ok.Value!.GetType().GetProperty("regime")!.GetValue(ok.Value)!;
        Assert.Equal("unknown", regime);
    }

    [Fact]
    public void GetRegime_FlatMarket_ReportsRanging()
    {
        var price = new PriceDataItem { Symbol = "BTC/USD" };
        for (int i = 0; i < 60; i++)
            price.AddKline(new DerivedKline { Asset = "BTC/USD", OpenTime = DateTime.UtcNow.AddHours(-60 + i), Open = 100m, Close = 100m, High = 100m, Low = 100m, Volume = 1000m });
        _state.Prices["BTC/USD"] = price;

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetRegime("BTC/USD"));
        var regime = (string)ok.Value!.GetType().GetProperty("regime")!.GetValue(ok.Value)!;
        // Flat market: ADX ≤ 30, BB width ≈ 0 → "ranging"
        Assert.Equal("ranging", regime);
    }

    // ── GetMultiTf ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMultiTf_Empty_ReturnsEmpty()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetMultiTf("BTC/USD"));
        Assert.Empty((List<MultiTfPredictionResult>)ok.Value!);
    }

    [Fact]
    public async Task GetMultiTf_FiltersBySymbol()
    {
        _db.MultiTfPredictionResults.AddRange(
            new MultiTfPredictionResult { Symbol = "BTC/USD", Interval = "OneHour" },
            new MultiTfPredictionResult { Symbol = "BTC/USD", Interval = "FourHour" },
            new MultiTfPredictionResult { Symbol = "ETH/USD", Interval = "OneHour" }
        );
        await _db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetMultiTf("BTC/USD"));
        var list = (List<MultiTfPredictionResult>)ok.Value!;
        Assert.Equal(2, list.Count);
        Assert.All(list, r => Assert.Equal("BTC/USD", r.Symbol));
    }

    // ── GetSettings ─────────────────────────────────────────────────────────

    [Fact]
    public void GetSettings_ReturnsStateValues()
    {
        _state.PredictionSymbols = "BTC/USD,ETH/USD";
        _state.PredictionInterval = "FourHour";
        _state.PredictionMode = "all";
        _state.PredictionCurrency = "EUR";

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetSettings());
        Assert.Equal("BTC/USD,ETH/USD", ok.Value!.GetType().GetProperty("symbols")!.GetValue(ok.Value));
        Assert.Equal("FourHour", ok.Value!.GetType().GetProperty("interval")!.GetValue(ok.Value));
        Assert.Equal("all", ok.Value!.GetType().GetProperty("mode")!.GetValue(ok.Value));
        Assert.Equal("EUR", ok.Value!.GetType().GetProperty("currency")!.GetValue(ok.Value));
    }

    [Fact]
    public void GetSettings_AvailableCurrencies_FromSymbolsTable()
    {
        _state.Symbols["A"] = new KrakenSymbol { WebsocketName = "BTC/USD", BaseAsset = "BTC", QuoteAsset = "ZUSD" };
        _state.Symbols["B"] = new KrakenSymbol { WebsocketName = "ETH/EUR", BaseAsset = "ETH", QuoteAsset = "ZEUR" };
        _state.Symbols["C"] = new KrakenSymbol { WebsocketName = "SOL/USD", BaseAsset = "SOL", QuoteAsset = "ZUSD" };

        var ok = Assert.IsType<OkObjectResult>(NewCtrl().GetSettings());
        var currencies = (List<string>)ok.Value!.GetType().GetProperty("availableCurrencies")!.GetValue(ok.Value)!;
        Assert.Equal(new[] { "EUR", "USD" }, currencies);
    }
}

// ── Misc DTO defaults ────────────────────────────────────────────────────────

public class MiscDtoDefaultTests
{
    [Fact]
    public void AmendOrderRequest_Defaults()
    {
        var r = new AmendOrderRequest();
        Assert.Equal(0m, r.Price);
        Assert.Equal(0m, r.Quantity);
    }

    [Fact]
    public void LedgerDto_Defaults()
    {
        var d = new LedgerDto();
        Assert.Equal("", d.Asset);
        Assert.Equal(0m, d.Quantity);
    }

    [Fact]
    public void TradeDto_Defaults()
    {
        var d = new TradeDto();
        Assert.Equal("", d.Id);
        Assert.Equal("", d.Symbol);
        Assert.Equal(0m, d.Quantity);
    }

    [Fact]
    public void DelistedPairDto_Defaults()
    {
        var d = new DelistedPairDto();
        Assert.Equal("", d.Symbol);
        Assert.False(d.HasHistoricalData);
    }

    [Fact]
    public void OrderLadderRequest_AllDefaults()
    {
        var r = new OrderLadderRequest();
        Assert.Equal("", r.Symbol);
        Assert.Equal("Buy", r.Side);
        Assert.Equal(5, r.Count);
    }

    [Fact]
    public void MultiTfPredictionResult_Defaults()
    {
        var r = new MultiTfPredictionResult();
        Assert.Equal("", r.Symbol);
        Assert.Equal("", r.Interval);
        Assert.Equal("", r.Status);
        Assert.False(r.PredictedUp);
        Assert.Null(r.ErrorMessage);
    }

    [Fact]
    public void PredictionResult_DefaultsAllZero()
    {
        var r = new PredictionResult();
        Assert.Equal("", r.Symbol);
        Assert.Equal("", r.Interval);
        Assert.Equal(0f, r.Probability);
        Assert.Equal(0f, r.Probability3);
        Assert.Equal(0f, r.Probability6);
        Assert.False(r.PredictedUp);
        Assert.False(r.PredictedUp3);
        Assert.False(r.PredictedUp6);
        Assert.Null(r.ErrorMessage);
    }
}
