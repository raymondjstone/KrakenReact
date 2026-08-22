namespace KrakenReact.Server.Analysis;

/// <summary>
/// Deterministic, dependency-free technical indicators over a candle series.
/// Every routine returns one value per input bar so results line up with the series by index;
/// bars before an indicator has warmed up carry a neutral value rather than being omitted.
/// </summary>
public static class Indicators
{
    /// <summary>Exponential moving average, seeded with the first value.</summary>
    public static decimal[] ExponentialMovingAverage(IReadOnlyList<decimal> values, int period)
    {
        var averages = new decimal[values.Count];
        if (values.Count == 0) return averages;
        decimal multiplier = 2m / (period + 1);
        averages[0] = values[0];
        for (int i = 1; i < values.Count; i++)
            averages[i] = values[i] * multiplier + averages[i - 1] * (1m - multiplier);
        return averages;
    }

    /// <summary>
    /// Zero-lag exponential moving average: the ordinary EMA applied to lag-compensated values,
    /// so the average tracks turns in the data more closely at the cost of more whipsaw.
    /// </summary>
    public static decimal[] ZeroLagExponentialMovingAverage(IReadOnlyList<decimal> values, int period)
    {
        var adjusted = new decimal[values.Count];
        int lag = (period - 1) / 2;
        for (int i = 0; i < values.Count; i++)
            adjusted[i] = 2m * values[i] - values[Math.Max(0, i - lag)];
        return ExponentialMovingAverage(adjusted, period);
    }

    /// <summary>Simple moving average, with a running average before the first full period.</summary>
    public static decimal[] SimpleMovingAverage(IReadOnlyList<decimal> values, int period)
    {
        int count = values.Count;
        var result = new decimal[count];
        decimal sum = 0m;
        for (int i = 0; i < count; i++)
        {
            sum += values[i];
            if (i >= period) sum -= values[i - period];
            result[i] = sum / Math.Min(i + 1, period);
        }
        return result;
    }

    /// <summary>The MACD line and its signal line, from which the histogram is the difference.</summary>
    public static (decimal[] Macd, decimal[] Signal) MacdLineAndSignal(
        IReadOnlyList<decimal> values, int fastPeriod, int slowPeriod, int signalPeriod, bool useZeroLag)
    {
        int count = values.Count;
        var fast = useZeroLag ? ZeroLagExponentialMovingAverage(values, fastPeriod) : ExponentialMovingAverage(values, fastPeriod);
        var slow = useZeroLag ? ZeroLagExponentialMovingAverage(values, slowPeriod) : ExponentialMovingAverage(values, slowPeriod);
        var macd = new decimal[count];
        for (int i = 0; i < count; i++) macd[i] = fast[i] - slow[i];
        var signal = useZeroLag ? ZeroLagExponentialMovingAverage(macd, signalPeriod) : ExponentialMovingAverage(macd, signalPeriod);
        return (macd, signal);
    }

    /// <summary>The MACD histogram: the MACD line less its signal line.</summary>
    public static decimal[] MacdHistogram(
        IReadOnlyList<decimal> values, int fastPeriod, int slowPeriod, int signalPeriod, bool useZeroLag)
    {
        var (macd, signal) = MacdLineAndSignal(values, fastPeriod, slowPeriod, signalPeriod, useZeroLag);
        var histogram = new decimal[values.Count];
        for (int i = 0; i < values.Count; i++) histogram[i] = macd[i] - signal[i];
        return histogram;
    }

    /// <summary>Wilder's relative strength index from 0 to 100, neutral 50 before the first full period.</summary>
    public static decimal[] RelativeStrengthIndex(IReadOnlyList<decimal> values, int period)
    {
        var rsi = new decimal[values.Count];
        for (int i = 0; i < Math.Min(period, values.Count); i++) rsi[i] = 50m;
        if (values.Count <= period) return rsi;

        decimal averageGain = 0m, averageLoss = 0m;
        for (int i = 1; i <= period; i++)
        {
            decimal change = values[i] - values[i - 1];
            if (change > 0m) averageGain += change; else averageLoss -= change;
        }
        averageGain /= period;
        averageLoss /= period;
        rsi[period] = averageLoss == 0m ? 100m : 100m - 100m / (1m + averageGain / averageLoss);

        for (int i = period + 1; i < values.Count; i++)
        {
            decimal change = values[i] - values[i - 1];
            averageGain = (averageGain * (period - 1) + Math.Max(0m, change)) / period;
            averageLoss = (averageLoss * (period - 1) + Math.Max(0m, -change)) / period;
            rsi[i] = averageLoss == 0m ? 100m : 100m - 100m / (1m + averageGain / averageLoss);
        }
        return rsi;
    }

    /// <summary>Average true range as the simple moving average of true range, running before the first full period.</summary>
    public static decimal[] AverageTrueRange(IReadOnlyList<AnalysisCandle> candles, int period)
    {
        var atr = new decimal[candles.Count];
        if (candles.Count == 0) return atr;

        var trueRanges = new decimal[candles.Count];
        trueRanges[0] = candles[0].High - candles[0].Low;
        for (int i = 1; i < candles.Count; i++)
            trueRanges[i] = Math.Max(
                candles[i].High - candles[i].Low,
                Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close), Math.Abs(candles[i].Low - candles[i - 1].Close)));

        decimal runningSum = 0m;
        for (int i = 0; i < candles.Count; i++)
        {
            runningSum += trueRanges[i];
            if (i >= period) runningSum -= trueRanges[i - period];
            atr[i] = runningSum / Math.Min(i + 1, period);
        }
        return atr;
    }

    /// <summary>Rate of change over the period, as a percentage; zero before the period has elapsed.</summary>
    public static decimal[] RateOfChange(IReadOnlyList<decimal> values, int period)
    {
        var result = new decimal[values.Count];
        for (int i = period; i < values.Count; i++)
        {
            decimal previous = values[i - period];
            if (previous != 0m) result[i] = (values[i] - previous) / previous * 100m;
        }
        return result;
    }

    /// <summary>
    /// Rolling least-squares trend of the closes over the window: the slope as a fraction of the
    /// window's mean price per bar, and the R² that says how much of the movement that line explains.
    /// A steep slope with a low R² is noise; a modest slope with a high R² is a trend.
    /// </summary>
    public static (decimal[] Slope, decimal[] RSquared) RegressionTrend(IReadOnlyList<AnalysisCandle> candles, int windowBars)
    {
        int count = candles.Count;
        var slopes = new decimal[count];
        var rSquared = new decimal[count];
        if (windowBars < 3 || count < windowBars) return (slopes, rSquared);

        double[] sumY = new double[count + 1], sumIndexY = new double[count + 1], sumYY = new double[count + 1];
        for (int i = 0; i < count; i++)
        {
            double y = (double)candles[i].Close;
            sumY[i + 1] = sumY[i] + y;
            sumIndexY[i + 1] = sumIndexY[i] + i * y;
            sumYY[i + 1] = sumYY[i] + y * y;
        }

        double n = windowBars;
        double sumX = n * (n - 1) / 2;
        double sumXX = (n - 1) * n * (2 * n - 1) / 6;
        double denominatorX = n * sumXX - sumX * sumX;

        for (int i = windowBars - 1; i < count; i++)
        {
            int start = i - windowBars + 1;
            double totalY = sumY[i + 1] - sumY[start];
            double totalXY = sumIndexY[i + 1] - sumIndexY[start] - start * totalY;
            double totalYY = sumYY[i + 1] - sumYY[start];
            double covariance = n * totalXY - sumX * totalY;
            double denominatorY = n * totalYY - totalY * totalY;
            if (denominatorX <= 0 || denominatorY <= 0 || totalY <= 0) continue;
            slopes[i] = (decimal)(covariance / denominatorX / (totalY / n));
            rSquared[i] = (decimal)(covariance * covariance / (denominatorX * denominatorY));
        }
        return (slopes, rSquared);
    }

    /// <summary>The median of the values, averaging the middle pair when the count is even.</summary>
    public static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m;
    }

    /// <summary>The index of the last candle opening at or before the time, or -1 when none does.</summary>
    public static int FindLastIndexAtOrBefore(IReadOnlyList<AnalysisCandle> candles, DateTime time)
    {
        int low = 0, high = candles.Count - 1, result = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (candles[mid].OpenTime <= time) { result = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return result;
    }

    /// <summary>
    /// The fractional close-to-close change over the window ending at the time, or null when the
    /// series does not reach back far enough to measure it.
    /// </summary>
    public static decimal? WindowChangeFraction(IReadOnlyList<AnalysisCandle> candles, DateTime endTime, int windowMinutes)
    {
        if (candles.Count == 0) return null;
        DateTime startTime = endTime.AddMinutes(-windowMinutes);
        int endIndex = FindLastIndexAtOrBefore(candles, endTime);
        if (endIndex < 0 || candles[endIndex].OpenTime < startTime) return null;
        int startIndex = FindLastIndexAtOrBefore(candles, startTime);
        if (startIndex < 0) return null;
        decimal startClose = candles[startIndex].Close;
        return startClose > 0m ? (candles[endIndex].Close - startClose) / startClose : null;
    }

    /// <summary>Which regime a <see cref="TrendShield"/> lets a trade through in.</summary>
    public enum TrendShieldMode
    {
        /// <summary>Price above the average and the average rising — the strictest gate.</summary>
        PriceAboveRisingAverage,

        /// <summary>The average is rising, wherever price sits against it.</summary>
        AverageRising,

        /// <summary>Either above the average or the average rising — the loosest gate.</summary>
        NotBelowFallingAverage,

        /// <summary>The average is falling, for a rule that wants the downtrend rather than avoiding it.</summary>
        AverageFalling,
    }

    /// <summary>
    /// A per-bar regime filter: whether the longer trend was favourable at that bar.
    /// <para>
    /// A mean-reversion rule that buys every fall buys the whole way down a bear market. Gating it on
    /// the longer trend is the usual first thing that separates a rule with an edge from one that
    /// merely worked in a rising market — and the gate is computed only from bars up to and including
    /// the one being judged, so it can be applied live.
    /// </para>
    /// </summary>
    public static bool[] TrendShield(
        IReadOnlyList<AnalysisCandle> candles,
        int averagePeriodBars,
        int slopeLookbackBars,
        TrendShieldMode mode = TrendShieldMode.PriceAboveRisingAverage)
    {
        var shield = new bool[candles.Count];
        if (averagePeriodBars < 1 || slopeLookbackBars < 1) return shield;

        var average = ExponentialMovingAverage(candles.Select(c => c.Close).ToList(), averagePeriodBars);
        for (int i = Math.Max(averagePeriodBars, slopeLookbackBars); i < candles.Count; i++)
        {
            bool rising = average[i] > average[i - slopeLookbackBars];
            bool above = candles[i].Close > average[i];
            shield[i] = mode switch
            {
                TrendShieldMode.AverageRising => rising,
                TrendShieldMode.NotBelowFallingAverage => above || rising,
                TrendShieldMode.AverageFalling => average[i] < average[i - slopeLookbackBars],
                _ => above && rising,
            };
        }
        return shield;
    }

    /// <summary>The spacing between bars in minutes, inferred from the first gap in the series.</summary>
    public static int InferIntervalMinutes(IReadOnlyList<AnalysisCandle> candles)
    {
        for (int i = 1; i < candles.Count; i++)
        {
            int minutes = (int)(candles[i].OpenTime - candles[i - 1].OpenTime).TotalMinutes;
            if (minutes > 0) return minutes;
        }
        return 0;
    }
}
