using KrakenReact.Server.Models;

namespace KrakenReact.Server.Services;

public static class CorrelationMath
{
    /// <summary>Fewest days two series must share before a correlation is worth reporting.</summary>
    public const int MinimumOverlap = 5;

    /// <summary>
    /// Daily returns keyed by the day they end on. A return is only recorded when the previous candle is exactly the day before:
    /// across a missing day the change would cover two days and would not be comparable with another pair's one-day change.
    /// </summary>
    public static Dictionary<DateTime, double> DailyReturns(IReadOnlyList<DerivedKline> ordered)
    {
        var result = new Dictionary<DateTime, double>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = (double)ordered[i - 1].Close;
            var curr = (double)ordered[i].Close;
            if (prev <= 0) continue;
            var day = ordered[i].OpenTime.Date;
            if ((day - ordered[i - 1].OpenTime.Date).TotalDays != 1) continue;
            result[day] = (curr - prev) / prev;
        }
        return result;
    }

    /// <summary>Pearson correlation over the days both series have. Zero when they share too few days or one is flat.</summary>
    public static double Pearson(IReadOnlyDictionary<DateTime, double> a, IReadOnlyDictionary<DateTime, double> b)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var (day, x) in a)
            if (b.TryGetValue(day, out var y)) { xs.Add(x); ys.Add(y); }

        var n = xs.Count;
        if (n < MinimumOverlap) return 0;

        double xMean = xs.Average(), yMean = ys.Average();
        double num = 0, denX = 0, denY = 0;
        for (var i = 0; i < n; i++)
        {
            double dx = xs[i] - xMean, dy = ys[i] - yMean;
            num += dx * dy;
            denX += dx * dx;
            denY += dy * dy;
        }
        var den = Math.Sqrt(denX * denY);
        return den < 1e-10 ? 0 : Math.Round(num / den, 4);
    }
}
