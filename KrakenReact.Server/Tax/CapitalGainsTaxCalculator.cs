namespace KrakenReact.Server.Tax;

/// <summary>What is owed on a year's disposals, and how the figure was arrived at.</summary>
public sealed record CapitalGainsTaxResult(
    UkTaxYear Year,
    decimal DisposalProceeds,
    decimal AllowableCosts,
    decimal NetGainOrLoss,
    decimal AnnualExemptAmount,
    decimal BroughtForwardLosses,
    decimal TaxableGains,
    decimal BasicRateBandAvailable,
    decimal AmountAtPreviousBasicRate,
    decimal AmountAtPreviousHigherRate,
    decimal AmountAtBasicRate,
    decimal AmountAtHigherRate,
    decimal TaxAtPreviousBasicRate,
    decimal TaxAtPreviousHigherRate,
    decimal TaxAtBasicRate,
    decimal TaxAtHigherRate,
    decimal TotalTaxDue,
    decimal LossesToCarryForward,
    bool IsRateChangeYear,
    bool IsExemptAmountAssumed)
{
    /// <summary>Tax as a share of the gain actually made, which is lower than any headline rate.</summary>
    public decimal EffectiveRate => NetGainOrLoss <= 0m ? 0m : TotalTaxDue / NetGainOrLoss;
}

/// <summary>
/// Works out capital gains tax on a year's matched disposals.
/// <para>
/// The 2024-25 year is the awkward one: rates rose on 30 October 2024, so gains before and after that
/// date are taxed differently and the reliefs have to be allocated between the two periods. Where
/// there is a choice about which period a relief lands in, it is taken where it saves the most.
/// </para>
/// </summary>
public static class CapitalGainsTaxCalculator
{
    /// <param name="rows">The year's matched disposals.</param>
    /// <param name="year">The tax year being reported.</param>
    /// <param name="otherTaxableIncome">Income taxable before this gain, which decides how much of the basic rate band is left.</param>
    /// <param name="broughtForwardLosses">Unused capital losses carried in from earlier years.</param>
    public static CapitalGainsTaxResult Calculate(
        IReadOnlyList<CapitalGainsRow> rows,
        UkTaxYear year,
        decimal otherTaxableIncome,
        decimal broughtForwardLosses)
    {
        decimal proceeds = rows.Sum(r => r.ProceedsGbp);
        decimal allowableCosts = rows.Sum(r => r.AllowableCostGbp + r.SellFeeGbp);
        decimal netGain = proceeds - allowableCosts;

        decimal exemptAmount = TaxConstants.AnnualExemptAmount(year);
        bool isRateChangeYear = TaxConstants.IsRateChangeYear(year);

        // Outside the rate-change year every disposal sits in one period, so the cutoff is set before
        // all of them and the "previous rate" buckets stay empty.
        DateTime cutoff = isRateChangeYear ? TaxConstants.CapitalGainsRateChangeDate : DateTime.MinValue;

        decimal preChangeRaw = rows.Where(r => r.SellDate < cutoff).Sum(r => r.GainOrLoss);
        decimal postChangeRaw = rows.Where(r => r.SellDate >= cutoff).Sum(r => r.GainOrLoss);

        // Losses in one period offset gains in the other. Setting them against the higher-rate period
        // first is the allocation that saves the most tax, and nothing in the rules forbids it.
        decimal preChangeGain, postChangeGain;
        if (preChangeRaw < 0m)
        {
            postChangeGain = Math.Max(0m, postChangeRaw + preChangeRaw);
            preChangeGain = 0m;
        }
        else if (postChangeRaw < 0m)
        {
            preChangeGain = Math.Max(0m, preChangeRaw + postChangeRaw);
            postChangeGain = 0m;
        }
        else
        {
            preChangeGain = preChangeRaw;
            postChangeGain = postChangeRaw;
        }

        decimal totalGains = preChangeGain + postChangeGain;
        decimal taxableGains = Math.Max(0m, totalGains - exemptAmount - broughtForwardLosses);

        decimal incomeAfterAllowance = Math.Max(0m, otherTaxableIncome - TaxConstants.IncomePersonalAllowance);
        decimal basicBandAvailable = Math.Max(0m, TaxConstants.IncomeBasicRateBand - incomeAfterAllowance);

        // The basic rate band goes to pre-change gains first: there it saves ten points (20 to 10),
        // against six (24 to 18) on post-change gains.
        decimal preBasicCapacity = Math.Min(preChangeGain, basicBandAvailable);
        decimal postBasicCapacity = Math.Min(postChangeGain, basicBandAvailable - preBasicCapacity);
        decimal preHigherCapacity = preChangeGain - preBasicCapacity;
        decimal postHigherCapacity = postChangeGain - postBasicCapacity;

        // Fill the buckets cheapest first, so the reliefs already deducted come off the dearest gains.
        decimal remaining = taxableGains;
        decimal atPreviousBasic = Math.Min(remaining, preBasicCapacity);
        remaining -= atPreviousBasic;
        decimal atBasic = Math.Min(remaining, postBasicCapacity);
        remaining -= atBasic;
        decimal atPreviousHigher = Math.Min(remaining, preHigherCapacity);
        remaining -= atPreviousHigher;
        decimal atHigher = Math.Min(remaining, postHigherCapacity);

        decimal taxPreviousBasic = atPreviousBasic * TaxConstants.CapitalGainsPreviousBasicRate;
        decimal taxPreviousHigher = atPreviousHigher * TaxConstants.CapitalGainsPreviousHigherRate;
        decimal taxBasic = atBasic * TaxConstants.BasicRateFor(year);
        decimal taxHigher = atHigher * TaxConstants.HigherRateFor(year);

        // Brought-forward losses are only spent to the extent gains exceed the exempt amount; the
        // rest survives, alongside any loss this year has made.
        decimal lossesUsed = Math.Min(broughtForwardLosses, Math.Max(0m, totalGains - exemptAmount));
        decimal lossesToCarryForward = broughtForwardLosses - lossesUsed + Math.Max(0m, -netGain);

        return new CapitalGainsTaxResult(
            year, proceeds, allowableCosts, netGain, exemptAmount, broughtForwardLosses, taxableGains,
            basicBandAvailable,
            atPreviousBasic, atPreviousHigher, atBasic, atHigher,
            taxPreviousBasic, taxPreviousHigher, taxBasic, taxHigher,
            taxPreviousBasic + taxPreviousHigher + taxBasic + taxHigher,
            lossesToCarryForward,
            isRateChangeYear,
            TaxConstants.IsExemptAmountAssumed(year));
    }
}
