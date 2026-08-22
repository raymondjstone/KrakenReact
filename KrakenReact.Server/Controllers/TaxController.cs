using KrakenReact.Server.Tax;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Controllers;

[ApiController]
[Route("api/tax")]
public class TaxController : ControllerBase
{
    private readonly TaxReportService _tax;

    public TaxController(TaxReportService tax) => _tax = tax;

    /// <summary>GET /api/tax/years — the UK tax years the stored trade history covers</summary>
    [HttpGet("years")]
    public async Task<IActionResult> GetYears(CancellationToken ct)
    {
        try
        {
            var years = await _tax.ListTaxYearsAsync(ct);
            return Ok(years.Select(y => new
            {
                startYear = y.StartYear,
                label = y.Label,
                start = y.InclusiveStart,
                end = y.ExclusiveEnd,
                exemptAmount = TaxConstants.AnnualExemptAmount(y),
                isExemptAmountAssumed = TaxConstants.IsExemptAmountAssumed(y),
                isRateChangeYear = TaxConstants.IsRateChangeYear(y),
            }));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/tax/report.csv?year=2024 — the same report as a CSV download.
    /// </summary>
    [HttpGet("report.csv")]
    public async Task<IActionResult> GetReportCsv(
        [FromQuery] int year,
        [FromQuery] decimal otherTaxableIncome = 0m,
        [FromQuery] decimal broughtForwardLosses = 0m,
        [FromQuery] bool scottishRates = false,
        CancellationToken ct = default)
    {
        if (year < 2009 || year > DateTime.UtcNow.Year + 1)
            return BadRequest(new { message = $"{year} is not a plausible tax year." });

        try
        {
            var report = await _tax.BuildAsync(
                new UkTaxYear(year),
                Math.Max(0m, otherTaxableIncome),
                Math.Max(0m, broughtForwardLosses),
                scottishRates ? TaxResidency.Scotland : TaxResidency.RestOfUk,
                ct);

            if (report.Status != "ok")
                return BadRequest(new { message = report.Message ?? "The report could not be built." });

            // A BOM so Excel opens the file as UTF-8 rather than mangling the pound signs.
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(System.Text.Encoding.UTF8.GetBytes(TaxReportCsv.Render(report)))
                .ToArray();

            return File(bytes, "text/csv", TaxReportCsv.FileName(report));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/tax/report?year=2024 — the capital gains report for one UK tax year.
    /// </summary>
    /// <param name="year">The tax year's starting calendar year, so 2024 means 2024-25.</param>
    /// <param name="otherTaxableIncome">Income taxed before the gain, which sets how much basic rate band is left.</param>
    /// <param name="broughtForwardLosses">Unused capital losses carried in from earlier years.</param>
    [HttpGet("report")]
    public async Task<IActionResult> GetReport(
        [FromQuery] int year,
        [FromQuery] decimal otherTaxableIncome = 0m,
        [FromQuery] decimal broughtForwardLosses = 0m,
        [FromQuery] bool scottishRates = false,
        CancellationToken ct = default)
    {
        if (year < 2009 || year > DateTime.UtcNow.Year + 1)
            return BadRequest(new { message = $"{year} is not a plausible tax year." });

        try
        {
            var report = await _tax.BuildAsync(
                new UkTaxYear(year),
                Math.Max(0m, otherTaxableIncome),
                Math.Max(0m, broughtForwardLosses),
                scottishRates ? TaxResidency.Scotland : TaxResidency.RestOfUk,
                ct);

            return Ok(new
            {
                year = report.Year.Label,
                startYear = report.Year.StartYear,
                report.Status,
                report.Message,
                tax = report.Tax,
                income = report.Income,
                incomeTax = report.IncomeTax,
                totalTaxDue = Math.Round(report.TotalTaxDue, 2),
                scottishBandsAssumed = scottishRates && IncomeTaxCalculator.AreScottishBandsAssumed(report.Year),
                coverage = report.Coverage,
                unmatched = report.UnmatchedDisposals.Select(u => new
                {
                    u.Asset,
                    u.SellDate,
                    u.Quantity,
                    proceedsGbp = Math.Round(u.ProceedsGbp, 2),
                }),
                rows = report.Rows.Select(r => new
                {
                    r.Asset,
                    r.SellDate,
                    r.BuyDate,
                    rule = r.Rule.ToString(),
                    r.Quantity,
                    proceedsGbp = Math.Round(r.ProceedsGbp, 2),
                    sellFeeGbp = Math.Round(r.SellFeeGbp, 2),
                    allowableCostGbp = Math.Round(r.AllowableCostGbp, 2),
                    gainOrLoss = Math.Round(r.GainOrLoss, 2),
                    r.Source,
                }),
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
