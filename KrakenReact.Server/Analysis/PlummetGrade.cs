namespace KrakenReact.Server.Analysis;

/// <summary>
/// Grades a detected fall on how bad it looks, using a model already fitted elsewhere.
/// <para>
/// The coefficients below are not derived here and must not be re-tuned casually: they were fitted
/// on minute-resolution history and are carried over verbatim so this app reproduces the same
/// judgement rather than inventing its own. A higher grade means a worse-looking setup, which is why
/// <see cref="StakeMultiplier"/> sizes on <c>1 - grade</c>.
/// </para>
/// <para>
/// <b>This only works on one-minute candles.</b> The features are measured against hourly bars built
/// by folding sixty candles together, so handing it hourly or daily input silently measures
/// something else entirely. <see cref="GradeSeries"/> refuses anything but minute spacing.
/// </para>
/// </summary>
public static class PlummetGrade
{
    /// <summary>The readings the model weighs, in the order it expects them.</summary>
    public static IReadOnlyList<string> FeatureNames =>
    [
        "DropFraction",
        "PriorHourlyAtrFraction",
        "DropVsHourlyAtrMultiple",
        "EmptyBaselineCandleShare",
        "BaselineValuePerCandleUsd",
        "FallVolumeConcentration",
        "FallOverBaselineVolume",
    ];

    // Fitted coefficients, carried across unchanged. Readings are clipped to the range seen during
    // fitting before being standardised, so an extreme value cannot drag the result somewhere the
    // model was never trained to go.
    private static readonly double[] LowerClips = [0.1, 0.00141629685582098, 0.9620253164556962, 0.016666666666666666, 0, 0.09253141652308836, 0.10480683193640757];
    private static readonly double[] UpperClips = [0.23333333333333334, 0.14500929121316697, 101.15, 1, 10177.273537081277, 1, 3490.2492760129853];
    private static readonly double[] Means = [0.12136468096169185, 0.0272423765314121, 9.84160488401445, 0.8147045477149056, 371.54205801524154, 0.5350807827126999, 98.6534545699509];
    private static readonly double[] Deviations = [0.02558695529102066, 0.02566400344725276, 13.1380003329941, 0.22984588514181017, 1321.8310464559038, 0.2972566289179075, 431.42352565267413];
    private static readonly double[] Weights = [-0.18131489449365532, 0.24455437038088784, -0.037330376133081905, -0.03147798906659703, -0.047761281296564276, -0.18462446303189384, 0.037795586256595855];
    private const double Bias = 0.28485547879296624;

    private const int HourlyAtrPeriod = 14;
    private const int MinutesPerHour = 60;

    /// <summary>The window the fall is measured over, in minutes, as the model was fitted.</summary>
    public const int DropWindowMinutes = 120;

    /// <summary>The quiet period before the fall the model compares it against, in minutes.</summary>
    public const int BaselineWindowMinutes = 240;

    /// <summary>Folds minute candles into hourly bars, aligned to where the series happens to start.</summary>
    public static List<AnalysisCandle> BuildHourlyBars(IReadOnlyList<AnalysisCandle> candles, int firstMinuteOffset)
    {
        var bars = new List<AnalysisCandle>((candles.Count + firstMinuteOffset) / MinutesPerHour + 1);
        int currentBar = -1;
        DateTime openTime = default;
        decimal open = 0m, high = 0m, low = 0m, close = 0m, volume = 0m;
        int tradeCount = 0;

        void Flush()
        {
            if (currentBar >= 0) bars.Add(new AnalysisCandle(openTime, open, high, low, close, volume, tradeCount));
        }

        for (int i = 0; i < candles.Count; i++)
        {
            int bar = (i + firstMinuteOffset) / MinutesPerHour;
            if (bar != currentBar)
            {
                Flush();
                currentBar = bar;
                var c = candles[i];
                (openTime, open, high, low, close, volume, tradeCount) = (c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.TradeCount);
            }
            else
            {
                var c = candles[i];
                if (c.High > high) high = c.High;
                if (c.Low < low) low = c.Low;
                close = c.Close;
                volume += c.Volume;
                tradeCount += c.TradeCount;
            }
        }
        Flush();
        return bars;
    }

    /// <summary>
    /// Builds the readings for one fall, or null when the series does not reach back far enough to
    /// measure them.
    /// </summary>
    public static double[]? ReadFeatures(
        IReadOnlyList<AnalysisCandle> candles,
        int triggerIndex,
        int referenceHighIndex,
        decimal referenceHigh,
        IReadOnlyList<AnalysisCandle> hourlyBars,
        IReadOnlyList<decimal> hourlyAtr,
        int firstMinuteOffset,
        int dropWindowMinutes,
        int baselineWindowMinutes,
        int intervalMinutes)
    {
        if (triggerIndex < 0 || triggerIndex >= candles.Count || referenceHighIndex < 0 || referenceHigh <= 0m || intervalMinutes <= 0) return null;

        int windowCandles = dropWindowMinutes / intervalMinutes;
        int baselineCandles = baselineWindowMinutes / intervalMinutes;
        int baselineEnd = triggerIndex - windowCandles;
        int baselineStart = baselineEnd - baselineCandles + 1;
        if (baselineStart < 0 || baselineEnd < baselineStart) return null;

        decimal referenceLow = candles[triggerIndex].Low;
        decimal dropDistance = referenceHigh - referenceLow;
        if (dropDistance <= 0m || referenceLow <= 0m) return null;

        var fall = Accumulate(candles, Math.Min(referenceHighIndex + 1, triggerIndex), triggerIndex);
        var baseline = Accumulate(candles, baselineStart, baselineEnd);
        if (fall.Count == 0 || baseline.Count == 0) return null;

        int emptyBaselineCandles = 0;
        for (int j = baselineStart; j <= baselineEnd; j++)
            if (candles[j].Volume <= 0m) emptyBaselineCandles++;

        double baselineVolume = Ratio(baseline.Volume, baseline.Count);
        double fallVolume = Ratio(fall.Volume, fall.Count);

        // Volatility is read from the hourly bar *before* the fall began, so the fall itself cannot
        // inflate the very measure it is being judged against.
        int atrBar = (triggerIndex - windowCandles + firstMinuteOffset) / MinutesPerHour - 1;
        double priorAtrFraction = double.NaN, dropVsAtr = double.NaN;
        if (atrBar >= HourlyAtrPeriod && atrBar < hourlyBars.Count && atrBar < hourlyAtr.Count &&
            hourlyBars[atrBar].Close > 0m && hourlyAtr[atrBar] > 0m)
        {
            priorAtrFraction = (double)(hourlyAtr[atrBar] / hourlyBars[atrBar].Close);
            dropVsAtr = (double)(dropDistance / hourlyAtr[atrBar]);
        }

        return
        [
            Ratio((double)dropDistance, (double)referenceHigh),
            priorAtrFraction,
            dropVsAtr,
            Ratio(emptyBaselineCandles, baseline.Count),
            baselineVolume * (double)candles[baselineEnd].Close,
            Ratio(fall.MaximumVolume, fall.Volume),
            Ratio(fallVolume, baselineVolume),
        ];
    }

    /// <summary>
    /// Grades the fall ending at the last candle of the series.
    /// </summary>
    /// <returns>A grade from 0 to 1, or null when the series is unusable or not minute-spaced.</returns>
    public static double? GradeSeries(IReadOnlyList<AnalysisCandle> candles, decimal referenceHigh, DateTime referenceHighTime)
    {
        if (candles.Count < 2) return null;

        int intervalMinutes = (int)Math.Round((candles[^1].OpenTime - candles[^2].OpenTime).TotalMinutes);
        // Anything but minute bars would make the sixty-fold hourly aggregation measure the wrong
        // span, and the model would be reading numbers that mean nothing.
        if (intervalMinutes != 1) return null;

        int triggerIndex = candles.Count - 1;
        int referenceHighIndex = -1;
        for (int i = triggerIndex; i >= 0; i--)
            if (candles[i].OpenTime <= referenceHighTime) { referenceHighIndex = i; break; }
        if (referenceHighIndex < 0) return null;

        int firstMinuteOffset = candles[0].OpenTime.Minute;
        var hourlyBars = BuildHourlyBars(candles, firstMinuteOffset);
        var hourlyAtr = Indicators.AverageTrueRange(hourlyBars, HourlyAtrPeriod);

        var features = ReadFeatures(
            candles, triggerIndex, referenceHighIndex, referenceHigh,
            hourlyBars, hourlyAtr, firstMinuteOffset,
            DropWindowMinutes, BaselineWindowMinutes, intervalMinutes);

        return features is null ? null : Grade(features);
    }

    /// <summary>Weighs the readings into a grade from 0 to 1, where higher is a worse-looking fall.</summary>
    public static double Grade(double[] features)
    {
        double z = Bias;
        for (int c = 0; c < Weights.Length; c++)
        {
            double value = features[c];
            // A reading the series could not supply is simply not weighed, rather than poisoning the
            // whole grade with a NaN.
            if (double.IsNaN(value) || double.IsInfinity(value)) continue;
            z += Weights[c] * (Math.Clamp(value, LowerClips[c], UpperClips[c]) - Means[c]) / Deviations[c];
        }
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    /// <summary>
    /// Turns a grade into a position-size multiplier: worse-looking falls get less money.
    /// <para>
    /// The tilt sets how hard the opinion bites — zero stakes every trade the same, one scales
    /// linearly, and larger values pull back sharply on anything but the cleanest setups.
    /// </para>
    /// </summary>
    public static decimal StakeMultiplier(double grade, double tilt) =>
        tilt <= 0 ? 1m : (decimal)Math.Pow(Math.Clamp(1.0 - grade, 0.0, 1.0), tilt);

    private static (double Volume, double MaximumVolume, int Count) Accumulate(IReadOnlyList<AnalysisCandle> candles, int from, int to)
    {
        double volume = 0, maximumVolume = -1;
        int count = 0;
        for (int j = Math.Max(0, from); j <= Math.Min(to, candles.Count - 1); j++)
        {
            double candleVolume = (double)candles[j].Volume;
            volume += candleVolume;
            if (candleVolume > maximumVolume) maximumVolume = candleVolume;
            count++;
        }
        return (volume, Math.Max(0, maximumVolume), count);
    }

    private static double Ratio(double numerator, double denominator) =>
        denominator == 0 || double.IsNaN(denominator) ? double.NaN : numerator / denominator;
}
