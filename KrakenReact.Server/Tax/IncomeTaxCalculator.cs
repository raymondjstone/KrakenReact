namespace KrakenReact.Server.Tax;

/// <summary>Where the taxpayer is resident, which decides the income tax bands that apply.</summary>
public enum TaxResidency
{
    /// <summary>England, Wales and Northern Ireland — three bands.</summary>
    RestOfUk = 0,

    /// <summary>Scotland, which sets its own rates and has had up to six bands.</summary>
    Scotland = 1,
}

/// <summary>One income tax band: everything up to the limit is charged at the rate.</summary>
public sealed record IncomeTaxBand(string Name, decimal CumulativeUpperLimit, decimal Rate);

/// <summary>How much of the income landed in a band, and what that cost.</summary>
public sealed record IncomeBandResult(string Name, decimal Rate, decimal Amount)
{
    public decimal TaxDue => Amount * Rate;
}

/// <summary>What is owed on the year's crypto income.</summary>
public sealed record IncomeTaxResult(
    decimal TotalIncome,
    decimal TradingAllowanceUsed,
    decimal TradingAllowanceRemaining,
    decimal IncomeAfterTradingAllowance,
    decimal PersonalAllowance,
    decimal PersonalAllowanceUsedByOtherIncome,
    decimal TaxableIncome,
    IReadOnlyList<IncomeBandResult> Bands,
    decimal TotalTaxDue,
    string Residency)
{
    /// <summary>Tax as a share of the income received, which is below any band's headline rate.</summary>
    public decimal EffectiveRate => TotalIncome <= 0m ? 0m : TotalTaxDue / TotalIncome;

    /// <summary>Whether the personal allowance was tapered away by income over £100,000.</summary>
    public bool PersonalAllowanceTapered => PersonalAllowance < TaxConstants.IncomePersonalAllowance;
}

/// <summary>
/// Works out income tax on crypto received as income — staking rewards, chiefly.
/// <para>
/// This is a separate charge from capital gains and is not interchangeable with it: rewards are
/// taxed as income at the sterling value on the day they arrived, and that same value then becomes
/// their acquisition cost for when they are eventually sold. Reporting one without the other
/// understates the bill.
/// </para>
/// </summary>
public static class IncomeTaxCalculator
{
    /// <summary>The trading income allowance, available from 2017-18 onwards.</summary>
    private const int TradingAllowanceFirstYear = 2017;

    /// <summary>Scottish bands by the tax year they took effect in; a later year uses the last one recorded.</summary>
    private static readonly SortedList<int, IncomeTaxBand[]> ScottishBands = new()
    {
        [2023] =
        [
            new("Starter rate", 2162m, 0.19m),
            new("Basic rate", 13118m, 0.20m),
            new("Intermediate rate", 31092m, 0.21m),
            new("Higher rate", 112570m, 0.42m),
            new("Top rate", decimal.MaxValue, 0.47m),
        ],
        [2024] =
        [
            new("Starter rate", 2306m, 0.19m),
            new("Basic rate", 13991m, 0.20m),
            new("Intermediate rate", 31092m, 0.21m),
            new("Higher rate", 62430m, 0.42m),
            new("Advanced rate", 112570m, 0.45m),
            new("Top rate", decimal.MaxValue, 0.48m),
        ],
        [2025] =
        [
            new("Starter rate", 2827m, 0.19m),
            new("Basic rate", 14921m, 0.20m),
            new("Intermediate rate", 31092m, 0.21m),
            new("Higher rate", 62430m, 0.42m),
            new("Advanced rate", 112570m, 0.45m),
            new("Top rate", decimal.MaxValue, 0.48m),
        ],
        [2026] =
        [
            new("Starter rate", 3967m, 0.19m),
            new("Basic rate", 16956m, 0.20m),
            new("Intermediate rate", 31092m, 0.21m),
            new("Higher rate", 62430m, 0.42m),
            new("Advanced rate", 112570m, 0.45m),
            new("Top rate", decimal.MaxValue, 0.48m),
        ],
    };

    /// <summary>The bands in force for a residency and year, lowest first.</summary>
    public static IReadOnlyList<IncomeTaxBand> BandsFor(TaxResidency residency, UkTaxYear year) =>
        residency == TaxResidency.Scotland ? ScottishBandsFor(year) : RestOfUkBandsFor(year);

    /// <summary>
    /// The rest-of-UK bands, as limits on income measured after the personal allowance.
    /// <para>
    /// The higher-rate ceiling is the £125,140 total-income figure rather than £112,570, and that is
    /// deliberate: by the time income reaches it the allowance has tapered to nothing, so the two
    /// measures have converged and the comparison is exact where it is actually used.
    /// </para>
    /// </summary>
    private static IncomeTaxBand[] RestOfUkBandsFor(UkTaxYear year) =>
    [
        new("Basic rate", TaxConstants.IncomeBasicRateBand, TaxConstants.IncomeBasicRate),
        new("Higher rate", year.StartYear < 2023 ? 150_000m : TaxConstants.IncomeHigherRateUpperBand, TaxConstants.IncomeHigherRate),
        new("Additional rate", decimal.MaxValue, TaxConstants.IncomeAdditionalRate),
    ];

    private static IncomeTaxBand[] ScottishBandsFor(UkTaxYear year)
    {
        if (ScottishBands.TryGetValue(year.StartYear, out var exact)) return exact;

        var effective = ScottishBands.Keys.Where(y => y <= year.StartYear).Cast<int?>().LastOrDefault();
        // Before Scotland's own bands were recorded, the rest-of-UK ones were the same in substance.
        return effective is null ? RestOfUkBandsFor(year) : ScottishBands[effective.Value];
    }

    /// <summary>Whether the Scottish bands for this year are carried forward rather than confirmed.</summary>
    public static bool AreScottishBandsAssumed(UkTaxYear year) => !ScottishBands.ContainsKey(year.StartYear);

    /// <summary>
    /// Calculates income tax on crypto income for the year.
    /// </summary>
    /// <param name="cryptoIncome">Sterling value of income received, at the value on each receipt date.</param>
    /// <param name="year">The tax year.</param>
    /// <param name="otherTaxableIncome">Income taxed before this, which decides which bands are left.</param>
    /// <param name="residency">Which set of bands applies.</param>
    public static IncomeTaxResult Calculate(
        decimal cryptoIncome,
        UkTaxYear year,
        decimal otherTaxableIncome,
        TaxResidency residency)
    {
        // The trading income allowance covers the first £1,000 of it outright.
        decimal tradingAllowanceUsed = year.StartYear >= TradingAllowanceFirstYear
            ? Math.Max(0m, Math.Min(cryptoIncome, TaxConstants.IncomeTradingAllowance))
            : 0m;
        decimal afterTradingAllowance = cryptoIncome - tradingAllowanceUsed;

        // The personal allowance tapers away by £1 for every £2 of income over £100,000, and is gone
        // entirely by £125,140. Forgetting this understates the bill badly at high incomes.
        decimal totalIncome = otherTaxableIncome + afterTradingAllowance;
        decimal personalAllowance = TaxConstants.IncomePersonalAllowance;
        if (totalIncome > TaxConstants.IncomePersonalAllowanceTaperThreshold)
            personalAllowance = Math.Max(0m, personalAllowance - (totalIncome - TaxConstants.IncomePersonalAllowanceTaperThreshold) / 2m);

        decimal allowanceUsedByOther = Math.Min(otherTaxableIncome, personalAllowance);
        decimal otherAfterAllowance = Math.Max(0m, otherTaxableIncome - personalAllowance);
        decimal allowanceRemaining = Math.Max(0m, personalAllowance - otherTaxableIncome);
        decimal taxable = Math.Max(0m, afterTradingAllowance - allowanceRemaining);

        var bands = new List<IncomeBandResult>();
        if (taxable > 0m)
        {
            // Crypto income stacks on top of the other income, so each band only offers the part of
            // itself that sits above what the other income has already used.
            decimal remaining = taxable;
            decimal previousLimit = 0m;
            foreach (var band in BandsFor(residency, year))
            {
                decimal spaceInBand = Math.Max(0m, band.CumulativeUpperLimit - Math.Max(previousLimit, otherAfterAllowance));
                decimal amount = Math.Min(remaining, spaceInBand);
                if (amount > 0m) bands.Add(new IncomeBandResult(band.Name, band.Rate, amount));
                remaining -= amount;
                previousLimit = band.CumulativeUpperLimit;
                if (remaining <= 0m) break;
            }
        }

        return new IncomeTaxResult(
            cryptoIncome,
            tradingAllowanceUsed,
            year.StartYear >= TradingAllowanceFirstYear ? TaxConstants.IncomeTradingAllowance - tradingAllowanceUsed : 0m,
            afterTradingAllowance,
            personalAllowance,
            allowanceUsedByOther,
            taxable,
            bands,
            bands.Sum(b => b.TaxDue),
            residency == TaxResidency.Scotland ? "Scotland" : "Rest of UK");
    }
}
