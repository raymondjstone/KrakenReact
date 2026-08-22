using KrakenReact.Server.Tax;

namespace KrakenReact.Tests;

public class TaxReportCsvTests
{
    private static readonly UkTaxYear Year2024 = new(2024);

    private static TaxReport BuildReport(
        IReadOnlyList<CapitalGainsRow>? rows = null,
        IReadOnlyList<UnmatchedDisposal>? unmatched = null,
        IReadOnlyList<TaxIncomeRow>? income = null)
    {
        rows ??=
        [
            new("BTC", DateTime.Parse("2024-06-01"), DateTime.Parse("2024-06-01"), TaxMatchingRule.SameDay,
                1m, 12_000m, 30m, 10_000m, "Trade", "abc"),
        ];
        income ??= [];

        return new TaxReport(
            Year2024, "ok", null,
            rows,
            CapitalGainsTaxCalculator.Calculate(rows, Year2024, 50_000m, 0m),
            income,
            IncomeTaxCalculator.Calculate(income.Sum(i => i.ValueGbp), Year2024, 50_000m, TaxResidency.RestOfUk),
            unmatched ?? [],
            new TaxCoverage(100, 2, 1, 3, 500, DateTime.Parse("2023-09-18"), DateTime.Parse("2026-08-21")));
    }

    [Fact]
    public void Csv_CarriesTheHeadlineFigures()
    {
        string csv = TaxReportCsv.Render(BuildReport());

        Assert.Contains("UK tax report 2024-25", csv);
        Assert.Contains("Disposal proceeds,12000.00", csv);
        Assert.Contains("Capital gains tax due", csv);
        Assert.Contains("Total tax due", csv);
    }

    [Fact]
    public void Csv_ListsEveryDisposalWithItsRule()
    {
        string csv = TaxReportCsv.Render(BuildReport());

        Assert.Contains("2024-06-01,BTC,1,12000.00,10000.00,30.00,1970.00,Same day", csv);
    }

    [Fact]
    public void Csv_NamesTheThreeMatchingRulesReadably()
    {
        var rows = new List<CapitalGainsRow>
        {
            new("BTC", DateTime.Parse("2024-06-01"), DateTime.Parse("2024-06-01"), TaxMatchingRule.SameDay, 1m, 100m, 0m, 90m, "Trade", "a"),
            new("BTC", DateTime.Parse("2024-06-02"), DateTime.Parse("2024-06-10"), TaxMatchingRule.ThirtyDay, 1m, 100m, 0m, 90m, "Trade", "b"),
            new("BTC", DateTime.Parse("2024-06-03"), null, TaxMatchingRule.Section104Pool, 1m, 100m, 0m, 90m, "Trade", "c"),
        };
        string csv = TaxReportCsv.Render(BuildReport(rows));

        Assert.Contains("Same day", csv);
        Assert.Contains("30 day", csv);
        Assert.Contains("Section 104 pool", csv);
    }

    [Fact]
    public void Csv_SaysWhenADisposalHasNoAcquisition()
    {
        var unmatched = new List<UnmatchedDisposal>
        {
            new("POL", DateTime.Parse("2024-11-07"), 8171.156m, 1143.76m),
        };
        string csv = TaxReportCsv.Render(BuildReport(unmatched: unmatched));

        Assert.Contains("EXCLUDED from the figures above", csv);
        Assert.Contains("POL", csv);
        Assert.Contains("1143.76", csv);
    }

    [Fact]
    public void Csv_OmitsTheUnmatchedSectionWhenThereIsNothingToSay()
    {
        Assert.DoesNotContain("EXCLUDED", TaxReportCsv.Render(BuildReport()));
    }

    [Fact]
    public void Csv_IncludesTheIncomeBreakdownWhenThereIsIncome()
    {
        var income = new List<TaxIncomeRow> { new("SOL", 1.774m, 2_500m, 50) };
        string csv = TaxReportCsv.Render(BuildReport(income: income));

        Assert.Contains("Income by asset", csv);
        Assert.Contains("SOL", csv);
        Assert.Contains("Income tax due", csv);
    }

    [Fact]
    public void Csv_ReportsCoverageSoGapsTravelWithTheNumbers()
    {
        string csv = TaxReportCsv.Render(BuildReport());

        Assert.Contains("Skipped (no GBP rate for the date),2", csv);
        Assert.Contains("Skipped (pair not recognised),1", csv);
        Assert.Contains("GBP rates available", csv);
    }

    // ── Escaping ────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_DefusesAFieldASpreadsheetWouldRunAsAFormula()
    {
        // Tickers come from an exchange feed, not from us. A value opening with '=' is executed on
        // open by Excel and Sheets, so an exported report must not be a delivery mechanism for it.
        var rows = new List<CapitalGainsRow>
        {
            new("=HYPERLINK(\"http://evil\",\"x\")", DateTime.Parse("2024-06-01"), null, TaxMatchingRule.Section104Pool,
                1m, 100m, 0m, 90m, "Trade", "a"),
        };
        string csv = TaxReportCsv.Render(BuildReport(rows));

        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.Contains("'=HYPERLINK", csv);
    }

    [Theory]
    [InlineData("+SUM(A1)")]
    [InlineData("-2+3")]
    [InlineData("@import")]
    public void Csv_DefusesEveryFormulaLeadCharacter(string ticker)
    {
        var rows = new List<CapitalGainsRow>
        {
            new(ticker, DateTime.Parse("2024-06-01"), null, TaxMatchingRule.Section104Pool, 1m, 100m, 0m, 90m, "Trade", "a"),
        };
        string csv = TaxReportCsv.Render(BuildReport(rows));
        Assert.Contains("'" + ticker[0], csv);
    }

    [Fact]
    public void Csv_QuotesAFieldContainingAComma()
    {
        var rows = new List<CapitalGainsRow>
        {
            new("A,B", DateTime.Parse("2024-06-01"), null, TaxMatchingRule.Section104Pool, 1m, 100m, 0m, 90m, "Trade", "a"),
        };
        Assert.Contains("\"A,B\"", TaxReportCsv.Render(BuildReport(rows)));
    }

    [Fact]
    public void Csv_DoublesAnEmbeddedQuote()
    {
        var rows = new List<CapitalGainsRow>
        {
            new("A\"B", DateTime.Parse("2024-06-01"), null, TaxMatchingRule.Section104Pool, 1m, 100m, 0m, 90m, "Trade", "a"),
        };
        Assert.Contains("\"A\"\"B\"", TaxReportCsv.Render(BuildReport(rows)));
    }

    [Fact]
    public void Csv_UsesInvariantNumbersWhateverTheMachineLocaleIs()
    {
        // A machine set to a comma decimal separator would otherwise emit "1970,00" and split the row.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            string csv = TaxReportCsv.Render(BuildReport());
            Assert.Contains("12000.00", csv);
            Assert.DoesNotContain("12000,00", csv);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void FileName_NamesTheYearItCovers()
    {
        Assert.Equal("uk-tax-report-2024-25.csv", TaxReportCsv.FileName(BuildReport()));
    }
}
