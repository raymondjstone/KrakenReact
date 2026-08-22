namespace KrakenReact.Server.Tax;

/// <summary>Which HMRC rule matched a disposal to its acquisition cost.</summary>
public enum TaxMatchingRule
{
    /// <summary>Matched to acquisitions of the same asset on the same day (TCGA 1992 s.105(1)(b)).</summary>
    SameDay,

    /// <summary>Matched to acquisitions in the 30 days after — the bed-and-breakfast rule (s.106A(5)).</summary>
    ThirtyDay,

    /// <summary>Matched against the pooled average cost of everything else held (s.104).</summary>
    Section104Pool,
}

/// <summary>
/// One acquisition or disposal, already valued in sterling. Quantity is consumed as the matching
/// passes claim it; <see cref="OriginalQuantity"/> keeps what it started as.
/// </summary>
public sealed class TaxEvent
{
    public TaxEvent(string asset, bool isAcquisition, DateTime ukDate, decimal quantity, decimal costGbp, decimal feeGbp, string source, string reference)
    {
        Asset = asset;
        IsAcquisition = isAcquisition;
        UkDate = ukDate;
        OriginalQuantity = quantity;
        Quantity = quantity;
        OriginalCostGbp = costGbp;
        FeeGbp = feeGbp;
        Source = source;
        Reference = reference;
    }

    public string Asset { get; }
    public bool IsAcquisition { get; }
    public DateTime UkDate { get; }
    public DateTime UkDay => UkDate.Date;

    public decimal OriginalQuantity { get; }
    public decimal Quantity { get; set; }

    /// <summary>Consideration in sterling, excluding the fee.</summary>
    public decimal OriginalCostGbp { get; }

    public decimal FeeGbp { get; }

    /// <summary>Where the event came from — a trade, a staking reward — for the report's audit trail.</summary>
    public string Source { get; }

    public string Reference { get; }

    /// <summary>
    /// Per-unit cost and fee after the day's acquisitions (or disposals) have been averaged together.
    /// Set by the aggregation pass; zero until then.
    /// </summary>
    public decimal SameDayAverageUnitCostGbp { get; set; }
    public decimal SameDayAverageUnitFeeGbp { get; set; }

    /// <summary>The share of the consideration attributable to a quantity, at the day's average unit rate.</summary>
    public decimal ProportionalCostGbp(decimal quantity) =>
        SameDayAverageUnitCostGbp > 0m
            ? quantity * SameDayAverageUnitCostGbp
            : OriginalQuantity == 0m ? 0m : OriginalCostGbp * quantity / OriginalQuantity;

    /// <summary>The share of the fee attributable to a quantity, at the day's average unit rate.</summary>
    public decimal ProportionalFeeGbp(decimal quantity) =>
        SameDayAverageUnitFeeGbp > 0m
            ? quantity * SameDayAverageUnitFeeGbp
            : OriginalQuantity == 0m ? 0m : FeeGbp * quantity / OriginalQuantity;

    public void ResetMatchingState()
    {
        Quantity = OriginalQuantity;
        SameDayAverageUnitCostGbp = 0m;
        SameDayAverageUnitFeeGbp = 0m;
    }

    public override string ToString() =>
        $"{(IsAcquisition ? "BUY" : "SELL")} {OriginalQuantity} {Asset} for {OriginalCostGbp:N2} GBP on {UkDate:d}";
}

/// <summary>A disposal matched to its acquisition cost under one of the three rules.</summary>
public sealed record CapitalGainsRow(
    string Asset,
    DateTime SellDate,
    DateTime? BuyDate,
    TaxMatchingRule Rule,
    decimal Quantity,
    decimal ProceedsGbp,
    decimal SellFeeGbp,
    decimal AllowableCostGbp,
    string Source,
    string Reference)
{
    /// <summary>
    /// Gain is proceeds less the acquisition cost and less the cost of disposing — the selling fee is
    /// an allowable deduction in its own right, not part of what the asset cost to buy.
    /// </summary>
    public decimal GainOrLoss => ProceedsGbp - AllowableCostGbp - SellFeeGbp;
}

/// <summary>What a pool could cover of a disposal, and what it could not.</summary>
public sealed record PoolRemoval(decimal CostGbp, decimal CoveredQuantity, decimal ShortfallQuantity);

/// <summary>A disposal the trade history holds no acquisition for.</summary>
public sealed record UnmatchedDisposal(string Asset, DateTime SellDate, decimal Quantity, decimal ProceedsGbp);

/// <summary>The Section 104 pool for one asset: a running quantity and its pooled cost.</summary>
public sealed class Section104Pool
{
    private const decimal QuantityRoundingTolerance = 0.000001m;

    public decimal TotalQuantity { get; private set; }
    public decimal TotalCostGbp { get; private set; }
    public decimal AverageCost => TotalQuantity > 0m ? TotalCostGbp / TotalQuantity : 0m;

    /// <summary>Adds an acquisition, whose pooled cost includes the fee paid to acquire it.</summary>
    public void Add(TaxEvent acquisition)
    {
        TotalQuantity += acquisition.Quantity;
        TotalCostGbp += acquisition.ProportionalCostGbp(acquisition.Quantity)
                      + acquisition.ProportionalFeeGbp(acquisition.Quantity);
    }

    /// <summary>
    /// Removes what the pool can cover of a quantity, and reports anything it could not.
    /// <para>
    /// A shortfall means the acquisition is missing from the data: coins bought before the stored
    /// history begins, or moved in from another exchange or wallet. The pool covers what it holds and
    /// hands the rest back rather than inventing a cost basis, because a disposal matched against a
    /// cost of nothing is reported as pure gain and would overstate the tax due.
    /// </para>
    /// </summary>
    public PoolRemoval Remove(decimal quantity)
    {
        decimal shortfall = 0m;
        if (TotalQuantity < quantity)
        {
            decimal gap = quantity - TotalQuantity;
            // A tiny gap is the residue of decimal division, not missing history; absorb it silently.
            if (gap > Math.Max(TotalQuantity * QuantityRoundingTolerance, QuantityRoundingTolerance))
                shortfall = gap;
            quantity = TotalQuantity;
        }

        decimal cost = quantity == TotalQuantity ? TotalCostGbp : quantity * AverageCost;
        TotalQuantity -= quantity;
        TotalCostGbp = TotalQuantity == 0m ? 0m : TotalCostGbp - cost;
        return new PoolRemoval(cost, quantity, shortfall);
    }
}

/// <summary>
/// Matches disposals to acquisition costs under the HMRC share identification rules, in the order the
/// legislation requires: same day first, then the following thirty days, then the Section 104 pool.
/// <para>
/// The order is not a preference — a disposal must be matched against a same-day acquisition before
/// any earlier holding can be used, which is what stops someone selling at a loss and rebuying
/// immediately to bank it.
/// </para>
/// </summary>
public static class CapitalGainsMatcher
{
    /// <summary>
    /// Runs the three passes over every asset's events and returns the disposals falling in the
    /// requested tax year.
    /// <para>
    /// Events from outside the year still take part: the pool is built from the whole history, and a
    /// disposal in March can be matched to an acquisition in the following April.
    /// </para>
    /// </summary>
    /// <param name="events">Every acquisition and disposal across all history, in any order.</param>
    /// <param name="year">The tax year to report on.</param>
    public static MatchResult Match(IEnumerable<TaxEvent> events, UkTaxYear year)
    {
        var rows = new List<CapitalGainsRow>();
        var unmatched = new List<UnmatchedDisposal>();

        foreach (var assetGroup in events.GroupBy(e => e.Asset, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var e in assetGroup) e.ResetMatchingState();

            var byDay = assetGroup.ToLookup(e => e.UkDay);
            var days = assetGroup.GroupBy(e => e.UkDay).Select(g => g.Key).OrderBy(d => d).ToList();

            AggregateEachDay(assetGroup, days);
            MatchSameDay(byDay, days, year, rows);
            MatchThirtyDay(byDay, days, year, rows);
            MatchSection104(byDay, days, year, rows, unmatched);
        }

        return new MatchResult(
            rows.OrderBy(r => r.SellDate).ThenBy(r => r.Asset).ToList(),
            unmatched.OrderByDescending(u => u.ProceedsGbp).ToList());
    }

    /// <summary>
    /// TCGA 1992 s.105: all acquisitions of an asset on one day count as a single acquisition, and
    /// likewise disposals. Giving every event of the day the day's average unit cost is what makes
    /// the later passes agree no matter which individual fill they happen to consume.
    /// </summary>
    private static void AggregateEachDay(IEnumerable<TaxEvent> assetEvents, List<DateTime> days)
    {
        var byDay = assetEvents.ToLookup(e => e.UkDay);
        foreach (var day in days)
        {
            SetAverageUnitValues(byDay[day].Where(e => e.IsAcquisition));
            SetAverageUnitValues(byDay[day].Where(e => !e.IsAcquisition));
        }
    }

    private static void SetAverageUnitValues(IEnumerable<TaxEvent> sameDaySameSide)
    {
        var list = sameDaySameSide.Where(e => e.OriginalQuantity > 0m).ToList();
        if (list.Count < 2) return;

        decimal totalQuantity = list.Sum(e => e.OriginalQuantity);
        if (totalQuantity <= 0m) return;

        decimal averageUnitCost = list.Sum(e => e.OriginalCostGbp) / totalQuantity;
        decimal averageUnitFee = list.Sum(e => e.FeeGbp) / totalQuantity;
        foreach (var e in list)
        {
            e.SameDayAverageUnitCostGbp = averageUnitCost;
            e.SameDayAverageUnitFeeGbp = averageUnitFee;
        }
    }

    /// <summary>Pass one: match each disposal against acquisitions of the same asset on the same day.</summary>
    private static void MatchSameDay(ILookup<DateTime, TaxEvent> byDay, List<DateTime> days, UkTaxYear year, List<CapitalGainsRow> rows)
    {
        foreach (var day in days)
        {
            foreach (var disposal in byDay[day].Where(e => !e.IsAcquisition && e.Quantity > 0m).OrderBy(e => e.UkDate))
            {
                decimal matched = 0m, cost = 0m;
                DateTime? firstBuyDate = null;

                foreach (var acquisition in byDay[day].Where(e => e.IsAcquisition && e.Quantity > 0m).OrderBy(e => e.UkDate))
                {
                    if (disposal.Quantity <= 0m) break;
                    decimal take = Math.Min(disposal.Quantity, acquisition.Quantity);

                    firstBuyDate ??= acquisition.UkDate;
                    cost += acquisition.ProportionalCostGbp(take) + acquisition.ProportionalFeeGbp(take);
                    matched += take;
                    disposal.Quantity -= take;
                    acquisition.Quantity -= take;
                }

                if (matched > 0m && year.Contains(disposal.UkDate))
                    rows.Add(BuildRow(disposal, TaxMatchingRule.SameDay, firstBuyDate, matched, cost));
            }
        }
    }

    /// <summary>
    /// Pass two: match what is left against acquisitions in the following thirty days, earliest first.
    /// A disposal in the reported year can legitimately reach into the next tax year for its cost.
    /// </summary>
    private static void MatchThirtyDay(ILookup<DateTime, TaxEvent> byDay, List<DateTime> days, UkTaxYear year, List<CapitalGainsRow> rows)
    {
        foreach (var day in days)
        {
            foreach (var disposal in byDay[day].Where(e => !e.IsAcquisition && e.Quantity > 0m).OrderBy(e => e.UkDate))
            {
                for (int offset = 1; offset <= 30 && disposal.Quantity > 0m; offset++)
                {
                    foreach (var acquisition in byDay[day.AddDays(offset)].Where(e => e.IsAcquisition && e.Quantity > 0m).OrderBy(e => e.UkDate))
                    {
                        if (disposal.Quantity <= 0m) break;
                        decimal take = Math.Min(disposal.Quantity, acquisition.Quantity);
                        decimal cost = acquisition.ProportionalCostGbp(take) + acquisition.ProportionalFeeGbp(take);

                        disposal.Quantity -= take;
                        acquisition.Quantity -= take;

                        if (year.Contains(disposal.UkDate))
                            rows.Add(BuildRow(disposal, TaxMatchingRule.ThirtyDay, acquisition.UkDate, take, cost));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Pass three: walk the days in order, adding unmatched acquisitions to the pool and taking each
    /// day's remaining disposals out of it in one aggregate removal, then sharing that cost back
    /// across the individual disposals. Removing in one go is what keeps the day's disposals on a
    /// single average cost, as s.105 requires.
    /// </summary>
    private static void MatchSection104(ILookup<DateTime, TaxEvent> byDay, List<DateTime> days, UkTaxYear year, List<CapitalGainsRow> rows, List<UnmatchedDisposal> unmatched)
    {
        var pool = new Section104Pool();

        foreach (var day in days)
        {
            foreach (var acquisition in byDay[day].Where(e => e.IsAcquisition && e.Quantity > 0m).OrderBy(e => e.UkDate))
                pool.Add(acquisition);

            var disposals = byDay[day].Where(e => !e.IsAcquisition && e.Quantity > 0m).OrderBy(e => e.UkDate).ToList();
            decimal totalQuantity = disposals.Sum(e => e.Quantity);
            if (totalQuantity <= 0m) continue;

            var removal = pool.Remove(totalQuantity);

            // The pool is the sole judge of whether a gap is real: it has already absorbed anything
            // small enough to be decimal division. Re-deriving the shortfall from a ratio here would
            // resurrect that crumb as a phantom unmatched disposal.
            bool fullyCovered = removal.ShortfallQuantity == 0m;
            decimal coveredFraction = fullyCovered ? 1m : removal.CoveredQuantity / totalQuantity;
            decimal averageCost = removal.CoveredQuantity > 0m ? removal.CostGbp / removal.CoveredQuantity : 0m;

            foreach (var disposal in disposals)
            {
                decimal covered = fullyCovered ? disposal.Quantity : disposal.Quantity * coveredFraction;
                decimal missing = fullyCovered ? 0m : disposal.Quantity - covered;

                if (year.Contains(disposal.UkDate))
                {
                    if (covered > 0m)
                        rows.Add(BuildRow(disposal, TaxMatchingRule.Section104Pool, null, covered, covered * averageCost));
                    if (missing > 0m)
                        unmatched.Add(new UnmatchedDisposal(disposal.Asset, disposal.UkDate, missing, disposal.ProportionalCostGbp(missing)));
                }
                disposal.Quantity = 0m;
            }
        }
    }

    private static CapitalGainsRow BuildRow(TaxEvent disposal, TaxMatchingRule rule, DateTime? buyDate, decimal quantity, decimal costGbp) =>
        new(disposal.Asset,
            disposal.UkDate,
            buyDate,
            rule,
            quantity,
            disposal.ProportionalCostGbp(quantity),
            disposal.ProportionalFeeGbp(quantity),
            costGbp,
            disposal.Source,
            disposal.Reference);
}

/// <summary>The matched disposals for a year, and any the trade history could not account for.</summary>
public sealed record MatchResult(
    IReadOnlyList<CapitalGainsRow> Rows,
    IReadOnlyList<UnmatchedDisposal> UnmatchedDisposals);
