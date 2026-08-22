namespace KrakenReact.Server.Analysis;

/// <summary>
/// Weighs a handful of readings into a single probability, with the weights learned from outcomes
/// already known rather than chosen by hand.
/// <para>
/// The model is ordinary logistic regression in plain managed code, deliberately: it is a few numbers
/// and a dot product, so the fitted judgement can be read, argued with and written down, which the
/// gradient-boosted models elsewhere in this app cannot be. It knows nothing about what the readings
/// mean — each caller decides what its readings are and what outcome it is predicting.
/// </para>
/// </summary>
public class LogisticJudge
{
    private readonly double[] _weights;
    private readonly double[] _means;
    private readonly double[] _deviations;
    private readonly double _intercept;

    public LogisticJudge(double[] weights, double intercept, double[] means, double[] deviations)
    {
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
        _means = means ?? throw new ArgumentNullException(nameof(means));
        _deviations = deviations ?? throw new ArgumentNullException(nameof(deviations));
        _intercept = intercept;
    }

    /// <summary>The weight on each standardised reading, in the order the readings are built.</summary>
    public IReadOnlyList<double> Weights => _weights;

    /// <summary>The constant term, which sets the judgement made when every reading sits at its average.</summary>
    public double Intercept => _intercept;

    /// <summary>The average of each reading over the data the weights were fitted on.</summary>
    public IReadOnlyList<double> Means => _means;

    /// <summary>The spread of each reading over that same data, which the reading is divided by.</summary>
    public IReadOnlyList<double> Deviations => _deviations;

    /// <summary>Weighs one set of readings into a probability between zero and one.</summary>
    public decimal Judge(double[] features)
    {
        if (features == null || features.Length != _weights.Length) return 0m;
        double sum = _intercept;
        for (int i = 0; i < features.Length; i++) sum += _weights[i] * ((features[i] - _means[i]) / _deviations[i]);
        return (decimal)(1d / (1d + Math.Exp(-Math.Clamp(sum, -40d, 40d))));
    }

    /// <summary>
    /// Fits weights to readings whose outcome is already known, by batch gradient descent with a fixed
    /// number of passes and no randomness anywhere, so the same data always produces the same weights.
    /// Readings are standardised first because they arrive in wildly different units and one learning
    /// rate cannot suit both a count of minutes and a slope in ranges per hour.
    /// </summary>
    /// <returns>The fitted judgement, or null when there is nothing to fit on.</returns>
    public static LogisticJudge? Fit(double[][] rows, double[] targets, int passes = 300, double learningRate = 0.5d)
    {
        if (rows == null || targets == null || rows.Length == 0 || rows.Length != targets.Length) return null;
        int count = rows.Length, featureCount = rows[0].Length;
        if (featureCount == 0) return null;

        var means = new double[featureCount];
        var deviations = new double[featureCount];
        for (int f = 0; f < featureCount; f++)
        {
            double sum = 0d;
            for (int i = 0; i < count; i++) sum += rows[i][f];
            means[f] = sum / count;
            double squared = 0d;
            for (int i = 0; i < count; i++) { double d = rows[i][f] - means[f]; squared += d * d; }
            deviations[f] = Math.Max(1e-9d, Math.Sqrt(squared / count));
        }

        var standardised = new double[count][];
        for (int i = 0; i < count; i++)
        {
            standardised[i] = new double[featureCount];
            for (int f = 0; f < featureCount; f++) standardised[i][f] = (rows[i][f] - means[f]) / deviations[f];
        }

        var weights = new double[featureCount];
        double intercept = 0d;
        for (int pass = 0; pass < passes; pass++)
        {
            var gradient = new double[featureCount];
            double interceptGradient = 0d;
            for (int i = 0; i < count; i++)
            {
                double sum = intercept;
                for (int f = 0; f < featureCount; f++) sum += weights[f] * standardised[i][f];
                double error = 1d / (1d + Math.Exp(-Math.Clamp(sum, -40d, 40d))) - targets[i];
                interceptGradient += error;
                for (int f = 0; f < featureCount; f++) gradient[f] += error * standardised[i][f];
            }
            intercept -= learningRate * interceptGradient / count;
            for (int f = 0; f < featureCount; f++) weights[f] -= learningRate * gradient[f] / count;
        }
        return new LogisticJudge(weights, intercept, means, deviations);
    }

    /// <summary>
    /// Measures how well the judgement orders the observations it is shown, as the chance that an
    /// observation whose outcome happened is judged more likely than one whose did not — the area
    /// under the ROC curve. A judgement that knows nothing scores a half.
    /// <para>
    /// This is reported rather than the share it gets right, because a judgement can be right most of
    /// the time by always answering with the base rate and still be worthless.
    /// </para>
    /// </summary>
    public decimal MeasureOrdering(IReadOnlyList<double[]> rows, IReadOnlyList<bool> positives)
    {
        if (rows == null || positives == null || rows.Count == 0 || rows.Count != positives.Count) return 0m;

        var scored = new (decimal Score, bool Positive)[rows.Count];
        for (int i = 0; i < rows.Count; i++) scored[i] = (Judge(rows[i]), positives[i]);
        Array.Sort(scored, (a, b) => a.Score.CompareTo(b.Score));

        long positiveCount = 0;
        foreach (var entry in scored) if (entry.Positive) positiveCount++;
        long negativeCount = scored.Length - positiveCount;
        if (positiveCount == 0 || negativeCount == 0) return 0m;

        // The rank sum of the positives gives the score directly, avoiding an all-pairs comparison.
        // Ties share the average of the ranks they span: the judgement expressed no preference between
        // them, and counting one above the other would credit it with an ordering it did not make.
        // Without this, a judgement that says the same thing about everything scores anything but the
        // half it has earned.
        double rankSum = 0d;
        for (int i = 0; i < scored.Length;)
        {
            int j = i;
            while (j + 1 < scored.Length && scored[j + 1].Score == scored[i].Score) j++;
            double sharedRank = (i + 1 + j + 1) / 2d;
            for (int k = i; k <= j; k++) if (scored[k].Positive) rankSum += sharedRank;
            i = j + 1;
        }
        return (decimal)((rankSum - positiveCount * (positiveCount + 1d) / 2d) / ((double)positiveCount * negativeCount));
    }
}
