using KrakenReact.Server.Models;
using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class FeatureEngineeringTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static float[] Flat(float price, int count) =>
        Enumerable.Repeat(price, count).ToArray();

    private static float[] Linear(float start, float step, int count) =>
        Enumerable.Range(0, count).Select(i => start + i * step).ToArray();

    private static float[] Alternating(float @base, float delta, int count) =>
        Enumerable.Range(0, count).Select(i => i % 2 == 0 ? @base : @base + delta).ToArray();

    private static List<DerivedKline> MakeKlines(int count, bool withVariance = false)
    {
        var rng = new Random(42);
        var list = new List<DerivedKline>(count);
        decimal close = 100m;
        for (int i = 0; i < count; i++)
        {
            if (withVariance) close += (decimal)(rng.NextDouble() * 4 - 2);
            else close = 100m + i;
            list.Add(new DerivedKline
            {
                Asset = "TEST/USD",
                OpenTime = DateTime.UtcNow.AddHours(-count + i),
                Open = close - 1m,
                High = close + 2m,
                Low = close - 2m,
                Close = close,
                Volume = 1000m + i,
                VolumeWeightedAveragePrice = close,
            });
        }
        return list;
    }

    // ── ComputeSma ─────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeSma_BasicSliding()
    {
        // [1,2,3,4,5] with period=3: index 2=(1+2+3)/3=2, index 3=(2+3+4)/3=3, index 4=(3+4+5)/3=4
        var sma = FeatureEngineering.ComputeSma([1f, 2f, 3f, 4f, 5f], 3);
        Assert.Equal(0f, sma[0]);
        Assert.Equal(0f, sma[1]);
        Assert.Equal(2f, sma[2], 4);
        Assert.Equal(3f, sma[3], 4);
        Assert.Equal(4f, sma[4], 4);
    }

    [Fact]
    public void ComputeSma_FlatInput_ConstantOutput()
    {
        var sma = FeatureEngineering.ComputeSma(Flat(50f, 30), 5);
        for (int i = 4; i < 30; i++)
            Assert.Equal(50f, sma[i], 3);
    }

    [Fact]
    public void ComputeSma_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(FeatureEngineering.ComputeSma([], 5));
    }

    [Fact]
    public void ComputeSma_PeriodLargerThanArray_AllZero()
    {
        var sma = FeatureEngineering.ComputeSma([1f, 2f, 3f], 10);
        Assert.All(sma, v => Assert.Equal(0f, v));
    }

    // ── ComputeEma ─────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeEma_FlatInput_EqualsSeedPrice()
    {
        var ema = FeatureEngineering.ComputeEma(Flat(50f, 20), 5);
        for (int i = 4; i < 20; i++)
            Assert.Equal(50f, ema[i], 2);
    }

    [Fact]
    public void ComputeEma_Rising_LagsClose()
    {
        var closes = Linear(100f, 1f, 30);
        var ema = FeatureEngineering.ComputeEma(closes, 5);
        // EMA lags price in a rising series
        for (int i = 10; i < 30; i++)
            Assert.True(ema[i] < closes[i], $"EMA should lag close at index {i}");
    }

    [Fact]
    public void ComputeEma_TooShort_ReturnsZeros()
    {
        var ema = FeatureEngineering.ComputeEma([1f, 2f], 5);
        Assert.All(ema, v => Assert.Equal(0f, v));
    }

    // ── ComputeRsi ─────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeRsi_AllGains_Returns100()
    {
        var rsi = FeatureEngineering.ComputeRsi(Linear(100f, 1f, 30), 14);
        for (int i = 14; i < 30; i++)
            Assert.Equal(100f, rsi[i], 1);
    }

    [Fact]
    public void ComputeRsi_AllLosses_ReturnsZero()
    {
        var rsi = FeatureEngineering.ComputeRsi(Linear(200f, -1f, 30), 14);
        Assert.Equal(0f, rsi[14], 2);
    }

    [Fact]
    public void ComputeRsi_Values_BoundedZeroToHundred()
    {
        var closes = Alternating(100f, 5f, 50);
        var rsi = FeatureEngineering.ComputeRsi(closes, 14);
        for (int i = 14; i < 50; i++)
            Assert.True(rsi[i] >= 0f && rsi[i] <= 100f, $"RSI out of [0,100] at {i}: {rsi[i]}");
    }

    [Fact]
    public void ComputeRsi_TooShort_AllZero()
    {
        var rsi = FeatureEngineering.ComputeRsi([1f, 2f], 14);
        Assert.All(rsi, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ComputeRsi_FlatInput_HandlesZeroDivide()
    {
        // Flat prices → avgLoss=0 → RSI=100 (no exception)
        var rsi = FeatureEngineering.ComputeRsi(Flat(100f, 30), 14);
        Assert.All(rsi.Skip(14), v => Assert.Equal(100f, v, 1));
    }

    // ── ComputeAtr ─────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeAtr_FlatHLC_ReturnsZero()
    {
        // H=L=C=100 everywhere → TR=0 throughout
        var p = Flat(100f, 30);
        var atr = FeatureEngineering.ComputeAtr(p, p, p, 14);
        for (int i = 14; i < 30; i++)
            Assert.Equal(0f, atr[i], 4);
    }

    [Fact]
    public void ComputeAtr_WithRange_Positive()
    {
        var closes = Flat(100f, 30);
        var highs = Flat(110f, 30);
        var lows = Flat(90f, 30);
        var atr = FeatureEngineering.ComputeAtr(highs, lows, closes, 14);
        for (int i = 14; i < 30; i++)
            Assert.True(atr[i] > 0f, $"ATR should be positive at {i}");
    }

    [Fact]
    public void ComputeAtr_TrueRange_UsesPreviousClose()
    {
        // Single gap day: C[0]=100, then H=95, L=90, C=92 → TR includes gap from prior close
        var closes = new float[] { 100f, 92f };
        var highs = new float[] { 100f, 95f };
        var lows = new float[] { 100f, 90f };
        var tr = FeatureEngineering.ComputeAtr(highs, lows, closes, 1);
        // TR[1] = max(95-90, |95-100|, |90-100|) = max(5, 5, 10) = 10
        Assert.Equal(10f, tr[1], 2);
    }

    // ── ComputeBollingerBands ──────────────────────────────────────────────────

    [Fact]
    public void ComputeBollingerBands_FlatInput_BandsCollapse()
    {
        var closes = Flat(100f, 30);
        var (up, mid, dn) = FeatureEngineering.ComputeBollingerBands(closes, 20);
        for (int i = 19; i < 30; i++)
        {
            Assert.Equal(100f, mid[i], 3);
            Assert.Equal(up[i], dn[i], 3);
        }
    }

    [Fact]
    public void ComputeBollingerBands_UpperAboveLower_WithVariance()
    {
        var closes = Alternating(100f, 10f, 40);
        var (up, mid, dn) = FeatureEngineering.ComputeBollingerBands(closes, 20);
        for (int i = 19; i < 40; i++)
        {
            Assert.True(up[i] >= mid[i], "Upper must be >= middle");
            Assert.True(dn[i] <= mid[i], "Lower must be <= middle");
        }
    }

    [Fact]
    public void ComputeBollingerBands_MiddleEqualsSma20()
    {
        var closes = Linear(100f, 0.5f, 40);
        var sma = FeatureEngineering.ComputeSma(closes, 20);
        var (_, mid, _) = FeatureEngineering.ComputeBollingerBands(closes, 20);
        for (int i = 19; i < 40; i++)
            Assert.Equal(sma[i], mid[i], 3);
    }

    // ── ComputeObvNormalized ───────────────────────────────────────────────────

    [Fact]
    public void ComputeObvNorm_AllRising_Positive()
    {
        var closes = Linear(100f, 1f, 40);
        var vols = Flat(1000f, 40);
        var obv = FeatureEngineering.ComputeObvNormalized(closes, vols, 20);
        for (int i = 21; i < 40; i++)
            Assert.True(obv[i] > 0, $"OBV should be positive at {i}");
    }

    [Fact]
    public void ComputeObvNorm_AllFalling_Negative()
    {
        var closes = Linear(200f, -1f, 40);
        var vols = Flat(1000f, 40);
        var obv = FeatureEngineering.ComputeObvNormalized(closes, vols, 20);
        for (int i = 21; i < 40; i++)
            Assert.True(obv[i] < 0, $"OBV should be negative at {i}");
    }

    [Fact]
    public void ComputeObvNorm_ClampedToMinusOneToOne()
    {
        var closes = Linear(100f, 5f, 40);
        var vols = Flat(1f, 40);
        var obv = FeatureEngineering.ComputeObvNormalized(closes, vols, 20);
        Assert.All(obv, v => Assert.True(v >= -1f && v <= 1f, $"OBV {v} out of [-1,1]"));
    }

    [Fact]
    public void ComputeObvNorm_ZeroVolume_ReturnsZero()
    {
        var closes = Linear(100f, 1f, 30);
        var vols = Flat(0f, 30);
        var obv = FeatureEngineering.ComputeObvNormalized(closes, vols, 20);
        // totalVol=0 → norm[i]=0 for all i >= period
        for (int i = 20; i < 30; i++)
            Assert.Equal(0f, obv[i]);
    }

    // ── ComputeRoc ─────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeRoc_FlatPrices_Zero()
    {
        var roc = FeatureEngineering.ComputeRoc(Flat(100f, 20), 10);
        for (int i = 10; i < 20; i++)
            Assert.Equal(0f, roc[i], 4);
    }

    [Fact]
    public void ComputeRoc_PriceDoubles_ClampedToHalf()
    {
        // close[0..9]=100, close[10..19]=200 → at i=10..19, look-back is 100 → ROC=1.0 clamped to 0.5
        // At i>=20 the look-back is also 200, so ROC=0 (separate concern, not tested here)
        var closes = new float[25];
        for (int i = 0; i < 10; i++) closes[i] = 100f;
        for (int i = 10; i < 25; i++) closes[i] = 200f;
        var roc = FeatureEngineering.ComputeRoc(closes, 10);
        for (int i = 10; i < 20; i++)
            Assert.True(Math.Abs(roc[i] - 0.5f) < 0.001f, $"Expected 0.5 at roc[{i}], got {roc[i]}");
    }

    [Fact]
    public void ComputeRoc_PriceHalves_ClampedToMinusHalf()
    {
        // close[0..9]=200, close[10..19]=50 → at i=10..19, look-back is 200 → ROC=-0.75 clamped to -0.5
        var closes = new float[25];
        for (int i = 0; i < 10; i++) closes[i] = 200f;
        for (int i = 10; i < 25; i++) closes[i] = 50f;
        var roc = FeatureEngineering.ComputeRoc(closes, 10);
        for (int i = 10; i < 20; i++)
            Assert.True(Math.Abs(roc[i] - (-0.5f)) < 0.001f, $"Expected -0.5 at roc[{i}], got {roc[i]}");
    }

    [Fact]
    public void ComputeRoc_AlwaysInBounds()
    {
        var roc = FeatureEngineering.ComputeRoc(Alternating(100f, 50f, 40), 10);
        Assert.All(roc, v => Assert.True(v >= -0.5f && v <= 0.5f, $"ROC {v} out of [-0.5,0.5]"));
    }

    // ── BuildIndicators + BuildFeatures end-to-end ────────────────────────────

    [Fact]
    public void BuildIndicators_ArrayLengthsMatchInput()
    {
        var klines = MakeKlines(100);
        var ind = FeatureEngineering.BuildIndicators(klines);
        Assert.Equal(100, ind.N);
        Assert.Equal(100, ind.Closes.Length);
        Assert.Equal(100, ind.Rsi14.Length);
        Assert.Equal(100, ind.MacdHistogram.Length);
        Assert.Equal(100, ind.MacdSignal.Length);
        Assert.Equal(100, ind.Atr14.Length);
        Assert.Equal(100, ind.VolSma14.Length);
        Assert.Equal(100, ind.Sma20.Length);
        Assert.Equal(100, ind.BbUp.Length);
        Assert.Equal(100, ind.BbDn.Length);
        Assert.Equal(100, ind.ObvNorm.Length);
        Assert.Equal(100, ind.Adx14.Length);
        Assert.Equal(100, ind.Roc10.Length);
    }

    [Fact]
    public void BuildFeatures_RowCountMatchesExpected()
    {
        var klines = MakeKlines(100);
        var features = FeatureEngineering.BuildFeatures(klines, horizon: 1);
        // Rows = [Warmup .. n-horizon-1], so count = n - Warmup - horizon
        int expected = 100 - FeatureEngineering.Warmup - 1;
        Assert.Equal(expected, features.Rows.Count);
    }

    [Fact]
    public void BuildFeatures_Horizon3_TwoFewerRows()
    {
        var klines = MakeKlines(100);
        int h1 = FeatureEngineering.BuildFeatures(klines, horizon: 1).Rows.Count;
        int h3 = FeatureEngineering.BuildFeatures(klines, horizon: 3).Rows.Count;
        Assert.Equal(h1 - 2, h3);
    }

    [Fact]
    public void BuildFeatures_LatestFeatures_NotNull_WhenSufficientData()
    {
        var features = FeatureEngineering.BuildFeatures(MakeKlines(100));
        Assert.NotNull(features.LatestFeatures);
    }

    [Fact]
    public void BuildFeatures_LatestFeatures_Null_WhenBelowWarmup()
    {
        var features = FeatureEngineering.BuildFeatures(MakeKlines(FeatureEngineering.Warmup - 1));
        Assert.Null(features.LatestFeatures);
    }

    [Fact]
    public void BuildFeatures_AllFeatureValues_AreFinite()
    {
        var features = FeatureEngineering.BuildFeatures(MakeKlines(200, withVariance: true));
        foreach (var row in features.Rows)
        {
            Assert.True(float.IsFinite(row.Rsi14),          "Rsi14 not finite");
            Assert.True(float.IsFinite(row.MacdHistogram),  "MacdHistogram not finite");
            Assert.True(float.IsFinite(row.MacdSignal),     "MacdSignal not finite");
            Assert.True(float.IsFinite(row.AtrNorm),        "AtrNorm not finite");
            Assert.True(float.IsFinite(row.VolumeSmaRatio), "VolumeSmaRatio not finite");
            Assert.True(float.IsFinite(row.BbPosition),     "BbPosition not finite");
            Assert.True(float.IsFinite(row.Sma20Ratio),     "Sma20Ratio not finite");
            Assert.True(float.IsFinite(row.Return1),        "Return1 not finite");
            Assert.True(float.IsFinite(row.Return5),        "Return5 not finite");
            Assert.True(float.IsFinite(row.Return20),       "Return20 not finite");
            Assert.True(float.IsFinite(row.Volatility20),   "Volatility20 not finite");
            Assert.True(float.IsFinite(row.ObvNorm),        "ObvNorm not finite");
            Assert.True(float.IsFinite(row.Adx14),          "Adx14 not finite");
            Assert.True(float.IsFinite(row.Roc10),          "Roc10 not finite");
        }
    }

    [Fact]
    public void BuildFeatures_RisingPrices_AllLabelsTrue()
    {
        // Strictly rising close prices → closes[i+1] > closes[i] always → Label=true
        var klines = Enumerable.Range(0, 200).Select(i => new DerivedKline
        {
            Asset = "TEST/USD",
            OpenTime = DateTime.UtcNow.AddHours(-200 + i),
            Open = 100m + i,
            High = 102m + i,
            Low = 99m + i,
            Close = 101m + i,
            Volume = 1000m,
            VolumeWeightedAveragePrice = 101m + i,
        }).ToList();

        var features = FeatureEngineering.BuildFeatures(klines, horizon: 1);
        Assert.All(features.Rows, r => Assert.True(r.Label, "All labels should be true for rising prices"));
    }

    [Fact]
    public void BuildFeatures_FallingPrices_AllLabelsFalse()
    {
        // Strictly falling close prices → closes[i+1] < closes[i] → Label=false
        var klines = Enumerable.Range(0, 200).Select(i => new DerivedKline
        {
            Asset = "TEST/USD",
            OpenTime = DateTime.UtcNow.AddHours(-200 + i),
            Open = 500m - i,
            High = 502m - i,
            Low = 499m - i,
            Close = 500m - i,
            Volume = 1000m,
            VolumeWeightedAveragePrice = 500m - i,
        }).ToList();

        var features = FeatureEngineering.BuildFeatures(klines, horizon: 1);
        Assert.All(features.Rows, r => Assert.False(r.Label, "All labels should be false for falling prices"));
    }

    [Fact]
    public void BuildFeatures_InvalidHorizon_Throws()
    {
        var ind = FeatureEngineering.BuildIndicators(MakeKlines(100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FeatureEngineering.BuildFeatures(ind, horizon: 0));
    }

    [Fact]
    public void BuildFeatures_ExposesCloseAndSmaArrays()
    {
        var features = FeatureEngineering.BuildFeatures(MakeKlines(100));
        Assert.Equal(100, features.Closes.Length);
        Assert.Equal(100, features.Sma5.Length);
        Assert.Equal(100, features.Sma20.Length);
    }

    // ── RsiNormalization: Rsi14 feature is divided by 100 ─────────────────────

    [Fact]
    public void BuildFeatures_Rsi14Feature_NormalisedToZeroOne()
    {
        var features = FeatureEngineering.BuildFeatures(MakeKlines(200, withVariance: true));
        Assert.All(features.Rows, r =>
            Assert.True(r.Rsi14 >= 0f && r.Rsi14 <= 1f, $"Rsi14 feature {r.Rsi14} out of [0,1]"));
    }

    // ── BbPosition is clamped [0,1] ────────────────────────────────────────────

    [Fact]
    public void BuildFeatures_BbPosition_InZeroOneRange()
    {
        var features = FeatureEngineering.BuildFeatures(MakeKlines(200, withVariance: true));
        Assert.All(features.Rows, r =>
            Assert.True(r.BbPosition >= 0f && r.BbPosition <= 1f, $"BbPosition {r.BbPosition} out of [0,1]"));
    }

    // ── VolumeSmaRatio is clamped [0,5] ────────────────────────────────────────

    [Fact]
    public void BuildFeatures_VolumeSmaRatio_Clamped()
    {
        var features = FeatureEngineering.BuildFeatures(MakeKlines(200, withVariance: true));
        Assert.All(features.Rows, r =>
            Assert.True(r.VolumeSmaRatio >= 0f && r.VolumeSmaRatio <= 5f, $"VolumeSmaRatio {r.VolumeSmaRatio} out of [0,5]"));
    }

    // ── BuildMarketContextMap ─────────────────────────────────────────────────

    [Fact]
    public void BuildMarketContextMap_EmptyInput_ReturnsEmpty()
    {
        var map = FeatureEngineering.BuildMarketContextMap([]);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildMarketContextMap_HasEntryPerKline()
    {
        var klines = MakeKlines(50);
        var map = FeatureEngineering.BuildMarketContextMap(klines);
        Assert.Equal(50, map.Count);
    }

    [Fact]
    public void BuildMarketContextMap_AllValuesFinite()
    {
        var map = FeatureEngineering.BuildMarketContextMap(MakeKlines(100, withVariance: true));
        foreach (var pt in map.Values)
        {
            Assert.True(float.IsFinite(pt.Return20),    "Return20 not finite");
            Assert.True(float.IsFinite(pt.Volatility20), "Volatility20 not finite");
        }
    }
}
