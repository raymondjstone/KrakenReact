namespace KrakenReact.Server.Analysis;

/// <summary>
/// Judges whether a fall has finished falling, from readings taken while it is still going.
/// <para>
/// The weights below were fitted on minute-resolution history in the source study, on readings taken
/// before its holdout date only, and are carried here verbatim so this app reproduces that judgement
/// rather than inventing its own. As measured there: it ordered the holdout at <b>0.723</b> against
/// 0.500 for knowing nothing.
/// </para>
/// <para>
/// That number is a claim inherited from elsewhere, not one this codebase has verified. It should be
/// re-measured against this account's own falls once enough minute history exists — see
/// <see cref="MeasureOrdering"/>.
/// </para>
/// </summary>
public sealed class PlummetBottomJudge
{
    public const int FeatureCount = 9;

    private static readonly double[] LearnedWeights = [0.3419615d, 0.2950855d, 0.1376415d, 0.0772287d, -0.1060436d, 0.0449842d, -0.0423225d, 0.2869773d, -0.2280733d];
    private static readonly double[] LearnedMeans = [0.0524319d, 2.5490237d, 3.8594367d, 0.8032648d, -0.3929675d, 0.4825632d, 0.4509788d, 1.1027338d, 4.3657316d];
    private static readonly double[] LearnedDeviations = [0.0951708d, 1.2357515d, 1.5113655d, 0.6964551d, 4.4358140d, 0.4996959d, 0.4975911d, 0.4374277d, 1.3121023d];
    private const double LearnedIntercept = -1.8270291d;

    /// <summary>The ordering score the source study measured on its own holdout.</summary>
    public const decimal InheritedHoldoutOrdering = 0.723m;

    private readonly LogisticJudge _judge;

    private PlummetBottomJudge(LogisticJudge judge) => _judge = judge;

    public PlummetBottomJudge(double[] weights, double intercept, double[] means, double[] deviations)
        => _judge = new LogisticJudge(weights, intercept, means, deviations);

    /// <summary>The judgement exactly as it was fitted in the source study.</summary>
    public static PlummetBottomJudge Learned() => new(new LogisticJudge(
        (double[])LearnedWeights.Clone(), LearnedIntercept,
        (double[])LearnedMeans.Clone(), (double[])LearnedDeviations.Clone()));

    public LogisticJudge Model => _judge;
    public IReadOnlyList<double> Weights => _judge.Weights;
    public double Intercept => _judge.Intercept;

    public static IReadOnlyList<string> FeatureNames =>
    [
        "how far it has fallen below the trigger",
        "how far it stands above the running low",
        "how long since the last new low",
        "how briskly it is trading",
        "the half hour slope",
        "making higher lows",
        "the high came after the low",
        "the fall against the pair's normal range",
        "how long since the trigger",
    ];

    /// <summary>
    /// Builds the readings in the order the weights expect.
    /// <para>
    /// Most are log-compressed. These quantities are heavily skewed — a volume multiple can be 1 or
    /// 400 — and on a raw scale one extreme observation would dominate the fit.
    /// </para>
    /// </summary>
    public static double[] BuildFeatures(PlummetBottomObservation o)
    {
        if (o is null) return new double[FeatureCount];
        return
        [
            Math.Log(1d + Math.Max(0d, (double)o.FallenBelowTheTriggerInDrops)),
            Math.Log(1d + Math.Max(0d, (double)o.AboveTheRunningLowInRanges)),
            Math.Log(1d + Math.Max(0, o.MinutesSinceTheLastNewLow)),
            Math.Log(1d + Math.Max(0d, (double)o.RecentVolumeMultiple)),
            Math.Clamp((double)o.SlopeRangesPerHour, -5d, 5d),
            o.IsMakingHigherLows ? 1d : 0d,
            o.TheHighCameAfterTheLow ? 1d : 0d,
            Math.Log(1d + Math.Max(0d, (double)o.FallAgainstTheBaselineRange)),
            Math.Log(1d + Math.Max(0, o.MinutesSinceTheTrigger)),
        ];
    }

    /// <summary>The probability that the falling is over, from readings taken now.</summary>
    public decimal JudgeTheFallingIsFinished(PlummetBottomObservation observation) =>
        _judge.Judge(BuildFeatures(observation));

    /// <inheritdoc cref="JudgeTheFallingIsFinished(PlummetBottomObservation)"/>
    public decimal JudgeTheFallingIsFinished(double[] features) => _judge.Judge(features);

    /// <summary>Refits the judgement on observations whose outcome is known.</summary>
    public static PlummetBottomJudge? Fit(IReadOnlyList<PlummetBottomObservation> observations, Func<PlummetBottomObservation, bool> label)
    {
        if (observations is null || observations.Count == 0 || label is null) return null;

        var rows = new double[observations.Count][];
        var targets = new double[observations.Count];
        for (int i = 0; i < observations.Count; i++)
        {
            rows[i] = BuildFeatures(observations[i]);
            targets[i] = label(observations[i]) ? 1d : 0d;
        }

        var fitted = LogisticJudge.Fit(rows, targets);
        return fitted is null ? null : new PlummetBottomJudge(fitted);
    }

    /// <summary>How well the judgement orders observations it was not fitted on.</summary>
    public decimal MeasureOrdering(IReadOnlyList<PlummetBottomObservation> observations, Func<PlummetBottomObservation, bool> label)
    {
        if (observations is null || observations.Count == 0 || label is null) return 0m;

        var rows = new double[observations.Count][];
        var positives = new bool[observations.Count];
        for (int i = 0; i < observations.Count; i++)
        {
            rows[i] = BuildFeatures(observations[i]);
            positives[i] = label(observations[i]);
        }
        return _judge.MeasureOrdering(rows, positives);
    }
}

/// <summary>
/// Judges whether a rebound in progress is still alive, or has already made its high.
/// <para>
/// Unlike <see cref="PlummetBottomJudge"/> this one carries <b>no fitted weights</b> — the source
/// study never wrote a learned set down for it. It must be fitted on real observations before it
/// says anything meaningful, which is why there is no <c>Learned()</c> here to reach for by mistake.
/// </para>
/// </summary>
public sealed class PlummetRiseJudge
{
    public const int FeatureCount = 9;

    private readonly LogisticJudge _judge;

    private PlummetRiseJudge(LogisticJudge judge) => _judge = judge;

    public PlummetRiseJudge(double[] weights, double intercept, double[] means, double[] deviations)
        => _judge = new LogisticJudge(weights, intercept, means, deviations);

    public LogisticJudge Model => _judge;
    public IReadOnlyList<double> Weights => _judge.Weights;
    public double Intercept => _judge.Intercept;

    public static IReadOnlyList<string> FeatureNames =>
    [
        "how far below the peak, in ranges",
        "how far below the peak, as a share of the gain",
        "how long off the peak",
        "this fall against the deepest already survived",
        "how far the rebound has run",
        "how briskly it is trading",
        "the two hour slope",
        "making higher lows",
        "the high came after the low",
    ];

    public static double[] BuildFeatures(PlummetRiseObservation o)
    {
        if (o is null) return new double[FeatureCount];

        double drawdown = Math.Max(0d, (double)o.DrawdownInRanges);
        // A floor on the denominator: a rebound that has survived nothing yet would otherwise divide
        // by zero and report an infinite ratio on its first wobble.
        double deepest = Math.Max(0.1d, (double)o.DeepestFallAlreadySurvivedInRanges);

        return
        [
            Math.Log(1d + drawdown),
            Math.Clamp((double)o.DrawdownShareOfTheGain, 0d, 2d),
            Math.Log(1d + Math.Max(0, o.MinutesSinceThePeak)),
            Math.Log(1d + drawdown / deepest),
            Math.Log(1d + Math.Max(0d, (double)o.GainSoFarInDrops)),
            Math.Log(1d + Math.Max(0d, (double)o.RecentVolumeMultiple)),
            Math.Clamp((double)o.SlopeRangesPerHourOverTwoHours, -5d, 5d),
            o.IsMakingHigherLows ? 1d : 0d,
            o.TheHighCameAfterTheLow ? 1d : 0d,
        ];
    }

    /// <summary>The probability that the rise has further to run.</summary>
    public decimal JudgeTheRiseIsAlive(PlummetRiseObservation observation) => _judge.Judge(BuildFeatures(observation));

    /// <inheritdoc cref="JudgeTheRiseIsAlive(PlummetRiseObservation)"/>
    public decimal JudgeTheRiseIsAlive(double[] features) => _judge.Judge(features);

    public static PlummetRiseJudge? Fit(
        IReadOnlyList<PlummetRiseObservation> observations,
        Func<PlummetRiseObservation, bool> label,
        int passes = 300,
        double learningRate = 0.5d)
    {
        if (observations is null || observations.Count == 0 || label is null) return null;

        var rows = new double[observations.Count][];
        var targets = new double[observations.Count];
        for (int i = 0; i < observations.Count; i++)
        {
            rows[i] = BuildFeatures(observations[i]);
            targets[i] = label(observations[i]) ? 1d : 0d;
        }

        var fitted = LogisticJudge.Fit(rows, targets, passes, learningRate);
        return fitted is null ? null : new PlummetRiseJudge(fitted);
    }

    public decimal MeasureOrdering(IReadOnlyList<PlummetRiseObservation> observations, Func<PlummetRiseObservation, bool> label)
    {
        if (observations is null || observations.Count == 0 || label is null) return 0m;

        var rows = new double[observations.Count][];
        var positives = new bool[observations.Count];
        for (int i = 0; i < observations.Count; i++)
        {
            rows[i] = BuildFeatures(observations[i]);
            positives[i] = label(observations[i]);
        }
        return _judge.MeasureOrdering(rows, positives);
    }
}
