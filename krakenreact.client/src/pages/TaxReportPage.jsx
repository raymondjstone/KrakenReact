import { useState, useEffect, useCallback, useMemo } from 'react';
import api from '../api/apiClient';

const RULE_LABEL = {
  SameDay: 'Same day',
  ThirtyDay: '30 day',
  Section104Pool: 'S.104 pool',
};

const RULE_HINT = {
  SameDay: 'Matched to an acquisition of the same asset on the same day (TCGA 1992 s.105).',
  ThirtyDay: 'Matched to an acquisition in the following 30 days — the bed-and-breakfast rule (s.106A).',
  Section104Pool: 'Matched against the pooled average cost of the rest of the holding (s.104).',
};

function gbp(value, decimals = 2) {
  if (value == null) return '—';
  const n = Number(value);
  return `${n < 0 ? '-' : ''}£${Math.abs(n).toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals })}`;
}

function qty(value) {
  if (value == null) return '—';
  return Number(value).toLocaleString(undefined, { maximumFractionDigits: 8 });
}

function shortDate(value) {
  if (!value) return '—';
  return new Date(value).toLocaleDateString('en-GB', { year: '2-digit', month: 'short', day: 'numeric' });
}

const card = {
  background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8,
  padding: '14px 16px', display: 'flex', flexDirection: 'column', gap: 10, minWidth: 0,
};
const th = { padding: '4px 8px', textAlign: 'right', whiteSpace: 'nowrap', fontWeight: 600, color: 'var(--text-muted)', fontSize: 11 };
const td = { padding: '3px 8px', textAlign: 'right', whiteSpace: 'nowrap', fontSize: 11.5 };
const tableStyle = { width: '100%', borderCollapse: 'collapse' };

function Stat({ label, value, color, hint }) {
  return (
    <div style={{ minWidth: 0 }}>
      <div style={{ fontSize: 10, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: 0.4 }}>{label}</div>
      <div style={{ fontSize: 17, fontWeight: 700, color: color || 'var(--text-primary)', whiteSpace: 'nowrap' }}>{value}</div>
      {hint && <div style={{ fontSize: 10, color: 'var(--text-muted)' }}>{hint}</div>}
    </div>
  );
}

function Note({ tone = 'warn', children }) {
  const accent = tone === 'warn' ? 'var(--yellow)' : 'var(--red)';
  return (
    <div style={{
      fontSize: 11, lineHeight: 1.55, padding: '7px 10px', borderRadius: 5,
      color: 'var(--text-secondary)',
      background: `color-mix(in srgb, ${accent} 10%, transparent)`,
      border: `1px solid color-mix(in srgb, ${accent} 35%, var(--border))`,
    }}>{children}</div>
  );
}

const statGrid = { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(130px, 1fr))', gap: '10px 16px' };

export default function TaxReportPage() {
  const [years, setYears] = useState([]);
  const [startYear, setStartYear] = useState(null);
  const [income, setIncome] = useState(0);
  const [losses, setLosses] = useState(0);
  const [report, setReport] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [ruleFilter, setRuleFilter] = useState('All');
  const [scottish, setScottish] = useState(false);

  useEffect(() => {
    let disposed = false;
    api.get('/tax/years')
      .then(r => {
        if (disposed) return;
        const list = r.data || [];
        setYears(list);
        if (list.length) setStartYear(prev => prev ?? list[0].startYear);
      })
      .catch(() => setError('Could not list tax years'));
    return () => { disposed = true; };
  }, []);

  const load = useCallback(() => {
    if (startYear == null) return;
    setLoading(true);
    const query = new URLSearchParams({
      year: String(startYear),
      otherTaxableIncome: String(income || 0),
      broughtForwardLosses: String(losses || 0),
      scottishRates: String(scottish),
    });
    api.get(`/tax/report?${query}`)
      .then(r => { setReport(r.data); setError(''); setLoading(false); })
      .catch(e => {
        setReport(null);
        setError(e.response?.data?.error || 'Could not build the report');
        setLoading(false);
      });
  }, [startYear, income, losses, scottish]);

  // The CSV comes back as a blob so the browser saves it with the filename the server chose,
  // rather than navigating away from the app to fetch it.
  const downloadCsv = useCallback(async () => {
    if (startYear == null) return;
    const query = new URLSearchParams({
      year: String(startYear),
      otherTaxableIncome: String(income || 0),
      broughtForwardLosses: String(losses || 0),
      scottishRates: String(scottish),
    });
    try {
      const res = await api.get(`/tax/report.csv?${query}`, { responseType: 'blob' });
      const url = URL.createObjectURL(new Blob([res.data], { type: 'text/csv' }));
      const link = document.createElement('a');
      link.href = url;
      link.download = `uk-tax-report-${report?.year || startYear}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch {
      setError('Could not export the report');
    }
  }, [startYear, income, losses, scottish, report]);

  // Debounced: typing an income figure shouldn't re-match the whole trade history per keystroke.
  useEffect(() => {
    const timer = setTimeout(load, 400);
    return () => clearTimeout(timer);
  }, [load]);

  const tax = report?.tax;
  const rows = useMemo(() => {
    const all = report?.rows || [];
    return ruleFilter === 'All' ? all : all.filter(r => r.rule === ruleFilter);
  }, [report, ruleFilter]);

  const ruleCounts = useMemo(() => {
    const counts = { SameDay: 0, ThirtyDay: 0, Section104Pool: 0 };
    for (const r of report?.rows || []) counts[r.rule] = (counts[r.rule] || 0) + 1;
    return counts;
  }, [report]);

  const selectedYear = years.find(y => y.startYear === startYear);
  const coverage = report?.coverage;
  const hasGaps = coverage && (coverage.skippedForMissingRate > 0 || coverage.skippedForUnknownPair > 0);

  const inputStyle = {
    padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4,
    background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 12,
  };
  const labelStyle = { display: 'flex', gap: 6, alignItems: 'center', fontSize: 12, color: 'var(--text-muted)' };

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: 'var(--bg-primary)' }}>
      <div style={{
        padding: '6px 12px', borderBottom: '1px solid var(--border)', background: 'var(--bg-secondary)',
        display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap',
      }}>
        <label style={labelStyle}>
          Tax year
          <select value={startYear ?? ''} onChange={e => setStartYear(Number(e.target.value))} style={inputStyle}>
            {years.map(y => <option key={y.startYear} value={y.startYear}>{y.label}</option>)}
          </select>
        </label>
        <label style={labelStyle} title="Your other taxable income for the year — it decides how much of the basic rate band is left for gains.">
          Other income £
          <input type="number" min="0" step="1000" value={income}
                 onChange={e => setIncome(Number(e.target.value))} style={{ ...inputStyle, width: 90 }} />
        </label>
        <label style={labelStyle} title="Unused capital losses carried in from earlier years.">
          Losses b/f £
          <input type="number" min="0" step="500" value={losses}
                 onChange={e => setLosses(Number(e.target.value))} style={{ ...inputStyle, width: 90 }} />
        </label>
        <label style={{ ...labelStyle, cursor: 'pointer' }} title="Scotland sets its own income tax bands — six of them, at different rates.">
          <input type="checkbox" checked={scottish} onChange={e => setScottish(e.target.checked)} />
          Scottish rates
        </label>
        {report?.status === 'ok' && (
          <button
            onClick={downloadCsv}
            title="Download the whole report as CSV, for a spreadsheet or an accountant."
            style={{ ...inputStyle, cursor: 'pointer', background: 'var(--bg-card)' }}
          >
            Export CSV
          </button>
        )}
        {loading && <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>Matching disposals…</span>}
        {error && <span style={{ fontSize: 12, color: 'var(--red)' }}>{error}</span>}
        {selectedYear && !loading && (
          <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>
            6 Apr {selectedYear.startYear} – 5 Apr {selectedYear.startYear + 1}
          </span>
        )}
      </div>

      <div style={{ flex: 1, overflow: 'auto', padding: 12, display: 'flex', flexDirection: 'column', gap: 12 }}>
        {!report && !loading && !error && (
          <div style={{ padding: 24, color: 'var(--text-muted)', fontSize: 13 }}>Pick a tax year.</div>
        )}

        {report?.status === 'unavailable' && <Note tone="err">{report.message}</Note>}

        {report?.status === 'ok' && tax && (
          <>
            <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fit, minmax(420px, 1fr))' }}>
              <div style={card}>
                <h3 style={{ margin: 0, fontSize: 13, letterSpacing: 0.4, textTransform: 'uppercase', color: 'var(--text-secondary)' }}>
                  Capital gains {report.year}
                </h3>
                <div style={statGrid}>
                  <Stat label="Disposal proceeds" value={gbp(tax.disposalProceeds, 0)} hint={`${report.rows.length} disposals`} />
                  <Stat label="Allowable costs" value={gbp(tax.allowableCosts, 0)} />
                  <Stat label="Net gain" value={gbp(tax.netGainOrLoss, 0)}
                        color={tax.netGainOrLoss >= 0 ? 'var(--green)' : 'var(--red)'} />
                  <Stat label="Exempt amount" value={gbp(tax.annualExemptAmount, 0)}
                        hint={tax.isExemptAmountAssumed ? 'carried forward — unconfirmed' : undefined}
                        color={tax.isExemptAmountAssumed ? 'var(--yellow)' : undefined} />
                  <Stat label="Taxable gains" value={gbp(tax.taxableGains, 0)} />
                  <Stat label="Tax due" value={gbp(tax.totalTaxDue)}
                        color={tax.totalTaxDue > 0 ? 'var(--red)' : 'var(--green)'}
                        hint={tax.netGainOrLoss > 0 ? `${(tax.effectiveRate * 100).toFixed(1)}% effective` : undefined} />
                </div>

                {tax.lossesToCarryForward > 0 && (
                  <div style={{ fontSize: 11.5, color: 'var(--text-secondary)' }}>
                    Losses to carry forward: <strong style={{ color: 'var(--yellow)' }}>{gbp(tax.lossesToCarryForward)}</strong>
                  </div>
                )}

                {tax.isExemptAmountAssumed && (
                  <Note>
                    No confirmed CGT annual exempt amount is recorded for {report.year}; the most recent one has
                    been carried forward. Check the figure against HMRC before filing.
                  </Note>
                )}
              </div>

              <div style={card}>
                <h3 style={{ margin: 0, fontSize: 13, letterSpacing: 0.4, textTransform: 'uppercase', color: 'var(--text-secondary)' }}>
                  How the tax breaks down
                </h3>
                <table style={tableStyle}>
                  <thead>
                    <tr>
                      <th style={{ ...th, textAlign: 'left' }}>Band</th>
                      <th style={th}>Rate</th>
                      <th style={th}>Gains</th>
                      <th style={th}>Tax</th>
                    </tr>
                  </thead>
                  <tbody>
                    {[
                      ['Basic rate (to 29 Oct 24)', 10, tax.amountAtPreviousBasicRate, tax.taxAtPreviousBasicRate],
                      ['Higher rate (to 29 Oct 24)', 20, tax.amountAtPreviousHigherRate, tax.taxAtPreviousHigherRate],
                      ['Basic rate', tax.isRateChangeYear ? 18 : 18, tax.amountAtBasicRate, tax.taxAtBasicRate],
                      ['Higher rate', 24, tax.amountAtHigherRate, tax.taxAtHigherRate],
                    ].filter(([, , amount]) => amount > 0).map(([label, rate, amount, due]) => (
                      <tr key={label} style={{ borderTop: '1px solid var(--border)' }}>
                        <td style={{ ...td, textAlign: 'left', color: 'var(--text-secondary)' }}>{label}</td>
                        <td style={{ ...td, color: 'var(--text-muted)' }}>{rate}%</td>
                        <td style={td}>{gbp(amount, 0)}</td>
                        <td style={{ ...td, fontWeight: 600 }}>{gbp(due)}</td>
                      </tr>
                    ))}
                    {tax.totalTaxDue === 0 && (
                      <tr><td colSpan={4} style={{ ...td, textAlign: 'left', color: 'var(--text-muted)' }}>
                        Nothing taxable this year.
                      </td></tr>
                    )}
                  </tbody>
                </table>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.55 }}>
                  Basic rate band left after your other income: <strong>{gbp(tax.basicRateBandAvailable, 0)}</strong>.
                  {tax.isRateChangeYear && ' Rates rose on 30 October 2024, so this year is split at that date and the band is set against the earlier, cheaper gains first.'}
                </div>
              </div>
            </div>

            {report.incomeTax && report.incomeTax.totalIncome > 0 && (
              <div style={card}>
                <h3 style={{ margin: 0, fontSize: 13, letterSpacing: 0.4, textTransform: 'uppercase', color: 'var(--text-secondary)' }}>
                  Staking income — taxed as income, not gains
                </h3>

                <div style={statGrid}>
                  <Stat label="Income received" value={gbp(report.incomeTax.totalIncome)} />
                  <Stat label="Trading allowance" value={gbp(report.incomeTax.tradingAllowanceUsed, 0)}
                        hint={report.incomeTax.tradingAllowanceRemaining > 0 ? `${gbp(report.incomeTax.tradingAllowanceRemaining, 0)} unused` : 'fully used'} />
                  <Stat label="Personal allowance" value={gbp(report.incomeTax.personalAllowance, 0)}
                        color={report.incomeTax.personalAllowanceTapered ? 'var(--yellow)' : undefined}
                        hint={report.incomeTax.personalAllowanceTapered ? 'tapered by income over £100k' : undefined} />
                  <Stat label="Taxable" value={gbp(report.incomeTax.taxableIncome)} />
                  <Stat label="Income tax due" value={gbp(report.incomeTax.totalTaxDue)}
                        color={report.incomeTax.totalTaxDue > 0 ? 'var(--red)' : 'var(--green)'}
                        hint={report.incomeTax.totalIncome > 0 ? `${(report.incomeTax.effectiveRate * 100).toFixed(1)}% effective` : undefined} />
                </div>

                {report.incomeTax.bands.length > 0 && (
                  <table style={tableStyle}>
                    <thead>
                      <tr>
                        <th style={{ ...th, textAlign: 'left' }}>Band ({report.incomeTax.residency})</th>
                        <th style={th}>Rate</th>
                        <th style={th}>Amount</th>
                        <th style={th}>Tax</th>
                      </tr>
                    </thead>
                    <tbody>
                      {report.incomeTax.bands.map(b => (
                        <tr key={b.name} style={{ borderTop: '1px solid var(--border)' }}>
                          <td style={{ ...td, textAlign: 'left', color: 'var(--text-secondary)' }}>{b.name}</td>
                          <td style={{ ...td, color: 'var(--text-muted)' }}>{(b.rate * 100).toFixed(0)}%</td>
                          <td style={td}>{gbp(b.amount)}</td>
                          <td style={{ ...td, fontWeight: 600 }}>{gbp(b.taxDue)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}

                {report.scottishBandsAssumed && (
                  <Note>
                    No confirmed Scottish bands are recorded for {report.year}; the most recent ones have been
                    carried forward. Check them against Revenue Scotland before filing.
                  </Note>
                )}
                <table style={tableStyle}>
                  <thead>
                    <tr>
                      <th style={{ ...th, textAlign: 'left' }}>Asset</th>
                      <th style={th}>Received</th>
                      <th style={th}>Payments</th>
                      <th style={th}>Value at receipt</th>
                    </tr>
                  </thead>
                  <tbody>
                    {report.income.map(r => (
                      <tr key={r.asset} style={{ borderTop: '1px solid var(--border)' }}>
                        <td style={{ ...td, textAlign: 'left', fontWeight: 600 }}>{r.asset}</td>
                        <td style={td}>{qty(r.quantity)}</td>
                        <td style={{ ...td, color: 'var(--text-muted)' }}>{r.paymentCount}</td>
                        <td style={{ ...td, fontWeight: 600 }}>{gbp(r.valueGbp)}</td>
                      </tr>
                    ))}
                    <tr style={{ borderTop: '2px solid var(--border)' }}>
                      <td style={{ ...td, textAlign: 'left', fontWeight: 700 }}>Total</td>
                      <td style={td} />
                      <td style={td} />
                      <td style={{ ...td, fontWeight: 700 }}>
                        {gbp(report.income.reduce((s, r) => s + r.valueGbp, 0))}
                      </td>
                    </tr>
                  </tbody>
                </table>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.55 }}>
                  Rewards are income at their sterling value on the day they arrived, and that value also becomes
                  their acquisition cost in the pool for when they are eventually sold. This total is not included
                  in the capital gains figures above.
                </div>
              </div>
            )}

            {report.totalTaxDue != null && (
              <div style={{ ...card, borderColor: 'color-mix(in srgb, var(--yellow) 45%, var(--border))' }}>
                <div style={{ display: 'flex', alignItems: 'baseline', gap: 16, flexWrap: 'wrap' }}>
                  <h3 style={{ margin: 0, fontSize: 13, letterSpacing: 0.4, textTransform: 'uppercase', color: 'var(--text-secondary)' }}>
                    Total for {report.year}
                  </h3>
                  <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>
                    {gbp(tax.totalTaxDue)} capital gains + {gbp(report.incomeTax?.totalTaxDue ?? 0)} income
                  </span>
                </div>
                <div style={{ fontSize: 26, fontWeight: 800, color: report.totalTaxDue > 0 ? 'var(--red)' : 'var(--green)' }}>
                  {gbp(report.totalTaxDue)}
                </div>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.55 }}>
                  Two separate charges that fall due together. They are worked out on different rules and
                  cannot offset each other: a capital loss does not reduce the tax on staking income.
                </div>
              </div>
            )}

            {(report.unmatched?.length > 0) && (
              <div style={{ ...card, borderColor: 'color-mix(in srgb, var(--red) 45%, var(--border))' }}>
                <h3 style={{ margin: 0, fontSize: 13, letterSpacing: 0.4, textTransform: 'uppercase', color: 'var(--red)' }}>
                  Disposals with no acquisition in the data
                </h3>
                <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', lineHeight: 1.55 }}>
                  These were sold, but the stored history holds nothing showing where they came from —
                  bought before the history begins, or moved in from another exchange or wallet. They are
                  <strong> excluded from every figure above</strong>, because matching them to a cost of nothing
                  would report the whole proceeds as gain and overstate the tax due. Find their real acquisition
                  cost and the gain will be lower than leaving them out implies.
                </div>
                <div style={{ overflowX: 'auto' }}>
                  <table style={tableStyle}>
                    <thead>
                      <tr>
                        <th style={{ ...th, textAlign: 'left' }}>Sold</th>
                        <th style={{ ...th, textAlign: 'left' }}>Asset</th>
                        <th style={th}>Quantity</th>
                        <th style={th}>Proceeds not accounted for</th>
                      </tr>
                    </thead>
                    <tbody>
                      {report.unmatched.map((u, i) => (
                        <tr key={`${u.asset}-${u.sellDate}-${i}`} style={{ borderTop: '1px solid var(--border)' }}>
                          <td style={{ ...td, textAlign: 'left', color: 'var(--text-secondary)' }}>{shortDate(u.sellDate)}</td>
                          <td style={{ ...td, textAlign: 'left', fontWeight: 600 }}>{u.asset}</td>
                          <td style={td}>{qty(u.quantity)}</td>
                          <td style={{ ...td, fontWeight: 600, color: 'var(--red)' }}>{gbp(u.proceedsGbp)}</td>
                        </tr>
                      ))}
                      <tr style={{ borderTop: '2px solid var(--border)' }}>
                        <td style={{ ...td, textAlign: 'left', fontWeight: 700 }}>Total</td>
                        <td style={td} />
                        <td style={td} />
                        <td style={{ ...td, fontWeight: 700, color: 'var(--red)' }}>
                          {gbp(report.unmatched.reduce((sum, u) => sum + u.proceedsGbp, 0))}
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {hasGaps && (
              <Note>
                {coverage.skippedForMissingRate > 0 && (
                  <>{coverage.skippedForMissingRate} event{coverage.skippedForMissingRate === 1 ? ' was' : 's were'} left
                  out because no GBP rate is stored for {coverage.skippedForMissingRate === 1 ? 'its' : 'their'} date.{' '}</>
                )}
                {coverage.skippedForUnknownPair > 0 && (
                  <>{coverage.skippedForUnknownPair} trade{coverage.skippedForUnknownPair === 1 ? '' : 's'} had a pair
                  that could not be resolved to a base and quote asset.{' '}</>
                )}
                The figures above are therefore incomplete. Rates are stored from{' '}
                {shortDate(coverage.rateFrom)} to {shortDate(coverage.rateTo)} ({coverage.rateDaysAvailable} days).
              </Note>
            )}

            {coverage && coverage.amountsUsingCarriedRate > 0 && (
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                {coverage.amountsUsingCarriedRate} amount{coverage.amountsUsingCarriedRate === 1 ? '' : 's'} used the
                previous day&apos;s GBP rate because none was stored for the exact date — weekends and download gaps
                do this, and the difference is normally small.
              </div>
            )}

            <div style={card}>
              <div style={{ display: 'flex', alignItems: 'baseline', gap: 12, flexWrap: 'wrap' }}>
                <h3 style={{ margin: 0, fontSize: 13, letterSpacing: 0.4, textTransform: 'uppercase', color: 'var(--text-secondary)' }}>
                  Disposals
                </h3>
                <div style={{ display: 'flex', gap: 6 }}>
                  {['All', 'SameDay', 'ThirtyDay', 'Section104Pool'].map(rule => (
                    <button
                      key={rule}
                      onClick={() => setRuleFilter(rule)}
                      title={RULE_HINT[rule]}
                      style={{
                        padding: '2px 8px', fontSize: 11, borderRadius: 4, cursor: 'pointer',
                        border: '1px solid var(--border)',
                        background: ruleFilter === rule ? 'var(--yellow)' : 'var(--bg-primary)',
                        color: ruleFilter === rule ? '#fff' : 'var(--text-secondary)',
                      }}
                    >
                      {rule === 'All' ? `All (${report.rows.length})` : `${RULE_LABEL[rule]} (${ruleCounts[rule] || 0})`}
                    </button>
                  ))}
                </div>
              </div>

              {rows.length === 0 ? (
                <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>No disposals in this year.</div>
              ) : (
                <div style={{ overflowX: 'auto' }}>
                  <table style={tableStyle}>
                    <thead>
                      <tr>
                        <th style={{ ...th, textAlign: 'left' }}>Sold</th>
                        <th style={{ ...th, textAlign: 'left' }}>Asset</th>
                        <th style={th}>Quantity</th>
                        <th style={th}>Proceeds</th>
                        <th style={th}>Cost</th>
                        <th style={th}>Sell fee</th>
                        <th style={th}>Gain / loss</th>
                        <th style={{ ...th, textAlign: 'left' }}>Rule</th>
                        <th style={{ ...th, textAlign: 'left' }}>Acquired</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((r, i) => (
                        <tr key={`${r.sellDate}-${r.asset}-${i}`} style={{ borderTop: '1px solid var(--border)' }}>
                          <td style={{ ...td, textAlign: 'left', color: 'var(--text-secondary)' }}>{shortDate(r.sellDate)}</td>
                          <td style={{ ...td, textAlign: 'left', fontWeight: 600 }}>{r.asset}</td>
                          <td style={td}>{qty(r.quantity)}</td>
                          <td style={td}>{gbp(r.proceedsGbp)}</td>
                          <td style={td}>{gbp(r.allowableCostGbp)}</td>
                          <td style={{ ...td, color: 'var(--text-muted)' }}>{gbp(r.sellFeeGbp)}</td>
                          <td style={{ ...td, fontWeight: 600, color: r.gainOrLoss >= 0 ? 'var(--green)' : 'var(--red)' }}>
                            {gbp(r.gainOrLoss)}
                          </td>
                          <td style={{ ...td, textAlign: 'left', color: 'var(--text-muted)' }} title={RULE_HINT[r.rule]}>
                            {RULE_LABEL[r.rule] || r.rule}
                          </td>
                          <td style={{ ...td, textAlign: 'left', color: 'var(--text-muted)' }}>
                            {r.buyDate ? shortDate(r.buyDate) : 'pool'}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              <div style={{ fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.55 }}>
                Disposals are matched to their acquisition cost in the order HMRC requires: same day first, then
                acquisitions in the following 30 days, then the Section 104 pool. That order is not a preference —
                it is what stops a loss being banked by selling and immediately rebuying.
              </div>
            </div>

            <Note>
              This is a working estimate from your Kraken data, not tax advice, and it does not know about
              activity on other exchanges, wallets, transfers, gifts, or lost coins. Crypto-to-crypto trades are
              treated as disposals; dollar stablecoins are valued at one dollar. Check it against your own records
              before it goes anywhere near a return.
            </Note>
          </>
        )}
      </div>
    </div>
  );
}
