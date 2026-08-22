namespace KrakenReact.Server.Analysis;

public enum TrendDirection { None, Up, Down }

/// <summary>
/// Where a trend stands within its own life: Start is the expanding momentum straight after a
/// crossover, Middle the established trend, End the decayed tail once momentum has been contracting
/// long enough to be believed.
/// </summary>
public enum TrendPhase { None, Start, Middle, End }

/// <summary>Thresholds governing trend direction and phase detection.</summary>
public class TrendDetectionParameters
{
    public static TrendDetectionParameters Default { get; } = new();

    public int FastPeriod { get; init; } = 12;
    public int SlowPeriod { get; init; } = 26;
    public int SignalPeriod { get; init; } = 9;
    public bool UseZeroLagMovingAverages { get; init; }

    /// <summary>Contracting bars required before the trend is called ended.</summary>
    public int EndConfirmationBars { get; init; } = 5;

    /// <summary>Strength at or below which a direction is reported but flagged as neutral.</summary>
    public decimal NeutralStrengthPercent { get; init; } = 15m;

    /// <summary>
    /// The share of the trailing histogram maximum this regime's own extreme must reach before its
    /// End phase is armed, which stops a limp move being called a trend that has ended.
    /// </summary>
    public decimal MinimumExtremeFraction { get; init; }

    public int HistogramLookbackBars { get; init; } = 130;
    public int AtrPeriod { get; init; } = 14;
    public int TrendAdvanceLookbackBars { get; init; } = 32;

    /// <summary>Ranges the price must have travelled for the End phase to be admitted; zero disables the gate.</summary>
    public decimal MinimumTrendAdvanceAtrMultiple { get; init; }

    public bool UseRsiGate { get; init; }
    public int RsiPeriod { get; init; } = 14;
    public decimal RsiUpperBound { get; init; } = 70m;
    public decimal RsiLowerBound { get; init; } = 30m;
    public int RsiLookbackBars { get; init; } = 10;
}

/// <summary>The descriptive trend reading of a single bar.</summary>
public class TrendState
{
    public TrendDirection Direction { get; init; }
    public TrendPhase Phase { get; init; }

    /// <summary>Momentum from 0 to 100, relative to this market's own recent histogram range.</summary>
    public decimal StrengthPercent { get; init; }

    public int FallingBarCount { get; init; }
    public bool IsNeutral { get; init; }

    /// <summary>The next close that would flip the histogram's sign, and with it the trend direction.</summary>
    public decimal DirectionChangePrice { get; init; }
}

/// <summary>Per-bar trend readings across a whole series.</summary>
public class TrendStateSeries
{
    public TrendDirection[] Directions { get; init; } = [];
    public TrendPhase[] Phases { get; init; } = [];
    public decimal[] HistogramValues { get; init; } = [];
    public int[] FallingBarCounts { get; init; } = [];
    public int WarmupBars { get; init; }
    public int Count => Directions.Length;
}

/// <summary>A confirmed swing high or low in the price series.</summary>
public sealed record TrendPivot(int Index, DateTime OpenTime, decimal Price, bool IsHigh);

/// <summary>
/// Computes the longer-term trend direction and phase over a candle series from the MACD histogram
/// within each regime between crossovers.
/// </summary>
public static class TrendDetection
{
    private const int WarmupSlowPeriodMultiple = 3;

    public static TrendStateSeries ComputeTrendStates(IReadOnlyList<AnalysisCandle> candles) =>
        ComputeTrendStates(candles, TrendDetectionParameters.Default);

    /// <summary>Computes the per-bar direction and phase, leaving None across the indicator warm-up.</summary>
    public static TrendStateSeries ComputeTrendStates(IReadOnlyList<AnalysisCandle> candles, TrendDetectionParameters parameters)
    {
        int count = candles?.Count ?? 0;
        var directions = new TrendDirection[count];
        var phases = new TrendPhase[count];
        var histogram = new decimal[count];
        var fallingBarCounts = new int[count];
        int warmupBars = Math.Min(count, parameters.SlowPeriod * WarmupSlowPeriodMultiple);
        var series = new TrendStateSeries
        {
            Directions = directions,
            Phases = phases,
            HistogramValues = histogram,
            FallingBarCounts = fallingBarCounts,
            WarmupBars = warmupBars
        };
        if (count == 0) return series;

        var closes = new decimal[count];
        for (int i = 0; i < count; i++) closes[i] = candles![i].Close;
        Array.Copy(ComputeHistogram(closes, parameters), histogram, count);

        var rsi = parameters.UseRsiGate ? Indicators.RelativeStrengthIndex(closes, parameters.RsiPeriod) : null;
        var atr = parameters.MinimumTrendAdvanceAtrMultiple > 0m ? Indicators.AverageTrueRange(candles!, parameters.AtrPeriod) : null;

        // Monotonic deques give the trailing histogram maximum and the trailing price extremes in
        // linear time rather than rescanning the lookback window on every bar.
        var windowIndexes = new int[count];
        var lowWindowIndexes = new int[count];
        var highWindowIndexes = new int[count];
        int windowHead = 0, windowTail = 0, lowHead = 0, lowTail = 0, highHead = 0, highTail = 0;

        var direction = TrendDirection.None;
        var phase = TrendPhase.None;
        decimal extremeMagnitude = 0m;
        int fallingCount = 0, lastOverboughtIndex = int.MinValue / 2, lastOversoldIndex = int.MinValue / 2;

        for (int i = warmupBars; i < count; i++)
        {
            decimal magnitude = Math.Abs(histogram[i]);
            while (windowTail > windowHead && Math.Abs(histogram[windowIndexes[windowTail - 1]]) <= magnitude) windowTail--;
            windowIndexes[windowTail++] = i;
            while (windowIndexes[windowHead] <= i - parameters.HistogramLookbackBars) windowHead++;
            decimal trailingMaximum = Math.Abs(histogram[windowIndexes[windowHead]]);

            if (atr != null)
            {
                while (lowTail > lowHead && candles![lowWindowIndexes[lowTail - 1]].Low >= candles[i].Low) lowTail--;
                lowWindowIndexes[lowTail++] = i;
                while (lowWindowIndexes[lowHead] <= i - parameters.TrendAdvanceLookbackBars) lowHead++;
                while (highTail > highHead && candles![highWindowIndexes[highTail - 1]].High <= candles[i].High) highTail--;
                highWindowIndexes[highTail++] = i;
                while (highWindowIndexes[highHead] <= i - parameters.TrendAdvanceLookbackBars) highHead++;
            }

            if (rsi != null)
            {
                if (rsi[i] >= parameters.RsiUpperBound) lastOverboughtIndex = i;
                if (rsi[i] <= parameters.RsiLowerBound) lastOversoldIndex = i;
            }

            var barDirection = histogram[i] > 0m ? TrendDirection.Up : histogram[i] < 0m ? TrendDirection.Down : direction;
            if (barDirection == TrendDirection.None) continue;

            if (barDirection != direction)
            {
                direction = barDirection;
                phase = TrendPhase.Start;
                extremeMagnitude = magnitude;
                fallingCount = 0;
            }
            else
            {
                decimal previousMagnitude = Math.Abs(histogram[i - 1]);
                if (magnitude < previousMagnitude) fallingCount++;
                else if (magnitude > previousMagnitude) fallingCount = 0;
                if (magnitude > extremeMagnitude) extremeMagnitude = magnitude;

                if (fallingCount == 0) phase = TrendPhase.Start;
                else
                {
                    phase = TrendPhase.Middle;
                    bool armed = parameters.MinimumExtremeFraction <= 0m || extremeMagnitude >= parameters.MinimumExtremeFraction * trailingMaximum;
                    bool rsiConfirmed = rsi == null ||
                        i - (direction == TrendDirection.Up ? lastOverboughtIndex : lastOversoldIndex) <= parameters.RsiLookbackBars;
                    bool advanceConfirmed = atr == null ||
                        (direction == TrendDirection.Up
                            ? candles![i].Close - candles[lowWindowIndexes[lowHead]].Low
                            : candles![highWindowIndexes[highHead]].High - candles[i].Close) >= parameters.MinimumTrendAdvanceAtrMultiple * atr[i];
                    if (armed && fallingCount >= parameters.EndConfirmationBars && rsiConfirmed && advanceConfirmed) phase = TrendPhase.End;
                }
            }

            directions[i] = direction;
            phases[i] = phase;
            fallingBarCounts[i] = Math.Min(fallingCount, parameters.EndConfirmationBars);
        }
        return series;
    }

    public static TrendState ComputeLatestTrendState(IReadOnlyList<AnalysisCandle> candles) =>
        ComputeLatestTrendState(candles, TrendDetectionParameters.Default);

    /// <summary>
    /// The trend reading of the last bar, combining direction and phase with momentum strength and
    /// the close that would flip the direction on the next bar.
    /// </summary>
    public static TrendState ComputeLatestTrendState(IReadOnlyList<AnalysisCandle> candles, TrendDetectionParameters parameters)
    {
        var series = ComputeTrendStates(candles, parameters);
        if (series.Count == 0) return new TrendState();

        int last = series.Count - 1;
        var closes = new decimal[series.Count];
        for (int i = 0; i < series.Count; i++) closes[i] = candles[i].Close;
        decimal strengthPercent = ComputeStrengthPercent(series.HistogramValues, last, series.WarmupBars, parameters.HistogramLookbackBars);

        return new TrendState
        {
            Direction = series.Directions[last],
            Phase = series.Phases[last],
            StrengthPercent = strengthPercent,
            FallingBarCount = series.FallingBarCounts[last],
            IsNeutral = series.Directions[last] != TrendDirection.None && strengthPercent <= parameters.NeutralStrengthPercent,
            DirectionChangePrice = ComputeDirectionChangePrice(closes, parameters)
        };
    }

    /// <summary>
    /// Confirmed swing pivots, where a reversal of the given multiple of ATR away from a running
    /// extreme is what confirms that extreme as a pivot.
    /// </summary>
    public static List<TrendPivot> FindTrendPivots(IReadOnlyList<AnalysisCandle> candles, int atrPeriod, decimal reversalAtrMultiple)
    {
        int count = candles?.Count ?? 0;
        return count < atrPeriod + 2
            ? new List<TrendPivot>()
            : FindTrendPivots(candles!, Indicators.AverageTrueRange(candles!, atrPeriod), atrPeriod, reversalAtrMultiple);
    }

    /// <inheritdoc cref="FindTrendPivots(IReadOnlyList{AnalysisCandle},int,decimal)"/>
    public static List<TrendPivot> FindTrendPivots(IReadOnlyList<AnalysisCandle> candles, decimal[] atr, int atrPeriod, decimal reversalAtrMultiple)
    {
        var pivots = new List<TrendPivot>();
        int count = candles?.Count ?? 0;
        if (count < atrPeriod + 2) return pivots;

        int start = atrPeriod;
        int candidateHighIndex = start, candidateLowIndex = start;
        decimal candidateHigh = candles![start].High, candidateLow = candles[start].Low;
        int direction = 0;

        for (int i = start + 1; i < count; i++)
        {
            if (direction >= 0 && candles[i].High > candidateHigh) { candidateHigh = candles[i].High; candidateHighIndex = i; }
            if (direction <= 0 && candles[i].Low < candidateLow) { candidateLow = candles[i].Low; candidateLowIndex = i; }

            if (direction >= 0 && candidateHigh - candles[i].Low >= reversalAtrMultiple * atr[candidateHighIndex])
            {
                pivots.Add(new TrendPivot(candidateHighIndex, candles[candidateHighIndex].OpenTime, candidateHigh, true));
                direction = -1;
                candidateLow = candles[i].Low;
                candidateLowIndex = i;
            }
            else if (direction <= 0 && candles[i].High - candidateLow >= reversalAtrMultiple * atr[candidateLowIndex])
            {
                pivots.Add(new TrendPivot(candidateLowIndex, candles[candidateLowIndex].OpenTime, candidateLow, false));
                direction = 1;
                candidateHigh = candles[i].High;
                candidateHighIndex = i;
            }
        }
        return pivots;
    }

    private static decimal[] ComputeHistogram(IReadOnlyList<decimal> closes, TrendDetectionParameters parameters) =>
        Indicators.MacdHistogram(closes, parameters.FastPeriod, parameters.SlowPeriod, parameters.SignalPeriod, parameters.UseZeroLagMovingAverages);

    /// <summary>
    /// Momentum at the index from 0 to 100, as the histogram magnitude normalised against the largest
    /// magnitude over the preceding lookback, so the reading expresses strength relative to this
    /// market's own recent range rather than an absolute that means nothing across pairs.
    /// </summary>
    private static decimal ComputeStrengthPercent(decimal[] histogram, int index, int warmupBars, int lookbackBars)
    {
        decimal current = Math.Abs(histogram[index]);
        decimal maximum = 0m;
        for (int i = Math.Max(warmupBars, index - lookbackBars + 1); i <= index; i++)
            maximum = Math.Max(maximum, Math.Abs(histogram[i]));
        return maximum == 0m ? 0m : Math.Min(100m, current / maximum * 100m);
    }

    /// <summary>
    /// The close that would flip the histogram's sign on the next bar. Every moving average is linear
    /// and the next bar's only new input is that close, so the next-bar histogram is an affine
    /// function of it — recovered exactly from two probes and solved in closed form.
    /// </summary>
    private static decimal ComputeDirectionChangePrice(IReadOnlyList<decimal> closes, TrendDetectionParameters parameters)
    {
        int count = closes.Count;
        if (count == 0) return 0m;
        decimal lastClose = closes[count - 1];
        decimal firstProbe = lastClose == 0m ? -1m : lastClose * 0.5m;
        decimal secondProbe = lastClose == 0m ? 1m : lastClose * 1.5m;
        decimal firstHistogram = ComputeNextBarHistogram(closes, parameters, firstProbe);
        decimal secondHistogram = ComputeNextBarHistogram(closes, parameters, secondProbe);
        decimal slope = secondHistogram - firstHistogram;
        return slope == 0m ? lastClose : firstProbe - firstHistogram * (secondProbe - firstProbe) / slope;
    }

    private static decimal ComputeNextBarHistogram(IReadOnlyList<decimal> closes, TrendDetectionParameters parameters, decimal nextClose)
    {
        var extended = new decimal[closes.Count + 1];
        for (int i = 0; i < closes.Count; i++) extended[i] = closes[i];
        extended[closes.Count] = nextClose;
        return ComputeHistogram(extended, parameters)[^1];
    }
}
