using System.Globalization;
using System.Text;

namespace KrakenReact.Server.Tax;

/// <summary>
/// Renders a tax report as CSV, in the shape an accountant or a self-assessment tool expects: a
/// summary block, then every disposal as its own row with the rule that matched it.
/// </summary>
public static class TaxReportCsv
{
    /// <summary>Builds the whole report as a single CSV document.</summary>
    public static string Render(TaxReport report)
    {
        var sb = new StringBuilder();

        Section(sb, $"UK tax report {report.Year.Label}");
        Row(sb, "Period", $"6 April {report.Year.StartYear} to 5 April {report.Year.StartYear + 1}");
        Row(sb, "Generated", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        Row(sb, "Source", "KrakenReact — working estimate from Kraken data only, not tax advice");
        sb.AppendLine();

        if (report.Tax is { } tax)
        {
            Section(sb, "Capital gains");
            Row(sb, "Disposal proceeds", Money(tax.DisposalProceeds));
            Row(sb, "Allowable costs", Money(tax.AllowableCosts));
            Row(sb, "Net gain or loss", Money(tax.NetGainOrLoss));
            Row(sb, "Annual exempt amount", Money(tax.AnnualExemptAmount));
            Row(sb, "Losses brought forward", Money(tax.BroughtForwardLosses));
            Row(sb, "Taxable gains", Money(tax.TaxableGains));
            Row(sb, "Basic rate band available", Money(tax.BasicRateBandAvailable));
            if (tax.IsRateChangeYear)
            {
                Row(sb, "Gains at 10% (to 29 Oct 2024)", Money(tax.AmountAtPreviousBasicRate));
                Row(sb, "Gains at 20% (to 29 Oct 2024)", Money(tax.AmountAtPreviousHigherRate));
            }
            Row(sb, "Gains at 18%", Money(tax.AmountAtBasicRate));
            Row(sb, "Gains at 24%", Money(tax.AmountAtHigherRate));
            Row(sb, "Capital gains tax due", Money(tax.TotalTaxDue));
            Row(sb, "Losses to carry forward", Money(tax.LossesToCarryForward));
            if (tax.IsExemptAmountAssumed)
                Row(sb, "Note", "The exempt amount for this year is carried forward, not confirmed — check it before filing.");
            sb.AppendLine();
        }

        if (report.IncomeTax is { TotalIncome: > 0m } incomeTax)
        {
            Section(sb, $"Income ({incomeTax.Residency})");
            Row(sb, "Income received", Money(incomeTax.TotalIncome));
            Row(sb, "Trading allowance used", Money(incomeTax.TradingAllowanceUsed));
            Row(sb, "Personal allowance", Money(incomeTax.PersonalAllowance));
            Row(sb, "Taxable income", Money(incomeTax.TaxableIncome));
            foreach (var band in incomeTax.Bands)
                Row(sb, $"  {band.Name} at {band.Rate * 100m:0.##}%", Money(band.Amount), Money(band.TaxDue));
            Row(sb, "Income tax due", Money(incomeTax.TotalTaxDue));
            sb.AppendLine();
        }

        Section(sb, "Total");
        Row(sb, "Capital gains tax", Money(report.Tax?.TotalTaxDue ?? 0m));
        Row(sb, "Income tax", Money(report.IncomeTax?.TotalTaxDue ?? 0m));
        Row(sb, "Total tax due", Money(report.TotalTaxDue));
        sb.AppendLine();

        if (report.Income.Count > 0)
        {
            Section(sb, "Income by asset");
            Line(sb, "Asset", "Quantity", "Payments", "Value at receipt (GBP)");
            foreach (var row in report.Income)
                Line(sb, row.Asset, Quantity(row.Quantity), row.PaymentCount.ToString(CultureInfo.InvariantCulture), Money(row.ValueGbp));
            sb.AppendLine();
        }

        Section(sb, "Disposals");
        Line(sb, "Sell date", "Asset", "Quantity", "Proceeds (GBP)", "Allowable cost (GBP)",
                 "Sell fee (GBP)", "Gain or loss (GBP)", "Matching rule", "Acquired", "Source");
        foreach (var row in report.Rows)
            Line(sb,
                row.SellDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.Asset,
                Quantity(row.Quantity),
                Money(row.ProceedsGbp),
                Money(row.AllowableCostGbp),
                Money(row.SellFeeGbp),
                Money(row.GainOrLoss),
                RuleLabel(row.Rule),
                row.BuyDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Section 104 pool",
                row.Source);
        sb.AppendLine();

        if (report.UnmatchedDisposals.Count > 0)
        {
            Section(sb, "Disposals with no acquisition in the data — EXCLUDED from the figures above");
            Line(sb, "Sell date", "Asset", "Quantity", "Proceeds not accounted for (GBP)");
            foreach (var row in report.UnmatchedDisposals)
                Line(sb,
                    row.SellDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    row.Asset, Quantity(row.Quantity), Money(row.ProceedsGbp));
            sb.AppendLine();
        }

        if (report.Coverage is { } coverage)
        {
            Section(sb, "Coverage");
            Row(sb, "Trades considered", coverage.TradesConsidered.ToString(CultureInfo.InvariantCulture));
            Row(sb, "Skipped (no GBP rate for the date)", coverage.SkippedForMissingRate.ToString(CultureInfo.InvariantCulture));
            Row(sb, "Skipped (pair not recognised)", coverage.SkippedForUnknownPair.ToString(CultureInfo.InvariantCulture));
            Row(sb, "Amounts using a carried-forward rate", coverage.AmountsUsingCarriedRate.ToString(CultureInfo.InvariantCulture));
            if (coverage.RateFrom is { } from && coverage.RateTo is { } to)
                Row(sb, "GBP rates available", $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd} ({coverage.RateDaysAvailable} days)");
        }

        return sb.ToString();
    }

    /// <summary>A filename that sorts and identifies itself without needing to be opened.</summary>
    public static string FileName(TaxReport report) => $"uk-tax-report-{report.Year.Label}.csv";

    private static string RuleLabel(TaxMatchingRule rule) => rule switch
    {
        TaxMatchingRule.SameDay => "Same day",
        TaxMatchingRule.ThirtyDay => "30 day",
        TaxMatchingRule.Section104Pool => "Section 104 pool",
        _ => rule.ToString(),
    };

    private static void Section(StringBuilder sb, string title) => Line(sb, title);

    private static void Row(StringBuilder sb, params string[] cells) => Line(sb, cells);

    private static void Line(StringBuilder sb, params string[] cells) =>
        sb.AppendLine(string.Join(",", cells.Select(Escape)));

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Quantity(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>
    /// Quotes a field for CSV, and defuses the ones a spreadsheet would treat as formulas.
    /// <para>
    /// A value opening with =, +, - or @ is executed on open by Excel and Sheets. Asset tickers come
    /// from an exchange feed rather than from us, so prefixing an apostrophe is what stops an exported
    /// report from being a delivery mechanism for whatever a ticker happens to contain.
    /// </para>
    /// </summary>
    private static string Escape(string? value)
    {
        string cell = value ?? "";
        if (cell.Length > 0 && (cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) cell = "'" + cell;
        if (cell.Contains('"') || cell.Contains(',') || cell.Contains('\n') || cell.Contains('\r'))
            cell = "\"" + cell.Replace("\"", "\"\"") + "\"";
        return cell;
    }
}
