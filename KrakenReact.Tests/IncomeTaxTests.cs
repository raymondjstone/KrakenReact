using KrakenReact.Server.Tax;

namespace KrakenReact.Tests;

/// <summary>
/// Worked examples for income tax on crypto received as income. Every expected figure is one a person
/// could check on paper against HMRC's own rates.
/// </summary>
public class IncomeTaxTests
{
    private static readonly UkTaxYear Year2024 = new(2024);
    private static readonly UkTaxYear Year2016 = new(2016);

    private static IncomeTaxResult Calc(decimal cryptoIncome, decimal otherIncome, TaxResidency residency = TaxResidency.RestOfUk, UkTaxYear? year = null) =>
        IncomeTaxCalculator.Calculate(cryptoIncome, year ?? Year2024, otherIncome, residency);

    // ── Trading allowance ───────────────────────────────────────────────────

    [Fact]
    public void TradingAllowance_CoversTheFirstThousandOutright()
    {
        var result = Calc(cryptoIncome: 800m, otherIncome: 50_000m);
        Assert.Equal(800m, result.TradingAllowanceUsed);
        Assert.Equal(0m, result.IncomeAfterTradingAllowance);
        Assert.Equal(0m, result.TotalTaxDue);
        Assert.Equal(200m, result.TradingAllowanceRemaining);
    }

    [Fact]
    public void TradingAllowance_OnlyCoversTheFirstThousand()
    {
        // £2,500 of rewards, £1,000 allowance, £1,500 taxable at 20%.
        var result = Calc(cryptoIncome: 2_500m, otherIncome: 30_000m);
        Assert.Equal(1_000m, result.TradingAllowanceUsed);
        Assert.Equal(1_500m, result.TaxableIncome);
        Assert.Equal(300m, result.TotalTaxDue);
    }

    [Fact]
    public void TradingAllowance_DidNotExistBefore2017()
    {
        var result = Calc(cryptoIncome: 800m, otherIncome: 30_000m, year: Year2016);
        Assert.Equal(0m, result.TradingAllowanceUsed);
        Assert.Equal(800m, result.TaxableIncome);
        Assert.Equal(160m, result.TotalTaxDue);
    }

    // ── Personal allowance ──────────────────────────────────────────────────

    [Fact]
    public void PersonalAllowance_ShieldsIncomeWhenThereIsNoOtherIncome()
    {
        // £5,000 of rewards less the £1,000 trading allowance is £4,000, well inside the £12,570
        // personal allowance, so nothing is due.
        var result = Calc(cryptoIncome: 5_000m, otherIncome: 0m);
        Assert.Equal(0m, result.TaxableIncome);
        Assert.Equal(0m, result.TotalTaxDue);
    }

    [Fact]
    public void PersonalAllowance_IsSharedWithOtherIncomeFirst()
    {
        // Other income of £10,000 leaves £2,570 of allowance. £5,000 of rewards less the £1,000
        // trading allowance is £4,000, of which £2,570 is sheltered and £1,430 taxed at 20%.
        var result = Calc(cryptoIncome: 5_000m, otherIncome: 10_000m);
        Assert.Equal(1_430m, result.TaxableIncome);
        Assert.Equal(286m, result.TotalTaxDue);
    }

    [Fact]
    public void PersonalAllowance_TapersAwayAboveAHundredThousand()
    {
        // £110,000 of total income tapers the allowance by £5,000 to £7,570.
        var result = Calc(cryptoIncome: 1_000m, otherIncome: 110_000m);
        Assert.True(result.PersonalAllowanceTapered);
        Assert.Equal(7_570m, result.PersonalAllowance);
    }

    [Fact]
    public void PersonalAllowance_IsGoneEntirelyAtTheAdditionalRateThreshold()
    {
        var result = Calc(cryptoIncome: 1_000m, otherIncome: 130_000m);
        Assert.Equal(0m, result.PersonalAllowance);
        Assert.True(result.PersonalAllowanceTapered);
    }

    // ── Banding ─────────────────────────────────────────────────────────────

    [Fact]
    public void Bands_ChargeTheBasicRateWhenIncomeIsModest()
    {
        var result = Calc(cryptoIncome: 3_000m, otherIncome: 30_000m);
        var band = Assert.Single(result.Bands);
        Assert.Equal("Basic rate", band.Name);
        Assert.Equal(0.20m, band.Rate);
        Assert.Equal(2_000m, band.Amount);          // 3,000 less the trading allowance
        Assert.Equal(400m, result.TotalTaxDue);
    }

    [Fact]
    public void Bands_ChargeTheHigherRateWhenTheBasicBandIsSpent()
    {
        // £60,000 of other income is £47,430 after the allowance, well past the £37,700 basic band,
        // so all of the crypto income sits in the higher rate.
        var result = Calc(cryptoIncome: 6_000m, otherIncome: 60_000m);
        var band = Assert.Single(result.Bands);
        Assert.Equal("Higher rate", band.Name);
        Assert.Equal(5_000m, band.Amount);
        Assert.Equal(2_000m, result.TotalTaxDue);   // 5,000 at 40%
    }

    [Fact]
    public void Bands_SplitIncomeThatStraddlesTwoBands()
    {
        // £45,000 of other income is £32,430 after the allowance, leaving £5,270 of basic band.
        // £11,000 of rewards less the trading allowance is £10,000: £5,270 at 20%, £4,730 at 40%.
        var result = Calc(cryptoIncome: 11_000m, otherIncome: 45_000m);

        Assert.Equal(2, result.Bands.Count);
        Assert.Equal("Basic rate", result.Bands[0].Name);
        Assert.Equal(5_270m, result.Bands[0].Amount);
        Assert.Equal("Higher rate", result.Bands[1].Name);
        Assert.Equal(4_730m, result.Bands[1].Amount);
        Assert.Equal(5_270m * 0.20m + 4_730m * 0.40m, result.TotalTaxDue);
    }

    [Fact]
    public void Bands_ReachTheAdditionalRateOnVeryHighIncome()
    {
        var result = Calc(cryptoIncome: 20_000m, otherIncome: 140_000m);
        Assert.Contains(result.Bands, b => b.Name == "Additional rate");
        Assert.Equal(0.45m, result.Bands.Last().Rate);
    }

    [Fact]
    public void Bands_AreEmptyWhenNothingIsTaxable()
    {
        var result = Calc(cryptoIncome: 500m, otherIncome: 0m);
        Assert.Empty(result.Bands);
        Assert.Equal(0m, result.TotalTaxDue);
    }

    [Fact]
    public void Bands_AccountForEveryTaxablePound()
    {
        var result = Calc(cryptoIncome: 30_000m, otherIncome: 40_000m);
        Assert.Equal(result.TaxableIncome, result.Bands.Sum(b => b.Amount));
    }

    // ── Scotland ────────────────────────────────────────────────────────────

    [Fact]
    public void Scotland_UsesItsOwnBands()
    {
        // Scotland's 2024-25 table has six bands where the rest of the UK has three, and the rates
        // differ, so the same income must not produce the same bill.
        var scottish = Calc(cryptoIncome: 20_000m, otherIncome: 40_000m, residency: TaxResidency.Scotland);
        var restOfUk = Calc(cryptoIncome: 20_000m, otherIncome: 40_000m);

        Assert.Equal("Scotland", scottish.Residency);
        Assert.Equal("Rest of UK", restOfUk.Residency);
        Assert.NotEqual(restOfUk.TotalTaxDue, scottish.TotalTaxDue);
    }

    [Fact]
    public void Scotland_ChargesTheAdvancedRateItAloneHas()
    {
        var bands = IncomeTaxCalculator.BandsFor(TaxResidency.Scotland, Year2024);
        Assert.Contains(bands, b => b.Name == "Advanced rate");
        Assert.DoesNotContain(IncomeTaxCalculator.BandsFor(TaxResidency.RestOfUk, Year2024), b => b.Name == "Advanced rate");
    }

    [Fact]
    public void Scotland_CarriesTheLatestBandsForwardButSaysSo()
    {
        var future = new UkTaxYear(2030);
        Assert.NotEmpty(IncomeTaxCalculator.BandsFor(TaxResidency.Scotland, future));
        Assert.True(IncomeTaxCalculator.AreScottishBandsAssumed(future));
        Assert.False(IncomeTaxCalculator.AreScottishBandsAssumed(Year2024));
    }

    [Fact]
    public void Scotland_FallsBackToRestOfUkBeforeItsOwnAreRecorded()
    {
        // Nothing is recorded before 2023-24, so an older year must still band rather than throw.
        var bands = IncomeTaxCalculator.BandsFor(TaxResidency.Scotland, new UkTaxYear(2019));
        Assert.NotEmpty(bands);
        Assert.Contains(bands, b => b.Name == "Basic rate");
    }

    // ── Reported figures ────────────────────────────────────────────────────

    [Fact]
    public void EffectiveRate_IsBelowTheHeadlineRate()
    {
        // Allowances mean the bill is always a smaller share of the income than the band rate.
        var result = Calc(cryptoIncome: 6_000m, otherIncome: 60_000m);
        Assert.True(result.EffectiveRate < TaxConstants.IncomeHigherRate);
        Assert.Equal(Math.Round(2_000m / 6_000m, 6), Math.Round(result.EffectiveRate, 6));
    }

    [Fact]
    public void EffectiveRate_IsZeroWithNoIncome()
    {
        var result = Calc(cryptoIncome: 0m, otherIncome: 50_000m);
        Assert.Equal(0m, result.EffectiveRate);
        Assert.Equal(0m, result.TotalTaxDue);
    }

    [Fact]
    public void HigherRateCeiling_WidensForYearsBefore2023()
    {
        // The additional rate began at £150,000 until 2023-24, when it dropped to £125,140.
        Assert.Equal(150_000m, IncomeTaxCalculator.BandsFor(TaxResidency.RestOfUk, new UkTaxYear(2022))[1].CumulativeUpperLimit);
        Assert.Equal(125_140m, IncomeTaxCalculator.BandsFor(TaxResidency.RestOfUk, new UkTaxYear(2023))[1].CumulativeUpperLimit);
    }
}
