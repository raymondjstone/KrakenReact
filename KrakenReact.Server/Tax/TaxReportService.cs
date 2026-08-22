using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Tax;

/// <summary>
/// Builds a UK tax report from the stored trades and ledger.
/// <para>
/// Two things happen here that the matcher itself knows nothing about: every amount is converted to
/// sterling at the rate on the day it happened, and a trade priced in a non-fiat quote currency is
/// split into two events, because swapping BTC for USDT disposes of the BTC *and* acquires the USDT.
/// </para>
/// </summary>
public class TaxReportService
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly TradingStateService _state;
    private readonly ILogger<TaxReportService> _logger;

    public TaxReportService(
        IDbContextFactory<KrakenDbContext> dbFactory,
        TradingStateService state,
        ILogger<TaxReportService> logger)
    {
        _dbFactory = dbFactory;
        _state = state;
        _logger = logger;
    }

    /// <summary>Loads the historical GBP rate table from the stored GBP/USD daily candles.</summary>
    public async Task<GbpRateTable> LoadRateTableAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var klines = await db.DerivedKlines
            .Where(k => k.Asset == GbpRateTable.RateSymbol && k.Interval == "OneDay" && k.Close > 0)
            .AsNoTracking()
            .OrderBy(k => k.OpenTime)
            .ToListAsync(ct);
        return GbpRateTable.FromKlines(klines);
    }

    /// <summary>The tax years the stored trade history actually spans, most recent first.</summary>
    public async Task<List<UkTaxYear>> ListTaxYearsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (!await db.Trades.AnyAsync(ct)) return [];

        var first = await db.Trades.MinAsync(t => t.Timestamp, ct);
        var last = await db.Trades.MaxAsync(t => t.Timestamp, ct);

        int from = UkTaxYear.ContainingDate(UkTime.ToUk(first)).StartYear;
        int to = UkTaxYear.ContainingDate(UkTime.ToUk(last)).StartYear;

        return Enumerable.Range(from, to - from + 1)
            .Select(y => new UkTaxYear(y))
            .OrderByDescending(y => y.StartYear)
            .ToList();
    }

    /// <summary>Builds the full report for one tax year.</summary>
    public async Task<TaxReport> BuildAsync(UkTaxYear year, decimal otherTaxableIncome, decimal broughtForwardLosses, TaxResidency residency, CancellationToken ct)
    {
        var rates = await LoadRateTableAsync(ct);
        if (rates.IsEmpty)
            return TaxReport.Unavailable(year,
                $"No {GbpRateTable.RateSymbol} daily candles are stored, so trades priced in dollars cannot be " +
                "valued in sterling. Run a daily price refresh and try again.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var trades = await db.Trades.AsNoTracking().OrderBy(t => t.Timestamp).ToListAsync(ct);
        var ledgers = await db.Ledgers.AsNoTracking().OrderBy(l => l.Timestamp).ToListAsync(ct);

        // A ticker that was renamed keeps one pool under its new name, so the map has to be in hand
        // before any event is built.
        var migrations = AssetMigrations.Detect(ledgers);
        if (migrations.Count > 0)
            _logger.LogInformation("[Tax] Following {Count} token migrations: {Map}",
                migrations.Count, string.Join(", ", migrations.Select(m => $"{m.Key}->{m.Value}")));

        var build = BuildTradeEvents(trades, rates, migrations);
        var stakingEvents = await BuildStakingEventsAsync(db, ledgers, rates, migrations, ct);

        // Staking rewards are income when received, and their sterling value at that moment becomes
        // the acquisition cost carried into the pool for when they are eventually sold.
        var allEvents = build.Events.Concat(stakingEvents.Events).ToList();
        var match = CapitalGainsMatcher.Match(allEvents, year);
        var tax = CapitalGainsTaxCalculator.Calculate(match.Rows, year, otherTaxableIncome, broughtForwardLosses);

        if (match.UnmatchedDisposals.Count > 0)
            _logger.LogWarning(
                "[Tax] {Year}: {Count} disposals have no acquisition in the stored history ({Assets})",
                year.Label, match.UnmatchedDisposals.Count,
                string.Join(", ", match.UnmatchedDisposals.Select(u => u.Asset).Distinct()));

        var income = stakingEvents.Events
            .Where(e => year.Contains(e.UkDate))
            .GroupBy(e => e.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TaxIncomeRow(g.Key, g.Sum(e => e.OriginalQuantity), g.Sum(e => e.OriginalCostGbp), g.Count()))
            .OrderByDescending(r => r.ValueGbp)
            .ToList();

        // Rewards are income, not gains, and are taxed on top of whatever else was earned that year.
        var incomeTax = IncomeTaxCalculator.Calculate(
            income.Sum(r => r.ValueGbp), year, otherTaxableIncome, residency);

        return new TaxReport(
            year, "ok", null,
            match.Rows, tax, income, incomeTax, match.UnmatchedDisposals,
            new TaxCoverage(
                trades.Count,
                build.SkippedNoRate + stakingEvents.SkippedNoRate,
                build.SkippedUnknownPair,
                build.CarriedForwardRates + stakingEvents.CarriedForwardRates,
                rates.DayCount,
                rates.EarliestDate,
                rates.LatestDate));
    }

    private sealed record EventBuild(List<TaxEvent> Events, int SkippedNoRate, int SkippedUnknownPair, int CarriedForwardRates);

    private EventBuild BuildTradeEvents(
        IReadOnlyList<Kraken.Net.Objects.Models.KrakenUserTrade> trades,
        GbpRateTable rates,
        IReadOnlyDictionary<string, string> migrations)
    {
        var events = new List<TaxEvent>();
        int skippedNoRate = 0, skippedUnknownPair = 0, carried = 0;

        foreach (var trade in trades)
        {
            string symbol = trade.Symbol ?? "";
            string baseAsset = AssetMigrations.Resolve(_state.NormalizeOrderSymbolBase(symbol), migrations);
            string quoteAsset = AssetMigrations.Resolve(_state.NormalizeOrderSymbolQuote(symbol), migrations);

            if (string.IsNullOrEmpty(baseAsset) || string.IsNullOrEmpty(quoteAsset)) { skippedUnknownPair++; continue; }

            var ukDate = UkTime.ToUk(trade.Timestamp);
            bool isBuy = trade.Side == OrderSide.Buy;

            // Sterling is the unit the gain is measured in, not an asset that can produce one, so a
            // pair with GBP on the base side (converting sterling to dollars, say) has no chargeable
            // leg there at all. Counting it would invent disposals with no acquisition behind them.
            bool baseIsSterling = string.Equals(baseAsset, "GBP", StringComparison.OrdinalIgnoreCase);

            // Kraken charges spot fees in the quote currency, so the base quantity is exactly what
            // changed hands and the fee is a separate sterling amount on the consideration side.
            var (considerationGbp, considerationSource) = rates.ToGbp(trade.QuoteQuantity, quoteAsset, ukDate);
            var (feeGbp, feeSource) = rates.ToGbp(trade.Fee, quoteAsset, ukDate);

            if (considerationSource == GbpRateTable.RateSource.Unavailable || feeSource == GbpRateTable.RateSource.Unavailable)
            {
                skippedNoRate++;
                continue;
            }
            if (considerationSource == GbpRateTable.RateSource.CarriedForward) carried++;

            string reference = trade.Id ?? trade.OrderId ?? "";

            if (!baseIsSterling)
                events.Add(new TaxEvent(baseAsset, isBuy, ukDate, trade.Quantity, considerationGbp, feeGbp, "Trade", reference));

            // A quote currency that is not sterling is itself an asset: paying USDT for BTC disposes
            // of that USDT, and receiving it on a sale acquires it. Skipping this leg is the single
            // most common way a crypto tax report understates disposals.
            if (!string.Equals(quoteAsset, "GBP", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(quoteAsset, "USD", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new TaxEvent(quoteAsset, !isBuy, ukDate, trade.QuoteQuantity, considerationGbp, 0m, "Trade (quote leg)", reference));
            }
        }

        return new EventBuild(events, skippedNoRate, skippedUnknownPair, carried);
    }

    private async Task<EventBuild> BuildStakingEventsAsync(
        KrakenDbContext db,
        IReadOnlyList<Kraken.Net.Objects.Models.KrakenLedgerEntry> ledgers,
        GbpRateTable rates,
        IReadOnlyDictionary<string, string> migrations,
        CancellationToken ct)
    {
        var rewards = ledgers
            .Where(l => l.Type == LedgerEntryType.Staking && l.Quantity > 0 &&
                        l.SubType != "spotFromStaking" && l.SubType != "spotToStaking")
            .ToList();

        var events = new List<TaxEvent>();
        int skippedNoRate = 0, carried = 0;

        foreach (var reward in rewards)
        {
            string asset = AssetMigrations.Resolve(TradingStateService.NormalizeAsset(reward.Asset), migrations);
            var ukDate = UkTime.ToUk(reward.Timestamp);

            // Valuing a reward needs the asset's own price on the day it landed. Only daily candles
            // reach back far enough to be worth asking, and where none exists the reward is counted
            // as received but carries no sterling value, which the coverage figures make plain.
            decimal priceUsd = await DailyCloseUsdAsync(db, TradingStateService.NormalizeAsset(reward.Asset), ukDate, ct);
            if (priceUsd <= 0m && asset != TradingStateService.NormalizeAsset(reward.Asset))
                priceUsd = await DailyCloseUsdAsync(db, asset, ukDate, ct);
            if (priceUsd <= 0m) { skippedNoRate++; continue; }

            var (valueGbp, source) = rates.ToGbp(reward.Quantity * priceUsd, "USD", ukDate);
            if (source == GbpRateTable.RateSource.Unavailable) { skippedNoRate++; continue; }
            if (source == GbpRateTable.RateSource.CarriedForward) carried++;

            events.Add(new TaxEvent(asset, true, ukDate, reward.Quantity, valueGbp, 0m, "Staking reward", reward.ReferenceId ?? ""));
        }

        return new EventBuild(events, skippedNoRate, 0, carried);
    }

    /// <summary>The asset's daily close in dollars on a date, or the most recent earlier one.</summary>
    private static async Task<decimal> DailyCloseUsdAsync(KrakenDbContext db, string asset, DateTime ukDate, CancellationToken ct)
    {
        string symbol = $"{asset}/USD";
        var upper = ukDate.Date.AddDays(1);
        return await db.DerivedKlines
            .Where(k => k.Asset == symbol && k.Interval == "OneDay" && k.Close > 0 && k.OpenTime < upper)
            .OrderByDescending(k => k.OpenTime)
            .Select(k => k.Close)
            .FirstOrDefaultAsync(ct);
    }
}

public sealed record TaxIncomeRow(string Asset, decimal Quantity, decimal ValueGbp, int PaymentCount);

/// <summary>How much of the report rests on exact data, so the reader can judge what to trust.</summary>
public sealed record TaxCoverage(
    int TradesConsidered,
    int SkippedForMissingRate,
    int SkippedForUnknownPair,
    int AmountsUsingCarriedRate,
    int RateDaysAvailable,
    DateTime? RateFrom,
    DateTime? RateTo);

public sealed record TaxReport(
    UkTaxYear Year,
    string Status,
    string? Message,
    IReadOnlyList<CapitalGainsRow> Rows,
    CapitalGainsTaxResult? Tax,
    IReadOnlyList<TaxIncomeRow> Income,
    IncomeTaxResult? IncomeTax,
    IReadOnlyList<UnmatchedDisposal> UnmatchedDisposals,
    TaxCoverage? Coverage)
{
    /// <summary>Capital gains tax and income tax together — the figure that actually leaves the account.</summary>
    public decimal TotalTaxDue => (Tax?.TotalTaxDue ?? 0m) + (IncomeTax?.TotalTaxDue ?? 0m);

    public static TaxReport Unavailable(UkTaxYear year, string message) =>
        new(year, "unavailable", message, [], null, [], null, [], null);
}
