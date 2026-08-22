namespace KrakenReact.Server.Analysis;

/// <summary>
/// Turns a detected plummet into the readings a <see cref="LogisticJudge"/> can weigh, and fits one
/// against a market's own history to answer: given a fall of this shape, in this market, how often did
/// the price go on to recover half of it?
/// <para>
/// Every reading is knowable at the moment of the low. Nothing here looks forward, because a judgement
/// fitted on what only hindsight could supply reports an accuracy it will never reproduce live.
/// </para>
/// </summary>
public static class ReboundJudge
{
    /// <summary>The share of the fall a rebound must recover to count as a success.</summary>
    public const decimal SuccessReboundFraction = 0.50m;

    public static IReadOnlyList<string> FeatureNames =>
    [
        "how far it fell",
        "how quiet the market was before",
        "how heavily it traded on the way down",
        "which way the window was already going",
        "whether the low was only a wick",
        "how far the high stood above the running average",
        "how far the low sat below that average",
        "how many falls this market had already had",
    ];

    /// <summary>Builds the readings for one event, in the order <see cref="FeatureNames"/> lists them.</summary>
    public static double[] BuildFeatures(PlummetEvent e, int priorEventCount) =>
    [
        Math.Log(1d + Math.Max(0d, (double)e.DropFraction) * 10d),
        Math.Log(1d + Math.Max(0d, (double)e.BaselineRangeFraction) * 10d),
        Math.Log(1d + Math.Max(0d, (double)e.VolumeRatio)),
        Math.Clamp((double)e.OwnWindowChangeFraction, -1d, 1d),
        e.IsWick ? 1d : 0d,
        Math.Clamp(e.AveragePrice > 0m ? (double)(e.ReferenceHigh / e.AveragePrice) - 1d : 0d, -1d, 2d),
        Math.Clamp(e.EventLow > 0m ? (double)(e.AveragePrice / e.EventLow) - 1d : 0d, -1d, 2d),
        Math.Log(1d + Math.Max(0, priorEventCount)),
    ];

    /// <summary>Whether the event's rebound actually reached the success threshold.</summary>
    public static bool Succeeded(PlummetEvent e) => e.MaximumReboundFraction >= SuccessReboundFraction;

    /// <summary>The fewest of each outcome the test set needs before its ordering score means anything.</summary>
    public const int ReliableTestClassCount = 5;

    /// <summary>The outcome of fitting a judge to one market's plummet history.</summary>
    public sealed record Fit(
        LogisticJudge Judge,
        int TrainCount,
        int TestCount,
        decimal TrainSuccessRate,
        decimal TestSuccessRate,
        decimal TestOrdering,
        int TestPositiveCount,
        int TestNegativeCount)
    {
        /// <summary>
        /// Whether the ordering score is worth reading. An AUC computed against one or two examples
        /// of an outcome is arithmetic, not evidence: it lands on 1.000 or 0.000 on the strength of a
        /// single case and reads as authority it has not earned.
        /// </summary>
        public bool IsReliable =>
            Math.Min(TestPositiveCount, TestNegativeCount) >= ReliableTestClassCount;
    }

    /// <summary>
    /// Fits a judge on the earlier events and measures it on the later ones. The split is by time
    /// rather than at random: a model chosen on shuffled market data has seen the future of every
    /// example it is tested on, and flatters itself accordingly.
    /// </summary>
    /// <param name="settledEvents">Events whose rebound window is fully contained in the data.</param>
    /// <param name="minimumEvents">The fewest events worth fitting on at all.</param>
    /// <returns>The fit, or null when there is too little history or one class is missing.</returns>
    public static Fit? FitFromHistory(IReadOnlyList<PlummetEvent> settledEvents, int minimumEvents = 20)
    {
        if (settledEvents == null || settledEvents.Count < minimumEvents) return null;

        var ordered = settledEvents.OrderBy(e => e.EventLowTime).ToList();
        int splitIndex = (int)(ordered.Count * 0.7);
        if (splitIndex < 8 || ordered.Count - splitIndex < 4) return null;

        var trainRows = new double[splitIndex][];
        var trainTargets = new double[splitIndex];
        for (int i = 0; i < splitIndex; i++)
        {
            trainRows[i] = BuildFeatures(ordered[i], i);
            trainTargets[i] = Succeeded(ordered[i]) ? 1d : 0d;
        }

        // A single-class training set has nothing to separate; gradient descent would just learn the
        // base rate and report an ordering score of zero, which reads as "wrong" rather than "silent".
        if (trainTargets.All(t => t == 0d) || trainTargets.All(t => t == 1d)) return null;

        var judge = LogisticJudge.Fit(trainRows, trainTargets);
        if (judge == null) return null;

        var testRows = new List<double[]>();
        var testPositives = new List<bool>();
        for (int i = splitIndex; i < ordered.Count; i++)
        {
            testRows.Add(BuildFeatures(ordered[i], i));
            testPositives.Add(Succeeded(ordered[i]));
        }

        int testPositiveCount = testPositives.Count(p => p);

        return new Fit(
            judge,
            splitIndex,
            testRows.Count,
            (decimal)trainTargets.Count(t => t == 1d) / splitIndex,
            testPositives.Count == 0 ? 0m : (decimal)testPositiveCount / testPositives.Count,
            judge.MeasureOrdering(testRows, testPositives),
            testPositiveCount,
            testPositives.Count - testPositiveCount);
    }
}
