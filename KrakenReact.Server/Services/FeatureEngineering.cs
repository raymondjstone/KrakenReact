using KrakenReact.Server.Models;
using Microsoft.ML.Data;

namespace KrakenReact.Server.Services;

/// <summary>ML.NET input schema - individual float properties so Concatenate() can reference them by name.</summary>
public class CandleFeatures
{
    public float Rsi14 { get; set; }
    public float MacdHistogram { get; set; }
    public float MacdSignal { get; set; }
    public float AtrNorm { get; set; }          // ATR / close
    public float VolumeSmaRatio { get; set; }   // volume / SMA14(volume), clamped [0,5]
    public float VolumePercentile20 { get; set; }
    public float BbPosition { get; set; }       // position within Bollinger Bands [0,1]
    public float Sma20Ratio { get; set; }       // close / SMA20 - 1
    public float Return1 { get; set; }          // 1-period log return
    public float Return5 { get; set; }          // 5-period log return
    public float Return20 { get; set; }         // 20-period log return
    public float Volatility20 { get; set; }     // rolling std of 20 log-returns
    public float VwapRatio { get; set; }        // close / VWAP - 1
    public float HighLowRange { get; set; }     // (high - low) / close
    public float HourOfDaySin { get; set; }
    public float HourOfDayCos { get; set; }
    public float DayOfWeekSin { get; set; }
    public float DayOfWeekCos { get; set; }
    public float BtcReturn20 { get; set; }
    public float BtcVolatility20 { get; set; }
    public float ObvNorm { get; set; }          // 20-period net OBV change / cumulative volume [-1,1]
    public float Adx14 { get; set; }            // ADX(14) / 100, trend strength [0,1]
    public float Roc10 { get; set; }            // (close - close[10]) / close[10], clamped [-0.5,0.5]
    public bool Label { get; set; }             // true = future close > this candle close
}

public class BinaryPrediction
{
    [ColumnName("PredictedLabel")]
    public bool PredictedLabel { get; set; }
    public float Probability { get; set; }
    public float Score { get; set; }
}

public class MarketContextPoint
{
    public float Return20 { get; set; }
    public float Volatility20 { get; set; }
}

/// <summary>Pre-computed indicator arrays for a single kline series, shared across horizon builds.</summary>
public class ComputedIndicators
{
    public int N { get; init; }
    public DateTime[] OpenTimes { get; init; } = [];
    public float[] Closes { get; init; } = [];
    public float[] Highs { get; init; } = [];
    public float[] Lows { get; init; } = [];
    public float[] Vols { get; init; } = [];
    public float[] Vwaps { get; init; } = [];
    public float[] LogRet { get; init; } = [];
    public float[] Rsi14 { get; init; } = [];
    public float[] MacdHistogram { get; init; } = [];
    public float[] MacdSignal { get; init; } = [];
    public float[] Atr14 { get; init; } = [];
    public float[] VolSma14 { get; init; } = [];
    public float[] Sma5 { get; init; } = [];
    public float[] Sma20 { get; init; } = [];
    public float[] BbUp { get; init; } = [];
    public float[] BbDn { get; init; } = [];
    public float[] ObvNorm { get; init; } = [];
    public float[] Adx14 { get; init; } = [];
    public float[] Roc10 { get; init; } = [];
}

public class FeatureSet
{
    public List<CandleFeatures> Rows { get; set; } = new();
    public CandleFeatures? LatestFeatures { get; set; }
    public int TotalCandles { get; set; }
    // Raw arrays for benchmark SMA computation in the job
    public float[] Closes { get; set; } = [];
    public float[] Sma5 { get; set; } = [];
    public float[] Sma20 { get; set; } = [];
}

public static class FeatureEngineering
{
    public const int Warmup = 36; // periods needed before first valid feature row (MACD slow=26 + signal=9 + 1)

    public static readonly string[] FeatureColumnNames =
    [
        nameof(CandleFeatures.Rsi14),
        nameof(CandleFeatures.MacdHistogram),
        nameof(CandleFeatures.MacdSignal),
        nameof(CandleFeatures.AtrNorm),
        nameof(CandleFeatures.VolumeSmaRatio),
        nameof(CandleFeatures.VolumePercentile20),
        nameof(CandleFeatures.BbPosition),
        nameof(CandleFeatures.Sma20Ratio),
        nameof(CandleFeatures.Return1),
        nameof(CandleFeatures.Return5),
        nameof(CandleFeatures.Return20),
        nameof(CandleFeatures.Volatility20),
        nameof(CandleFeatures.VwapRatio),
        nameof(CandleFeatures.HighLowRange),
        nameof(CandleFeatures.HourOfDaySin),
        nameof(CandleFeatures.HourOfDayCos),
        nameof(CandleFeatures.DayOfWeekSin),
        nameof(CandleFeatures.DayOfWeekCos),
        nameof(CandleFeatures.BtcReturn20),
        nameof(CandleFeatures.BtcVolatility20),
        nameof(CandleFeatures.ObvNorm),
        nameof(CandleFeatures.Adx14),
        nameof(CandleFeatures.Roc10),
    ];

    /// <summary>
    /// Computes all indicator arrays once for a kline series.
    /// Pass the returned object to BuildFeatures for each horizon to avoid repeating indicator work.
    /// </summary>
    public static ComputedIndicators BuildIndicators(List<DerivedKline> klines)
    {
        int n = klines.Count;
        var openTimes = klines.Select(k => k.OpenTime).ToArray();
        var closes    = klines.Select(k => (float)k.Close).ToArray();
        var highs     = klines.Select(k => (float)k.High).ToArray();
        var lows      = klines.Select(k => (float)k.Low).ToArray();
        var vols      = klines.Select(k => (float)k.Volume).ToArray();
        var vwaps     = klines.Select(k => (float)k.VolumeWeightedAveragePrice).ToArray();

        var rsi14   = ComputeRsi(closes, 14);
        var (_, signal, hist) = ComputeMacd(closes);
        var atr14   = ComputeAtr(highs, lows, closes, 14);
        var volSma14 = ComputeSma(vols, 14);
        var sma5    = ComputeSma(closes, 5);
        var sma20   = ComputeSma(closes, 20);
        var (bbUp, _, bbDn) = ComputeBollingerBands(closes, 20);

        var logRet = new float[n];
        for (int i = 1; i < n; i++)
            if (closes[i - 1] > 0) logRet[i] = MathF.Log(closes[i] / closes[i - 1]);

        return new ComputedIndicators
        {
            N         = n,
            OpenTimes = openTimes,
            Closes    = closes,
            Highs     = highs,
            Lows      = lows,
            Vols      = vols,
            Vwaps     = vwaps,
            LogRet    = logRet,
            Rsi14     = rsi14,
            MacdHistogram = hist,
            MacdSignal    = signal,
            Atr14     = atr14,
            VolSma14  = volSma14,
            Sma5      = sma5,
            Sma20     = sma20,
            BbUp      = bbUp,
            BbDn      = bbDn,
            ObvNorm   = ComputeObvNormalized(closes, vols),
            Adx14     = ComputeAdx(highs, lows, closes, 14),
            Roc10     = ComputeRoc(closes, 10),
        };
    }

    /// <summary>
    /// Builds a labelled feature set for a given horizon using pre-computed indicators.
    /// Call BuildIndicators once and reuse it across horizons.
    /// </summary>
    public static FeatureSet BuildFeatures(
        ComputedIndicators ind,
        int horizon = 1,
        Dictionary<DateTime, MarketContextPoint>? marketContext = null,
        bool zeroMarketContext = false)
    {
        if (horizon < 1) throw new ArgumentOutOfRangeException(nameof(horizon));

        int n = ind.N;
        var rows = new List<CandleFeatures>(n);

        // Training rows: indices [Warmup .. n-horizon-1] - each needs closes[i+horizon] for the label
        for (int i = Warmup; i < n - horizon; i++)
        {
            var row = BuildRow(i, ind, marketContext, zeroMarketContext);
            row.Label = ind.Closes[i + horizon] > ind.Closes[i];
            rows.Add(row);
        }

        // Latest features: index n-1 (no future label — used for live prediction)
        CandleFeatures? latest = null;
        if (n - 1 >= Warmup)
            latest = BuildRow(n - 1, ind, marketContext, zeroMarketContext);

        return new FeatureSet
        {
            Rows           = rows,
            LatestFeatures = latest,
            TotalCandles   = n,
            Closes         = ind.Closes,
            Sma5           = ind.Sma5,
            Sma20          = ind.Sma20,
        };
    }

    /// <summary>Convenience overload for callers that don't share indicators across horizons.</summary>
    public static FeatureSet BuildFeatures(
        List<DerivedKline> klines,
        int horizon = 1,
        Dictionary<DateTime, MarketContextPoint>? marketContext = null)
        => BuildFeatures(BuildIndicators(klines), horizon, marketContext);

    public static Dictionary<DateTime, MarketContextPoint> BuildMarketContextMap(List<DerivedKline> klines)
    {
        int n = klines.Count;
        var map = new Dictionary<DateTime, MarketContextPoint>(n);
        if (n == 0) return map;

        var openTimes = klines.Select(k => k.OpenTime).ToArray();
        var closes    = klines.Select(k => (float)k.Close).ToArray();

        var logRet = new float[n];
        for (int i = 1; i < n; i++)
            if (closes[i - 1] > 0) logRet[i] = MathF.Log(closes[i] / closes[i - 1]);

        for (int i = 0; i < n; i++)
        {
            float ret20 = i >= 20 && closes[i - 20] > 0 ? (closes[i] - closes[i - 20]) / closes[i - 20] : 0f;
            float vol20 = 0f;
            if (i >= 20)
            {
                float sum = 0f, sumSq = 0f;
                for (int j = i - 19; j <= i; j++) { sum += logRet[j]; sumSq += logRet[j] * logRet[j]; }
                float mean = sum / 20f;
                float variance = sumSq / 20f - mean * mean;
                vol20 = variance > 0 ? MathF.Sqrt(variance) : 0f;
            }

            map[openTimes[i]] = new MarketContextPoint
            {
                Return20    = Safe(ret20),
                Volatility20 = Safe(vol20),
            };
        }

        return map;
    }

    private static CandleFeatures BuildRow(
        int i,
        ComputedIndicators ind,
        Dictionary<DateTime, MarketContextPoint>? marketContext,
        bool zeroMarketContext)
    {
        float c      = ind.Closes[i];
        var closes   = ind.Closes;
        var logRet   = ind.LogRet;
        var vols     = ind.Vols;
        var vwaps    = ind.Vwaps;
        var sma20    = ind.Sma20;
        var bbUp     = ind.BbUp;
        var bbDn     = ind.BbDn;
        var atr14    = ind.Atr14;
        var volSma14 = ind.VolSma14;

        float ret5  = i >= 5  && closes[i - 5]  > 0 ? (c - closes[i - 5])  / closes[i - 5]  : 0f;
        float ret20 = i >= 20 && closes[i - 20] > 0 ? (c - closes[i - 20]) / closes[i - 20] : 0f;

        // 20-period rolling volatility (population std of log returns)
        float vol20 = 0f;
        if (i >= 20)
        {
            float sum = 0f, sumSq = 0f;
            for (int j = i - 19; j <= i; j++) { sum += logRet[j]; sumSq += logRet[j] * logRet[j]; }
            float mean = sum / 20f;
            float variance = sumSq / 20f - mean * mean;
            vol20 = variance > 0 ? MathF.Sqrt(variance) : 0f;
        }

        float bbRange   = bbUp[i] - bbDn[i];
        float bbPos     = bbRange > 0 ? Math.Clamp((c - bbDn[i]) / bbRange, 0f, 1f) : 0.5f;
        float volRatio  = volSma14[i] > 0 ? Math.Clamp(vols[i] / volSma14[i], 0f, 5f) : 1f;
        float volPct20  = 0.5f;
        if (i >= 19)
        {
            int belowOrEqual = 0;
            for (int j = i - 19; j <= i; j++)
                if (vols[j] <= vols[i]) belowOrEqual++;
            volPct20 = belowOrEqual / 20f;
        }
        float vwapRatio  = vwaps[i] > 0 ? c / vwaps[i] - 1f : 0f;
        float atrNorm    = c > 0 ? atr14[i] / c : 0f;
        float sma20Ratio = sma20[i] > 0 ? c / sma20[i] - 1f : 0f;
        float hlRange    = c > 0 ? (ind.Highs[i] - ind.Lows[i]) / c : 0f;

        var timestamp   = ind.OpenTimes[i].Kind == DateTimeKind.Utc
            ? ind.OpenTimes[i]
            : DateTime.SpecifyKind(ind.OpenTimes[i], DateTimeKind.Utc);
        float hourFraction = (timestamp.Hour + timestamp.Minute / 60f) / 24f;
        float dayFraction  = ((int)timestamp.DayOfWeek) / 7f;

        MarketContextPoint? market = null;
        if (!zeroMarketContext)
            marketContext?.TryGetValue(ind.OpenTimes[i], out market);

        return new CandleFeatures
        {
            Rsi14              = Safe(ind.Rsi14[i] / 100f),
            MacdHistogram      = Safe(ind.MacdHistogram[i]),
            MacdSignal         = Safe(ind.MacdSignal[i]),
            AtrNorm            = Safe(atrNorm),
            VolumeSmaRatio     = Safe(volRatio),
            VolumePercentile20 = Safe(volPct20),
            BbPosition         = Safe(bbPos),
            Sma20Ratio         = Safe(sma20Ratio),
            Return1            = Safe(logRet[i]),
            Return5            = Safe(ret5),
            Return20           = Safe(ret20),
            Volatility20       = Safe(vol20),
            VwapRatio          = Safe(vwapRatio),
            HighLowRange       = Safe(hlRange),
            HourOfDaySin       = Safe(MathF.Sin(2f * MathF.PI * hourFraction)),
            HourOfDayCos       = Safe(MathF.Cos(2f * MathF.PI * hourFraction)),
            DayOfWeekSin       = Safe(MathF.Sin(2f * MathF.PI * dayFraction)),
            DayOfWeekCos       = Safe(MathF.Cos(2f * MathF.PI * dayFraction)),
            BtcReturn20        = Safe(market?.Return20 ?? 0f),
            BtcVolatility20    = Safe(market?.Volatility20 ?? 0f),
            ObvNorm            = Safe(ind.ObvNorm[i]),
            Adx14              = Safe(ind.Adx14[i]),
            Roc10              = Safe(ind.Roc10[i]),
        };
    }

    private static float Safe(float v) => float.IsFinite(v) ? v : 0f;

    // ── Technical indicators ────────────────────────────────────────────────

    public static float[] ComputeRsi(float[] closes, int period = 14)
    {
        var rsi = new float[closes.Length];
        if (closes.Length <= period) return rsi;

        float avgGain = 0f, avgLoss = 0f;
        for (int i = 1; i <= period; i++)
        {
            float d = closes[i] - closes[i - 1];
            if (d > 0) avgGain += d; else avgLoss -= d;
        }
        avgGain /= period;
        avgLoss /= period;
        rsi[period] = avgLoss == 0 ? 100f : 100f - 100f / (1f + avgGain / avgLoss);

        for (int i = period + 1; i < closes.Length; i++)
        {
            float d    = closes[i] - closes[i - 1];
            float gain = d > 0 ? d : 0f;
            float loss = d < 0 ? -d : 0f;
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
            rsi[i] = avgLoss == 0 ? 100f : 100f - 100f / (1f + avgGain / avgLoss);
        }
        return rsi;
    }

    public static float[] ComputeEma(float[] values, int period)
    {
        var ema = new float[values.Length];
        if (values.Length < period) return ema;

        float mult = 2f / (period + 1);
        float seed = 0f;
        for (int i = 0; i < period; i++) seed += values[i];
        ema[period - 1] = seed / period;

        for (int i = period; i < values.Length; i++)
            ema[i] = (values[i] - ema[i - 1]) * mult + ema[i - 1];

        return ema;
    }

    public static (float[] Macd, float[] Signal, float[] Histogram) ComputeMacd(
        float[] closes, int fast = 12, int slow = 26, int signalPeriod = 9)
    {
        var emaFast = ComputeEma(closes, fast);
        var emaSlow = ComputeEma(closes, slow);
        var macd = new float[closes.Length];
        for (int i = slow - 1; i < closes.Length; i++)
            macd[i] = emaFast[i] - emaSlow[i];

        var sig  = ComputeEma(macd, signalPeriod);
        var hist = new float[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            hist[i] = macd[i] - sig[i];

        return (macd, sig, hist);
    }

    public static float[] ComputeAtr(float[] highs, float[] lows, float[] closes, int period = 14)
    {
        int n  = closes.Length;
        var tr  = new float[n];
        var atr = new float[n];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < n; i++)
            tr[i] = MathF.Max(highs[i] - lows[i],
                    MathF.Max(MathF.Abs(highs[i] - closes[i - 1]),
                              MathF.Abs(lows[i] - closes[i - 1])));

        float seed = 0f;
        for (int i = 0; i < period; i++) seed += tr[i];
        atr[period - 1] = seed / period;
        for (int i = period; i < n; i++)
            atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;

        return atr;
    }

    public static float[] ComputeSma(float[] values, int period)
    {
        var sma = new float[values.Length];
        float sum = 0f;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            if (i >= period) sum -= values[i - period];
            if (i >= period - 1) sma[i] = sum / period;
        }
        return sma;
    }

    public static (float[] Upper, float[] Middle, float[] Lower) ComputeBollingerBands(
        float[] closes, int period = 20, float stdMult = 2f)
    {
        var mid = ComputeSma(closes, period);
        var up  = new float[closes.Length];
        var dn  = new float[closes.Length];

        for (int i = period - 1; i < closes.Length; i++)
        {
            float sumSq = 0f;
            for (int j = i - period + 1; j <= i; j++)
            {
                float d = closes[j] - mid[i];
                sumSq += d * d;
            }
            float std = MathF.Sqrt(sumSq / period);
            up[i] = mid[i] + stdMult * std;
            dn[i] = mid[i] - stdMult * std;
        }
        return (up, mid, dn);
    }

    /// <summary>
    /// On-Balance Volume normalised by the rolling cumulative absolute volume.
    /// Returns values in [-1, 1]: positive = net buying pressure, negative = selling pressure.
    /// </summary>
    public static float[] ComputeObvNormalized(float[] closes, float[] vols, int period = 20)
    {
        int n    = closes.Length;
        var obv  = new float[n];
        var norm = new float[n];

        for (int i = 1; i < n; i++)
        {
            if (closes[i] > closes[i - 1])       obv[i] = obv[i - 1] + vols[i];
            else if (closes[i] < closes[i - 1])  obv[i] = obv[i - 1] - vols[i];
            else                                  obv[i] = obv[i - 1];
        }

        for (int i = period; i < n; i++)
        {
            float totalVol = 0f;
            for (int j = i - period + 1; j <= i; j++) totalVol += MathF.Abs(vols[j]);
            float obvChange = obv[i] - obv[i - period];
            norm[i] = totalVol > 0 ? Math.Clamp(obvChange / totalVol, -1f, 1f) : 0f;
        }

        return norm;
    }

    /// <summary>
    /// Average Directional Index using Wilder smoothing. Returns values in [0, 1] (raw ADX / 100).
    /// Valid from index 2*period onwards; earlier indices are 0.
    /// </summary>
    public static float[] ComputeAdx(float[] highs, float[] lows, float[] closes, int period = 14)
    {
        int n   = closes.Length;
        var adx = new float[n];
        if (n < 2 * period + 2) return adx;

        var tr      = new float[n];
        var dmPlus  = new float[n];
        var dmMinus = new float[n];

        for (int i = 1; i < n; i++)
        {
            tr[i] = MathF.Max(highs[i] - lows[i],
                    MathF.Max(MathF.Abs(highs[i] - closes[i - 1]),
                              MathF.Abs(lows[i] - closes[i - 1])));

            float upMove   = highs[i] - highs[i - 1];
            float downMove = lows[i - 1] - lows[i];
            dmPlus[i]  = upMove   > downMove && upMove   > 0 ? upMove   : 0f;
            dmMinus[i] = downMove > upMove   && downMove > 0 ? downMove : 0f;
        }

        // Initialise Wilder smoothed sums from bars 1..period
        float smTr = 0f, smPlus = 0f, smMinus = 0f;
        for (int i = 1; i <= period; i++) { smTr += tr[i]; smPlus += dmPlus[i]; smMinus += dmMinus[i]; }

        float adxVal  = 0f;
        int   dxCount = 0;
        float dxAccum = 0f;

        for (int i = period + 1; i < n; i++)
        {
            smTr    = smTr    - smTr    / period + tr[i];
            smPlus  = smPlus  - smPlus  / period + dmPlus[i];
            smMinus = smMinus - smMinus / period + dmMinus[i];

            float diPlus  = smTr > 0 ? smPlus  / smTr : 0f;
            float diMinus = smTr > 0 ? smMinus / smTr : 0f;
            float dxVal   = diPlus + diMinus > 0
                ? MathF.Abs(diPlus - diMinus) / (diPlus + diMinus)
                : 0f;

            if (dxCount < period)
            {
                dxAccum += dxVal;
                dxCount++;
                if (dxCount == period)
                {
                    adxVal = dxAccum / period;
                    adx[i] = adxVal;
                }
            }
            else
            {
                adxVal = (adxVal * (period - 1) + dxVal) / period;
                adx[i] = adxVal;
            }
        }

        return adx;
    }

    /// <summary>Rate of Change over `period` bars, clamped to [-0.5, 0.5].</summary>
    public static float[] ComputeRoc(float[] closes, int period = 10)
    {
        int n   = closes.Length;
        var roc = new float[n];
        for (int i = period; i < n; i++)
            if (closes[i - period] > 0)
                roc[i] = Math.Clamp((closes[i] - closes[i - period]) / closes[i - period], -0.5f, 0.5f);
        return roc;
    }
}
