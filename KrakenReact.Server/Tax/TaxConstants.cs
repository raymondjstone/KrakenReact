namespace KrakenReact.Server.Tax;

/// <summary>
/// UK capital gains and income tax figures, by the tax year they took effect in.
/// <para>
/// These are statutory numbers that change at most once a year. They are held here rather than in the
/// database so a report run for a past year keeps reproducing the same answer.
/// </para>
/// </summary>
public static class TaxConstants
{
    /// <summary>CGT rates before 30 October 2024.</summary>
    public const decimal CapitalGainsPreviousBasicRate = 0.10m;
    public const decimal CapitalGainsPreviousHigherRate = 0.20m;

    /// <summary>CGT rates from 30 October 2024, announced at that Budget.</summary>
    public const decimal CapitalGainsBasicRate = 0.18m;
    public const decimal CapitalGainsHigherRate = 0.24m;

    /// <summary>
    /// The date the new rates took effect. Disposals before it in the 2024-25 year are taxed at the
    /// old rates, which is why that one year needs splitting rather than a single rate.
    /// </summary>
    public static readonly DateTime CapitalGainsRateChangeDate = new(2024, 10, 30);

    public const decimal IncomePersonalAllowance = 12570m;

    /// <summary>
    /// Income above which the personal allowance tapers away, by £1 for every £2 over. It is gone
    /// entirely at £125,140, which is also where the additional rate begins.
    /// </summary>
    public const decimal IncomePersonalAllowanceTaperThreshold = 100_000m;

    /// <summary>The trading income allowance, which covers the first £1,000 of casual income outright.</summary>
    public const decimal IncomeTradingAllowance = 1000m;
    public const decimal IncomeBasicRateBand = 37700m;
    public const decimal IncomeBasicRate = 0.20m;
    public const decimal IncomeHigherRate = 0.40m;
    public const decimal IncomeAdditionalRate = 0.45m;
    public const decimal IncomeHigherRateUpperBand = 125140m;

    /// <summary>The annual CGT exempt amount, keyed by the tax year it first applied to.</summary>
    private static readonly SortedList<int, decimal> AnnualExemptAmounts = new()
    {
        [2016] = 11100m,
        [2017] = 11300m,
        [2018] = 11700m,
        [2019] = 12000m,
        [2020] = 12300m,
        [2021] = 12300m,
        [2022] = 12300m,
        [2023] = 6000m,
        [2024] = 3000m,
    };

    /// <summary>
    /// The CGT annual exempt amount for a tax year. A year later than the last one recorded carries
    /// the most recent figure forward, which is right until the Chancellor changes it — see
    /// <see cref="IsExemptAmountAssumed"/> for telling the reader when that has happened.
    /// </summary>
    public static decimal AnnualExemptAmount(UkTaxYear year)
    {
        if (AnnualExemptAmounts.TryGetValue(year.StartYear, out var exact)) return exact;

        var effective = AnnualExemptAmounts.Keys.Where(y => y <= year.StartYear).Cast<int?>().LastOrDefault();
        if (effective is null)
            throw new ArgumentOutOfRangeException(nameof(year),
                $"No CGT annual exempt amount is recorded for {year.Label} or any earlier year.");
        return AnnualExemptAmounts[effective.Value];
    }

    /// <summary>Whether the exempt amount for this year is carried forward rather than confirmed.</summary>
    public static bool IsExemptAmountAssumed(UkTaxYear year) => !AnnualExemptAmounts.ContainsKey(year.StartYear);

    /// <summary>The most recent tax year whose exempt amount is recorded rather than assumed.</summary>
    public static int LastConfirmedExemptYear => AnnualExemptAmounts.Keys[^1];

    /// <summary>Whether this year straddles the 30 October 2024 rate change.</summary>
    public static bool IsRateChangeYear(UkTaxYear year) => year.StartYear == 2024;

    /// <summary>Whether the whole of this year predates the rate change.</summary>
    public static bool IsPreviousRateYear(UkTaxYear year) => year.StartYear < 2024;

    public static decimal BasicRateFor(UkTaxYear year) =>
        IsPreviousRateYear(year) ? CapitalGainsPreviousBasicRate : CapitalGainsBasicRate;

    public static decimal HigherRateFor(UkTaxYear year) =>
        IsPreviousRateYear(year) ? CapitalGainsPreviousHigherRate : CapitalGainsHigherRate;
}
