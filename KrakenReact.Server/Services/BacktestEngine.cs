using KrakenReact.Server.Models;

namespace KrakenReact.Server.Services;

public sealed record BacktestTrade(DateTime EntryDate, decimal EntryPrice, DateTime ExitDate, decimal ExitPrice, decimal PlPct, decimal CashAfter);

/// <param name="FinalValue">Cash plus any still-open position valued at the last close.</param>
/// <param name="OpenPosition">True when the run ended while holding - its result is not in <see cref="Trades"/>.</param>
public sealed record BacktestResult(List<BacktestTrade> Trades, decimal FinalValue, bool OpenPosition);

/// <summary>
/// Simulates the auto-trade rule on daily candles: buy when the close is below the average of the previous 7 closes, sell when
/// it is back at or above it, all-in each time. One implementation serves both the plain backtest and each walk-forward window,
/// so the two cannot drift apart.
/// </summary>
public static class BacktestEngine
{
    public const decimal StartingCash = 10000m;
    public const int AverageWindow = 7;

    /// <summary>Runs over candles [from, to). <paramref name="feePct"/> is charged on every buy and every sell.</summary>
    public static BacktestResult Run(IReadOnlyList<DerivedKline> klines, int from, int to, decimal feePct)
    {
        var keep = 1m - Math.Clamp(feePct, 0m, 50m) / 100m;
        var trades = new List<BacktestTrade>();
        var cash = StartingCash;
        var position = 0m;
        decimal? entryPrice = null;
        DateTime entryDate = default;
        var cashBeforeBuy = 0m;
        to = Math.Min(to, klines.Count);

        for (var i = Math.Max(from, 0); i < to; i++)
        {
            var close = klines[i].Close;
            var start = Math.Max(0, i - AverageWindow);
            var sum = 0m;
            var n = 0;
            for (var j = start; j < Math.Min(start + AverageWindow, klines.Count); j++) { sum += klines[j].Close; n++; }
            var avg = n > 0 ? sum / n : 0m;
            var ratio = avg > 0 ? close * 100 / avg : 100m;

            if (entryPrice == null && close > 0 && ratio < 100m && cash > 0)
            {
                position = cash * keep / close;
                entryPrice = close;
                entryDate = klines[i].OpenTime;
                cashBeforeBuy = cash;
                cash = 0m;
            }
            else if (entryPrice != null && ratio >= 100m)
            {
                cash = position * close * keep;
                trades.Add(new BacktestTrade(entryDate, entryPrice.Value, klines[i].OpenTime, close,
                    Math.Round((cash / cashBeforeBuy - 1m) * 100m, 2), Math.Round(cash, 2)));
                position = 0m;
                entryPrice = null;
            }
        }

        var lastClose = to > 0 && to - 1 < klines.Count ? klines[to - 1].Close : 0m;
        return new BacktestResult(trades, cash + position * lastClose, entryPrice != null);
    }
}
