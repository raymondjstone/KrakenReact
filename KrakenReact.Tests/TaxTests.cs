using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Tax;

namespace KrakenReact.Tests;

/// <summary>
/// Worked examples for the UK share identification rules, with the expected figures derived by hand
/// from HMRC's own guidance. Tax arithmetic gets no benefit of the doubt: every number here is one a
/// person could check against a return.
/// </summary>
public class TaxTests
{
    private static TaxEvent Buy(string day, decimal qty, decimal costGbp, decimal feeGbp = 0m) =>
        new("BTC", true, DateTime.Parse(day), qty, costGbp, feeGbp, "Trade", day);

    private static TaxEvent Sell(string day, decimal qty, decimal proceedsGbp, decimal feeGbp = 0m) =>
        new("BTC", false, DateTime.Parse(day), qty, proceedsGbp, feeGbp, "Trade", day);

    /// <summary>Runs the matcher and returns just the matched rows, which is what most tests assert on.</summary>
    private static IReadOnlyList<CapitalGainsRow> MatchRows(IEnumerable<TaxEvent> events, UkTaxYear year) =>
        CapitalGainsMatcher.Match(events, year).Rows;

    private static readonly UkTaxYear Year2024 = new(2024);
    private static readonly UkTaxYear Year2023 = new(2023);

    // ── Tax year boundaries ─────────────────────────────────────────────────

    [Theory]
    [InlineData("2024-04-06", 2024)]   // first day of 2024-25
    [InlineData("2025-04-05", 2024)]   // last day of 2024-25
    [InlineData("2025-04-06", 2025)]   // first day of 2025-26
    [InlineData("2024-04-05", 2023)]   // last day of 2023-24
    [InlineData("2024-12-31", 2024)]
    public void UkTaxYear_StartsOnTheSixthOfApril(string date, int expectedStartYear)
    {
        Assert.Equal(expectedStartYear, UkTaxYear.ContainingDate(DateTime.Parse(date)).StartYear);
    }

    [Fact]
    public void UkTaxYear_LabelsItselfTheWayHmrcDoes()
    {
        Assert.Equal("2024-25", new UkTaxYear(2024).Label);
        Assert.Equal("2009-10", new UkTaxYear(2009).Label);
    }

    [Fact]
    public void UkTaxYear_ExcludesTheFirstInstantOfTheNextYear()
    {
        var year = new UkTaxYear(2024);
        Assert.True(year.Contains(DateTime.Parse("2025-04-05 23:59:59")));
        Assert.False(year.Contains(year.ExclusiveEnd));
    }

    // ── Same day rule ───────────────────────────────────────────────────────

    [Fact]
    public void SameDayRule_MatchesADisposalToThatDaysAcquisition()
    {
        // Bought 1 for £10,000 and sold 1 for £12,000 on the same day: a £2,000 gain, and the pool
        // is never touched.
        var rows = MatchRows(
            [Buy("2024-06-01", 1m, 10_000m), Sell("2024-06-01", 1m, 12_000m)], Year2024);

        var row = Assert.Single(rows);
        Assert.Equal(TaxMatchingRule.SameDay, row.Rule);
        Assert.Equal(2_000m, row.GainOrLoss);
    }

    [Fact]
    public void SameDayRule_TakesPriorityOverAnOlderPoolHolding()
    {
        // The old cheap holding must NOT be used: HMRC matches the same-day acquisition first.
        var rows = MatchRows(
        [
            Buy("2020-01-01", 1m, 1_000m),     // old, cheap — must stay in the pool
            Buy("2024-06-01", 1m, 10_000m),    // same day as the sale
            Sell("2024-06-01", 1m, 12_000m),
        ], Year2024);

        var row = Assert.Single(rows);
        Assert.Equal(TaxMatchingRule.SameDay, row.Rule);
        Assert.Equal(2_000m, row.GainOrLoss);   // not £11,000
    }

    [Fact]
    public void SameDayRule_AveragesSeveralAcquisitionsOnTheSameDay()
    {
        // TCGA 1992 s.105: two buys on one day are one acquisition at the average cost.
        // (10,000 + 14,000) / 2 = 12,000 per unit; selling 1 for 13,000 gains 1,000.
        var rows = MatchRows(
        [
            Buy("2024-06-01", 1m, 10_000m),
            Buy("2024-06-01", 1m, 14_000m),
            Sell("2024-06-01", 1m, 13_000m),
        ], Year2024);

        var row = Assert.Single(rows);
        Assert.Equal(TaxMatchingRule.SameDay, row.Rule);
        Assert.Equal(1_000m, row.GainOrLoss);
    }

    // ── 30 day (bed and breakfast) rule ─────────────────────────────────────

    [Fact]
    public void ThirtyDayRule_MatchesARepurchaseWithinThirtyDays()
    {
        // The classic bed-and-breakfast: sell at a loss, buy back a week later. The rule denies the
        // loss against the old cheap pool and matches it to the repurchase instead.
        var rows = MatchRows(
        [
            Buy("2020-01-01", 1m, 1_000m),
            Sell("2024-06-01", 1m, 12_000m),
            Buy("2024-06-08", 1m, 11_500m),
        ], Year2024);

        var row = Assert.Single(rows);
        Assert.Equal(TaxMatchingRule.ThirtyDay, row.Rule);
        Assert.Equal(500m, row.GainOrLoss);     // not £11,000 against the old pool
    }

    [Fact]
    public void ThirtyDayRule_IncludesARepurchaseOnTheThirtiethDay()
    {
        // The window is inclusive: an acquisition exactly thirty days later still matches.
        var rows = MatchRows(
        [
            Buy("2020-01-01", 1m, 1_000m),
            Sell("2024-06-01", 1m, 12_000m),
            Buy("2024-07-01", 1m, 11_500m),
        ], Year2024);

        Assert.Equal(TaxMatchingRule.ThirtyDay, Assert.Single(rows).Rule);
    }

    [Fact]
    public void ThirtyDayRule_IgnoresARepurchaseOnTheThirtyFirstDay()
    {
        // One day beyond the window: the disposal falls through to the pool and the old cost applies.
        var rows = MatchRows(
        [
            Buy("2020-01-01", 1m, 1_000m),
            Sell("2024-06-01", 1m, 12_000m),
            Buy("2024-07-02", 1m, 11_500m),
        ], Year2024);

        var row = Assert.Single(rows);
        Assert.Equal(TaxMatchingRule.Section104Pool, row.Rule);
        Assert.Equal(11_000m, row.GainOrLoss);
    }

    [Fact]
    public void ThirtyDayRule_TakesTheEarliestRepurchaseFirst()
    {
        var rows = MatchRows(
        [
            Sell("2024-06-01", 2m, 20_000m),
            Buy("2024-06-05", 1m, 8_000m),
            Buy("2024-06-20", 1m, 9_000m),
        ], Year2024);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(TaxMatchingRule.ThirtyDay, r.Rule));
        Assert.Equal(DateTime.Parse("2024-06-05"), rows[0].BuyDate);
        Assert.Equal(DateTime.Parse("2024-06-20"), rows[1].BuyDate);
    }

    // ── Section 104 pool ────────────────────────────────────────────────────

    [Fact]
    public void Section104Pool_UsesThePooledAverageCost()
    {
        // Two acquisitions pool to an average of £15,000; selling 1 for £20,000 gains £5,000.
        var rows = MatchRows(
        [
            Buy("2023-01-01", 1m, 10_000m),
            Buy("2023-06-01", 1m, 20_000m),
            Sell("2024-06-01", 1m, 20_000m),
        ], Year2024);

        var row = Assert.Single(rows);
        Assert.Equal(TaxMatchingRule.Section104Pool, row.Rule);
        Assert.Equal(5_000m, row.GainOrLoss);
    }

    [Fact]
    public void Section104Pool_FoldsAcquisitionFeesIntoTheCost()
    {
        // HMRC allows the cost of acquiring as part of the base cost: £10,000 + £50 = £10,050.
        var rows = MatchRows(
        [
            Buy("2023-01-01", 1m, 10_000m, feeGbp: 50m),
            Sell("2024-06-01", 1m, 12_000m),
        ], Year2024);

        Assert.Equal(10_050m, Assert.Single(rows).AllowableCostGbp);
    }

    [Fact]
    public void Section104Pool_DeductsTheDisposalFeeFromTheGain()
    {
        // Selling costs are deductible too: 12,000 - 10,000 - 30 = 1,970.
        var rows = MatchRows(
        [
            Buy("2023-01-01", 1m, 10_000m),
            Sell("2024-06-01", 1m, 12_000m, feeGbp: 30m),
        ], Year2024);

        Assert.Equal(1_970m, Assert.Single(rows).GainOrLoss);
    }

    [Fact]
    public void Section104Pool_LeavesNoResidualCostWhenFullyDrained()
    {
        // Selling the lot then rebuying must not carry a rounding crumb into the new pool: the
        // second disposal's cost has to be exactly the second acquisition's. The rebuy is placed
        // well clear of the 30-day window, or that rule would claim it before the pool is reached.
        var rows = MatchRows(
        [
            Buy("2023-01-01", 3m, 10_000m),
            Sell("2023-06-01", 3m, 15_000m),
            Buy("2023-09-01", 1m, 7_000m),
            Sell("2024-06-01", 1m, 9_000m),
        ], Year2024);

        var row = Assert.Single(rows);
        Assert.Equal(7_000m, row.AllowableCostGbp);
        Assert.Equal(2_000m, row.GainOrLoss);
    }

    [Fact]
    public void Section104Pool_SetsAsideADisposalItHasNoAcquisitionFor()
    {
        // Selling five having bought one means four came from somewhere the data cannot see. Giving
        // them a cost basis of nothing would report 40,000 of phantom gain, so they are set aside and
        // named instead — the report stays honest and the reader knows exactly what is missing.
        var result = CapitalGainsMatcher.Match(
        [
            Buy("2023-01-01", 1m, 10_000m),
            Sell("2024-06-01", 5m, 50_000m),
        ], Year2024);

        var matched = Assert.Single(result.Rows);
        Assert.Equal(1m, matched.Quantity);
        Assert.Equal(10_000m, matched.AllowableCostGbp);

        var missing = Assert.Single(result.UnmatchedDisposals);
        Assert.Equal(4m, missing.Quantity);
        Assert.Equal(40_000m, missing.ProceedsGbp);
    }

    [Fact]
    public void Section104Pool_AbsorbsATinyShortfallAsRounding()
    {
        // A gap of one part in a billion is decimal division, not missing history: no warning.
        var result = CapitalGainsMatcher.Match(
        [
            Buy("2023-01-01", 1m, 10_000m),
            Sell("2024-06-01", 1.0000000001m, 12_000m),
        ], Year2024);

        Assert.Single(result.Rows);
        Assert.Empty(result.UnmatchedDisposals);
    }

    [Fact]
    public void Section104Pool_ReportsNothingUnmatchedWhenTheHistoryIsComplete()
    {
        var result = CapitalGainsMatcher.Match(
            [Buy("2023-01-01", 2m, 20_000m), Sell("2024-06-01", 2m, 30_000m)], Year2024);

        Assert.Single(result.Rows);
        Assert.Empty(result.UnmatchedDisposals);
    }

    // ── Rule precedence and year scoping ────────────────────────────────────

    [Fact]
    public void Matcher_AppliesTheRulesInTheStatutoryOrder()
    {
        // 3 sold: 1 matches the same day, 1 the repurchase inside 30 days, 1 falls to the pool.
        var rows = MatchRows(
        [
            Buy("2020-01-01", 1m, 1_000m),      // the pool
            Buy("2024-06-01", 1m, 10_000m),     // same day
            Sell("2024-06-01", 3m, 36_000m),    // 12,000 each
            Buy("2024-06-10", 1m, 11_000m),     // inside 30 days
        ], Year2024);

        Assert.Equal(3, rows.Count);
        Assert.Equal(
            [TaxMatchingRule.SameDay, TaxMatchingRule.ThirtyDay, TaxMatchingRule.Section104Pool],
            rows.Select(r => r.Rule).ToArray());
        Assert.Equal(2_000m, rows[0].GainOrLoss);    // 12,000 - 10,000
        Assert.Equal(1_000m, rows[1].GainOrLoss);    // 12,000 - 11,000
        Assert.Equal(11_000m, rows[2].GainOrLoss);   // 12,000 - 1,000
    }

    [Fact]
    public void Matcher_ReportsOnlyTheRequestedYearButPoolsTheWholeHistory()
    {
        var events = new[]
        {
            Buy("2022-01-01", 1m, 5_000m),
            Sell("2023-06-01", 1m, 8_000m),   // 2023-24
            Buy("2023-08-01", 1m, 9_000m),
            Sell("2024-06-01", 1m, 15_000m),  // 2024-25
        };

        var reported2024 = MatchRows(events, Year2024);
        var row = Assert.Single(reported2024);
        Assert.Equal(DateTime.Parse("2024-06-01"), row.SellDate);
        // Its cost comes from the 2023 acquisition, so earlier years must still have been processed.
        Assert.Equal(9_000m, row.AllowableCostGbp);

        Assert.Equal(DateTime.Parse("2023-06-01"), Assert.Single(MatchRows(events, Year2023)).SellDate);
    }

    [Fact]
    public void Matcher_KeepsAssetsApart()
    {
        var events = new List<TaxEvent>
        {
            new("BTC", true, DateTime.Parse("2023-01-01"), 1m, 10_000m, 0m, "Trade", "a"),
            new("ETH", true, DateTime.Parse("2023-01-01"), 1m, 2_000m, 0m, "Trade", "b"),
            new("ETH", false, DateTime.Parse("2024-06-01"), 1m, 3_000m, 0m, "Trade", "c"),
        };

        var row = Assert.Single(MatchRows(events, Year2024));
        Assert.Equal("ETH", row.Asset);
        Assert.Equal(1_000m, row.GainOrLoss);
    }

    // ── Tax calculation ─────────────────────────────────────────────────────

    [Fact]
    public void Tax_IsNilWhenTheGainSitsInsideTheExemptAmount()
    {
        // 2024-25 exempt amount is £3,000; a £2,000 gain owes nothing.
        var rows = MatchRows(
            [Buy("2023-01-01", 1m, 10_000m), Sell("2024-06-01", 1m, 12_000m)], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 20_000m, broughtForwardLosses: 0m);
        Assert.Equal(3_000m, tax.AnnualExemptAmount);
        Assert.Equal(0m, tax.TaxableGains);
        Assert.Equal(0m, tax.TotalTaxDue);
    }

    [Fact]
    public void Tax_ChargesTheHigherRateWhenIncomeFillsTheBasicBand()
    {
        // Income of £80,000 exhausts the basic band, so the whole taxable gain is at 24%.
        // Gain 23,000 - 3,000 exempt = 20,000 taxable; 20,000 * 0.24 = 4,800.
        var rows = MatchRows(
            [Buy("2023-01-01", 1m, 10_000m), Sell("2024-11-01", 1m, 33_000m)], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 80_000m, broughtForwardLosses: 0m);
        Assert.Equal(20_000m, tax.TaxableGains);
        Assert.Equal(0m, tax.BasicRateBandAvailable);
        Assert.Equal(4_800m, tax.TotalTaxDue);
    }

    [Fact]
    public void Tax_UsesTheOldRateForADisposalBeforeThirtiethOctober2024()
    {
        // Same gain, sold in June 2024: the pre-Budget higher rate of 20% applies, not 24%.
        var rows = MatchRows(
            [Buy("2023-01-01", 1m, 10_000m), Sell("2024-06-01", 1m, 33_000m)], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 80_000m, broughtForwardLosses: 0m);
        Assert.True(tax.IsRateChangeYear);
        Assert.Equal(20_000m, tax.AmountAtPreviousHigherRate);
        Assert.Equal(0m, tax.AmountAtHigherRate);
        Assert.Equal(4_000m, tax.TotalTaxDue);      // 20,000 * 0.20
    }

    [Fact]
    public void Tax_SplitsAYearThatStraddlesTheRateChange()
    {
        // £20,000 gain either side of 30 October. After the £3,000 exempt amount, 37,000 is taxable
        // and every pound is above the basic band, so: 20,000 at 20% + 17,000 at 24% = 8,080.
        var rows = MatchRows(
        [
            Buy("2023-01-01", 2m, 20_000m),
            Sell("2024-06-01", 1m, 30_000m),
            Sell("2024-12-01", 1m, 30_000m),
        ], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 80_000m, broughtForwardLosses: 0m);
        Assert.Equal(37_000m, tax.TaxableGains);
        Assert.Equal(20_000m, tax.AmountAtPreviousHigherRate);
        Assert.Equal(17_000m, tax.AmountAtHigherRate);
        Assert.Equal(8_080m, tax.TotalTaxDue);
    }

    [Fact]
    public void Tax_GivesTheBasicBandToPreChangeGainsFirst()
    {
        // The band is worth more against pre-change gains (saves 10 points, not 6), so that is where
        // it must land when both periods have gains competing for it.
        var rows = MatchRows(
        [
            Buy("2023-01-01", 2m, 20_000m),
            Sell("2024-06-01", 1m, 30_000m),
            Sell("2024-12-01", 1m, 30_000m),
        ], Year2024);

        // £20,000 income uses £7,430 of the band above the personal allowance, leaving £30,270.
        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 20_000m, broughtForwardLosses: 0m);
        Assert.Equal(30_270m, tax.BasicRateBandAvailable);
        Assert.True(tax.AmountAtPreviousBasicRate > 0m, "pre-change gains should take the basic band first");
        Assert.Equal(20_000m, tax.AmountAtPreviousBasicRate);
    }

    [Fact]
    public void Tax_SetsLossesAgainstTheDearerPeriod()
    {
        // A pre-change loss offsets post-change gains, which are taxed higher — the allocation that
        // costs the taxpayer least.
        var rows = MatchRows(
        [
            Buy("2023-01-01", 2m, 40_000m),
            Sell("2024-06-01", 1m, 15_000m),   // £5,000 loss, pre-change
            Sell("2024-12-01", 1m, 45_000m),   // £25,000 gain, post-change
        ], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 80_000m, broughtForwardLosses: 0m);
        Assert.Equal(20_000m, tax.NetGainOrLoss);
        Assert.Equal(17_000m, tax.TaxableGains);          // 20,000 - 3,000 exempt
        Assert.Equal(0m, tax.AmountAtPreviousHigherRate); // the pre-change period nets to nothing
        Assert.Equal(17_000m, tax.AmountAtHigherRate);
    }

    [Fact]
    public void Tax_CarriesAYearsLossForward()
    {
        var rows = MatchRows(
            [Buy("2023-01-01", 1m, 20_000m), Sell("2024-06-01", 1m, 12_000m)], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 50_000m, broughtForwardLosses: 0m);
        Assert.Equal(-8_000m, tax.NetGainOrLoss);
        Assert.Equal(0m, tax.TotalTaxDue);
        Assert.Equal(8_000m, tax.LossesToCarryForward);
    }

    [Fact]
    public void Tax_SpendsBroughtForwardLossesOnlyAboveTheExemptAmount()
    {
        // £10,000 gain, £3,000 exempt, £5,000 brought forward: only £7,000 of the gain is exposed, so
        // £5,000 of loss is used and £2,000 taxable remains. Nothing is carried on.
        var rows = MatchRows(
            [Buy("2023-01-01", 1m, 10_000m), Sell("2024-11-01", 1m, 20_000m)], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 80_000m, broughtForwardLosses: 5_000m);
        Assert.Equal(2_000m, tax.TaxableGains);
        Assert.Equal(0m, tax.LossesToCarryForward);
        Assert.Equal(480m, tax.TotalTaxDue);   // 2,000 * 0.24
    }

    [Fact]
    public void Tax_KeepsUnusedBroughtForwardLosses()
    {
        // A £4,000 gain is covered by the exempt amount alone, so the brought-forward loss survives.
        var rows = MatchRows(
            [Buy("2023-01-01", 1m, 10_000m), Sell("2024-11-01", 1m, 14_000m)], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 80_000m, broughtForwardLosses: 5_000m);
        Assert.Equal(0m, tax.TaxableGains);
        Assert.Equal(4_000m, tax.LossesToCarryForward);   // 5,000 less the 1,000 spent
    }

    [Fact]
    public void Tax_ReportsTheEffectiveRateAgainstTheGainActuallyMade()
    {
        var rows = MatchRows(
            [Buy("2023-01-01", 1m, 10_000m), Sell("2024-11-01", 1m, 33_000m)], Year2024);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 80_000m, broughtForwardLosses: 0m);
        // 4,800 of tax on a 23,000 gain is under the 24% headline, because of the exempt amount.
        Assert.Equal(Math.Round(4_800m / 23_000m, 6), Math.Round(tax.EffectiveRate, 6));
        Assert.True(tax.EffectiveRate < TaxConstants.CapitalGainsHigherRate);
    }

    // ── Allowances ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2022, 12_300)]
    [InlineData(2023, 6_000)]
    [InlineData(2024, 3_000)]
    public void ExemptAmount_MatchesTheStatutoryFigure(int startYear, decimal expected)
    {
        Assert.Equal(expected, TaxConstants.AnnualExemptAmount(new UkTaxYear(startYear)));
    }

    [Fact]
    public void ExemptAmount_CarriesForwardButSaysSo()
    {
        var future = new UkTaxYear(TaxConstants.LastConfirmedExemptYear + 3);
        Assert.Equal(3_000m, TaxConstants.AnnualExemptAmount(future));
        Assert.True(TaxConstants.IsExemptAmountAssumed(future),
            "a year with no confirmed figure must be flagged, not quietly assumed");
        Assert.False(TaxConstants.IsExemptAmountAssumed(new UkTaxYear(2024)));
    }

    [Fact]
    public void ExemptAmount_RefusesAYearBeforeAnyRecordedFigure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TaxConstants.AnnualExemptAmount(new UkTaxYear(2010)));
    }

    // ── GBP conversion ──────────────────────────────────────────────────────

    private static GbpRateTable RateTable(params (string Date, decimal GbpUsdClose)[] closes) =>
        GbpRateTable.FromKlines(closes.Select(c => new KrakenReact.Server.Models.DerivedKline
        {
            Asset = GbpRateTable.RateSymbol,
            Interval = "OneDay",
            OpenTime = DateTime.Parse(c.Date),
            Close = c.GbpUsdClose,
        }));

    [Fact]
    public void GbpRate_ConvertsDollarsAtThatDaysRate()
    {
        // GBP/USD of 1.25 means a dollar is worth 80 pence, so $100 is £80.
        var (gbp, source) = RateTable(("2024-06-01", 1.25m)).ToGbp(100m, "USD", DateTime.Parse("2024-06-01"));
        Assert.Equal(80m, gbp);
        Assert.Equal(GbpRateTable.RateSource.Exact, source);
    }

    [Fact]
    public void GbpRate_CarriesTheLastKnownRateOverAGapAndSaysSo()
    {
        var table = RateTable(("2024-06-01", 1.25m), ("2024-06-05", 1.00m));
        var (gbp, source) = table.ToGbp(100m, "USD", DateTime.Parse("2024-06-03"));
        Assert.Equal(80m, gbp);
        Assert.Equal(GbpRateTable.RateSource.CarriedForward, source);
    }

    [Fact]
    public void GbpRate_HasNothingToOfferBeforeItsFirstDay()
    {
        var (_, source) = RateTable(("2024-06-01", 1.25m)).ToGbp(100m, "USD", DateTime.Parse("2020-01-01"));
        Assert.Equal(GbpRateTable.RateSource.Unavailable, source);
    }

    [Fact]
    public void GbpRate_PassesSterlingThroughUnchanged()
    {
        var (gbp, source) = RateTable().ToGbp(100m, "GBP", DateTime.Parse("2024-06-01"));
        Assert.Equal(100m, gbp);
        Assert.Equal(GbpRateTable.RateSource.Exact, source);
    }

    [Fact]
    public void GbpRate_TreatsDollarStablecoinsAsDollars()
    {
        var table = RateTable(("2024-06-01", 1.25m));
        foreach (var stable in new[] { "USDT", "USDC", "USDQ", "DAI" })
            Assert.Equal(80m, table.ToGbp(100m, stable, DateTime.Parse("2024-06-01")).Gbp);
    }

    [Fact]
    public void GbpRate_DeclinesACurrencyItCannotPrice()
    {
        var (_, source) = RateTable(("2024-06-01", 1.25m)).ToGbp(100m, "EUR", DateTime.Parse("2024-06-01"));
        Assert.Equal(GbpRateTable.RateSource.Unavailable, source);
    }

    // ── Token migrations ────────────────────────────────────────────────────

    private static KrakenLedgerEntry Transfer(string id, string asset, string day, decimal quantity) => new()
    {
        Id = id,
        Asset = asset,
        Timestamp = DateTime.Parse(day),
        Quantity = quantity,
        Type = LedgerEntryType.Transfer,
        SubType = "",
    };

    [Fact]
    public void AssetMigrations_SpotsARenameFromItsTransferPair()
    {
        // The whole of one asset leaves and the identical quantity of another arrives: a rename,
        // which is what MATIC becoming POL looks like in the raw ledger.
        var map = AssetMigrations.Detect(
        [
            Transfer("a", "MATIC", "2024-09-13 10:00", -500m),
            Transfer("b", "POL", "2024-09-13 10:00", 500m),
        ]);

        Assert.Equal("POL", Assert.Single(map).Value);
        Assert.Equal("MATIC", map.Keys.Single());
    }

    [Fact]
    public void AssetMigrations_IgnoresTransfersThatDoNotPairUp()
    {
        // An ordinary deposit and an unrelated withdrawal of a different size are not a migration.
        Assert.Empty(AssetMigrations.Detect(
        [
            Transfer("a", "BTC", "2024-09-13", -1m),
            Transfer("b", "ETH", "2024-09-14", 3m),
        ]));
    }

    [Fact]
    public void AssetMigrations_IgnoresAPairOfTheSameAsset()
    {
        // Moving an asset between wallets is not a rename, however neatly the quantities match.
        Assert.Empty(AssetMigrations.Detect(
        [
            Transfer("a", "BTC", "2024-09-13", -1m),
            Transfer("b", "BTC", "2024-09-13", 1m),
        ]));
    }

    [Fact]
    public void AssetMigrations_FollowsAChainOfRenames()
    {
        var map = AssetMigrations.Detect(
        [
            Transfer("a", "OLD", "2023-01-01", -100m),
            Transfer("b", "MID", "2023-01-01", 100m),
            Transfer("c", "MID", "2024-01-01", -100m),
            Transfer("d", "NEW", "2024-01-01", 100m),
        ]);

        Assert.Equal("NEW", AssetMigrations.Resolve("OLD", map));
        Assert.Equal("NEW", AssetMigrations.Resolve("MID", map));
        Assert.Equal("NEW", AssetMigrations.Resolve("NEW", map));
    }

    [Fact]
    public void AssetMigrations_SurvivesACycleInMalformedData()
    {
        // Data that claims A became B and B became A would spin forever without the visited set.
        var cyclic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["A"] = "B", ["B"] = "A" };
        Assert.Equal("B", AssetMigrations.Resolve("A", cyclic));
    }

    [Fact]
    public void AssetMigrations_LeavesAnUnknownTickerAlone()
    {
        Assert.Equal("BTC", AssetMigrations.Resolve("BTC", new Dictionary<string, string>()));
    }

    [Fact]
    public void AssetMigrations_KeepOneContinuousPoolAcrossTheRename()
    {
        // Bought as MATIC, sold as POL. Resolving both to POL is what lets the disposal find its
        // cost; without it the sale has no acquisition and the gain is overstated by the whole
        // proceeds.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MATIC"] = "POL" };
        var events = new List<TaxEvent>
        {
            new(AssetMigrations.Resolve("MATIC", map), true, DateTime.Parse("2023-01-01"), 500m, 200m, 0m, "Trade", "a"),
            new(AssetMigrations.Resolve("POL", map), false, DateTime.Parse("2024-06-01"), 500m, 350m, 0m, "Trade", "b"),
        };

        var result = CapitalGainsMatcher.Match(events, Year2024);
        Assert.Empty(result.UnmatchedDisposals);
        var row = Assert.Single(result.Rows);
        Assert.Equal("POL", row.Asset);
        Assert.Equal(150m, row.GainOrLoss);
    }

    // ── End to end ──────────────────────────────────────────────────────────

    [Fact]
    public void WorkedExample_MatchesAHandCalculatedReturn()
    {
        // Jan 2023: buy 2 BTC for £30,000 (pool: 2 @ £15,000)
        // Jun 2024: sell 1 for £25,000            → pool match, gain £10,000
        // Dec 2024: sell 1 for £5,000             → pool match, loss £10,000
        // Net nil, so no tax and nothing to carry forward.
        var rows = MatchRows(
        [
            Buy("2023-01-15", 2m, 30_000m),
            Sell("2024-06-10", 1m, 25_000m),
            Sell("2024-12-10", 1m, 5_000m),
        ], Year2024);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(TaxMatchingRule.Section104Pool, r.Rule));
        Assert.Equal(10_000m, rows[0].GainOrLoss);
        Assert.Equal(-10_000m, rows[1].GainOrLoss);

        var tax = CapitalGainsTaxCalculator.Calculate(rows, Year2024, otherTaxableIncome: 60_000m, broughtForwardLosses: 0m);
        Assert.Equal(30_000m, tax.DisposalProceeds);
        Assert.Equal(0m, tax.NetGainOrLoss);
        Assert.Equal(0m, tax.TotalTaxDue);
        Assert.Equal(0m, tax.LossesToCarryForward);
    }

    [Fact]
    public void Matcher_TreatsSterlingLikeAnyOtherAssetItIsGiven()
    {
        // The matcher is deliberately asset-agnostic — it is the report builder's job never to hand
        // it sterling. This pins that contract: if GBP events ever arrive here they WILL be matched,
        // so the filtering has to happen upstream where the trade legs are built.
        var events = new List<TaxEvent>
        {
            new("GBP", true, DateTime.Parse("2023-01-01"), 1_000m, 1_000m, 0m, "Trade", "a"),
            new("GBP", false, DateTime.Parse("2024-06-01"), 1_000m, 1_000m, 0m, "Trade", "b"),
        };

        Assert.Single(CapitalGainsMatcher.Match(events, Year2024).Rows);
    }

    [Fact]
    public void Matcher_ProducesNothingFromAnEmptyHistory()
    {
        Assert.Empty(MatchRows([], Year2024));

        var tax = CapitalGainsTaxCalculator.Calculate([], Year2024, 50_000m, 0m);
        Assert.Equal(0m, tax.TotalTaxDue);
        Assert.Equal(0m, tax.NetGainOrLoss);
    }
}
