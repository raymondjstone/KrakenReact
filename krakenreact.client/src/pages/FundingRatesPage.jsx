import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

export default function FundingRatesPage() {
  const [data, setData] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [filter, setFilter] = useState('');
  const [sortCol, setSortCol] = useState('fundingRatePct');
  const [sortAsc, setSortAsc] = useState(false);
  const [lastUpdated, setLastUpdated] = useState(null);

  const load = useCallback(() => {
    setLoading(true);
    api.get('/fundingrates')
      .then(r => {
        setData(r.data || []);
        setLastUpdated(new Date());
        setLoading(false);
      })
      .catch(e => {
        setError(e.response?.data || 'Failed to load funding rates');
        setLoading(false);
      });
  }, []);

  useEffect(() => {
    load();
    const interval = setInterval(load, 60000);
    return () => clearInterval(interval);
  }, [load]);

  const toggleSort = (col) => {
    if (sortCol === col) setSortAsc(a => !a);
    else { setSortCol(col); setSortAsc(false); }
  };

  const fmt = (n, decimals = 4) => Number(n).toFixed(decimals);
  const fmtBig = (n) => Number(n).toLocaleString(undefined, { maximumFractionDigits: 0 });

  const filtered = data.filter(r => !filter || r.displayName.toLowerCase().includes(filter.toLowerCase()) || r.symbol.toLowerCase().includes(filter.toLowerCase()));

  const sorted = [...filtered].sort((a, b) => {
    const av = a[sortCol] ?? 0, bv = b[sortCol] ?? 0;
    if (typeof av === 'string') return sortAsc ? av.localeCompare(bv) : bv.localeCompare(av);
    return sortAsc ? av - bv : bv - av;
  });

  const fundingColor = (rate) => {
    if (rate > 0.05) return '#ef4444';
    if (rate > 0.01) return '#f97316';
    if (rate < -0.05) return '#22c55e';
    if (rate < -0.01) return '#84cc16';
    return 'var(--text-primary)';
  };

  const premiumColor = (prem) => {
    if (Math.abs(prem) < 0.1) return 'var(--text-muted)';
    return prem > 0 ? '#f97316' : '#22c55e';
  };

  const Th = ({ col, label, right }) => (
    <th
      onClick={() => toggleSort(col)}
      style={{ padding: '8px', textAlign: right ? 'right' : 'left', color: sortCol === col ? 'var(--text-primary)' : 'var(--text-muted)', cursor: 'pointer', userSelect: 'none', whiteSpace: 'nowrap', fontSize: 12, fontWeight: 600 }}
    >
      {label} {sortCol === col ? (sortAsc ? '▲' : '▼') : ''}
    </th>
  );

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 16, marginBottom: 16, flexWrap: 'wrap' }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Funding Rates &amp; Futures Premium</h2>
        <button onClick={load} disabled={loading} style={{ padding: '4px 12px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-card)', color: 'var(--text-primary)', cursor: 'pointer', fontSize: 12 }}>
          {loading ? 'Loading…' : 'Refresh'}
        </button>
        {lastUpdated && (
          <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>
            Updated {lastUpdated.toLocaleTimeString()}
          </span>
        )}
        <input
          value={filter}
          onChange={e => setFilter(e.target.value)}
          placeholder="Filter symbol…"
          style={{ padding: '5px 10px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13, marginLeft: 'auto' }}
        />
      </div>

      {error && <div style={{ color: 'var(--red)', marginBottom: 12, fontSize: 13 }}>{error}</div>}

      {!loading && sorted.length === 0 && !error && (
        <div style={{ color: 'var(--text-muted)', textAlign: 'center', padding: 48 }}>No perpetual contracts found.</div>
      )}

      {sorted.length > 0 && (
        <div style={{ overflowX: 'auto' }}>
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ borderBottom: '2px solid var(--border)', background: 'var(--bg-card)' }}>
                <Th col="displayName" label="Symbol" />
                <Th col="fundingRatePct" label="Funding Rate" right />
                <Th col="fundingRatePrediction" label="Predicted" right />
                <Th col="annualisedFundingPct" label="Annualised" right />
                <Th col="premium" label="Premium" right />
                <Th col="markPrice" label="Mark Price" right />
                <Th col="indexPrice" label="Index Price" right />
                <Th col="openInterest" label="Open Interest" right />
                <Th col="vol24h" label="24h Volume" right />
              </tr>
            </thead>
            <tbody>
              {sorted.map(row => (
                <tr key={row.symbol} style={{ borderBottom: '1px solid var(--border)', ':hover': { background: 'var(--bg-card)' } }}>
                  <td style={{ padding: '8px', fontWeight: 600, color: 'var(--text-primary)' }}>
                    {row.displayName}
                    <span style={{ fontSize: 10, color: 'var(--text-muted)', display: 'block' }}>{row.symbol}</span>
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', fontWeight: 700, color: fundingColor(row.fundingRatePct) }}>
                    {row.fundingRatePct > 0 ? '+' : ''}{fmt(row.fundingRatePct, 4)}%
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: fundingColor(row.fundingRatePrediction) }}>
                    {row.fundingRatePrediction !== 0 ? `${row.fundingRatePrediction > 0 ? '+' : ''}${fmt(row.fundingRatePrediction, 4)}%` : '—'}
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: fundingColor(row.fundingRatePct) }}>
                    {row.annualisedFundingPct > 0 ? '+' : ''}{fmt(row.annualisedFundingPct, 1)}%
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: premiumColor(row.premium) }}>
                    {row.premium !== 0 ? `${row.premium > 0 ? '+' : ''}${fmt(row.premium, 3)}%` : '—'}
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-primary)' }}>
                    {row.markPrice > 0 ? `$${Number(row.markPrice).toLocaleString(undefined, { maximumFractionDigits: row.markPrice < 1 ? 6 : 2 })}` : '—'}
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-muted)' }}>
                    {row.indexPrice > 0 ? `$${Number(row.indexPrice).toLocaleString(undefined, { maximumFractionDigits: row.indexPrice < 1 ? 6 : 2 })}` : '—'}
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-muted)' }}>
                    {row.openInterest > 0 ? `$${fmtBig(row.openInterest)}` : '—'}
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-muted)' }}>
                    {row.vol24h > 0 ? `$${fmtBig(row.vol24h)}` : '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div style={{ marginTop: 20, padding: 14, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.7 }}>
        <strong style={{ color: 'var(--text-primary)' }}>Understanding Funding Rates</strong><br />
        <strong>Funding Rate</strong>: 8-hour charge paid between long and short traders. Positive = longs pay shorts (bullish sentiment); negative = shorts pay longs (bearish).<br />
        <strong>Annualised</strong>: Funding rate × 3 (per day) × 365 — indicative annual cost of holding the position.<br />
        <strong>Premium</strong>: (Mark − Index) / Index — positive premium means futures trade above spot (contango); negative means backwardation.<br />
        <strong>Interpretation</strong>: High positive funding + contango often signals crowded longs and potential for a squeeze. Strongly negative funding may indicate over-leveraged shorts.
        Data sourced from Kraken Futures public API (perpetuals only).
      </div>
    </div>
  );
}
