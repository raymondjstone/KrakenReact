import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

export default function AnalyticsPage() {
  const [activeSection, setActiveSection] = useState('heatmap');

  const sectionBtn = (id, label) => (
    <button
      onClick={() => setActiveSection(id)}
      style={{
        padding: '6px 16px', border: 'none', cursor: 'pointer', fontSize: 13, fontWeight: 600,
        borderBottom: activeSection === id ? '2px solid var(--green)' : '2px solid transparent',
        background: 'transparent', color: activeSection === id ? 'var(--text-primary)' : 'var(--text-muted)',
      }}>
      {label}
    </button>
  );

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <h2 style={{ margin: '0 0 20px', color: 'var(--text-primary)' }}>Analytics</h2>
      <div style={{ display: 'flex', gap: 4, marginBottom: 24, borderBottom: '1px solid var(--border)' }}>
        {sectionBtn('heatmap', 'P/L Calendar')}
        {sectionBtn('correlation', 'Correlation Matrix')}
        {sectionBtn('metrics', 'Portfolio Metrics')}
      </div>
      {activeSection === 'heatmap'    && <PlCalendar />}
      {activeSection === 'correlation' && <CorrelationMatrix />}
      {activeSection === 'metrics'    && <PortfolioMetrics />}
    </div>
  );
}

// ─── P/L Calendar Heatmap ────────────────────────────────────────────────────

function PlCalendar() {
  const [history, setHistory] = useState([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.get('/portfolio/history?days=365')
      .then(r => { setHistory(r.data || []); setLoading(false); })
      .catch(() => setLoading(false));
  }, []);

  if (loading) return <p style={{ color: 'var(--text-muted)' }}>Loading…</p>;
  if (history.length < 2) return (
    <div style={{ color: 'var(--text-muted)', padding: 24 }}>
      Not enough snapshot history for a calendar (need ≥ 2 days). A nightly snapshot runs at 23:55.
    </div>
  );

  // Build map: dateString → dailyChange%
  const map = {};
  for (let i = 1; i < history.length; i++) {
    const prev = Number(history[i - 1].totalUsd);
    const curr = Number(history[i].totalUsd);
    if (prev > 0) {
      const pct = ((curr - prev) / prev) * 100;
      const dateStr = history[i].date.substring(0, 10);
      map[dateStr] = pct;
    }
  }

  // Build 52-week grid anchored to today (Sunday-first weeks)
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const startSunday = new Date(today);
  startSunday.setDate(startSunday.getDate() - startSunday.getDay() - 52 * 7 + 7);

  const weeks = [];
  let d = new Date(startSunday);
  while (d <= today) {
    const week = [];
    for (let dow = 0; dow < 7; dow++) {
      const dateStr = d.toISOString().substring(0, 10);
      week.push({ date: new Date(d), dateStr, pct: map[dateStr] ?? null });
      d.setDate(d.getDate() + 1);
    }
    weeks.push(week);
  }

  const absMax = Math.max(1, ...Object.values(map).map(Math.abs));

  const cellColor = (pct) => {
    if (pct === null) return 'var(--border)';
    const intensity = Math.min(Math.abs(pct) / absMax, 1);
    if (pct > 0) return `rgba(34, 197, 94, ${0.15 + intensity * 0.85})`;
    return `rgba(239, 68, 68, ${0.15 + intensity * 0.85})`;
  };

  const CELL = 12, GAP = 2;
  const monthLabels = [];
  let lastMonth = -1;
  weeks.forEach((week, wi) => {
    const m = week[0].date.getMonth();
    if (m !== lastMonth) {
      monthLabels.push({ wi, label: week[0].date.toLocaleString('default', { month: 'short' }) });
      lastMonth = m;
    }
  });

  const dayLabels = ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa'];

  return (
    <div>
      <div style={{ fontWeight: 600, marginBottom: 12, color: 'var(--text-primary)' }}>
        Daily P/L — last 52 weeks
      </div>
      <div style={{ overflowX: 'auto' }}>
        <div style={{ display: 'inline-flex', flexDirection: 'column', gap: 2 }}>
          {/* Month labels */}
          <div style={{ display: 'flex', gap: GAP, paddingLeft: 24 }}>
            {weeks.map((_, wi) => {
              const ml = monthLabels.find(m => m.wi === wi);
              return (
                <div key={wi} style={{ width: CELL, fontSize: 9, color: 'var(--text-muted)', textAlign: 'center', whiteSpace: 'nowrap', overflow: 'visible' }}>
                  {ml ? ml.label : ''}
                </div>
              );
            })}
          </div>
          {/* Day rows */}
          {[0, 1, 2, 3, 4, 5, 6].map(dow => (
            <div key={dow} style={{ display: 'flex', alignItems: 'center', gap: GAP }}>
              <div style={{ width: 20, fontSize: 9, color: 'var(--text-muted)', textAlign: 'right' }}>
                {dow % 2 === 1 ? dayLabels[dow] : ''}
              </div>
              {weeks.map((week, wi) => {
                const cell = week[dow];
                const isFuture = cell.date > today;
                return (
                  <div
                    key={wi}
                    title={cell.pct !== null ? `${cell.dateStr}: ${cell.pct >= 0 ? '+' : ''}${cell.pct.toFixed(2)}%` : cell.dateStr}
                    style={{
                      width: CELL, height: CELL, borderRadius: 2,
                      background: isFuture ? 'transparent' : cellColor(cell.pct),
                      cursor: cell.pct !== null ? 'default' : undefined,
                      flexShrink: 0,
                    }}
                  />
                );
              })}
            </div>
          ))}
        </div>
      </div>
      {/* Legend */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, fontSize: 11, color: 'var(--text-muted)' }}>
        <span>Loss</span>
        {[-1, -0.5, 0, 0.5, 1].map(v => (
          <div key={v} style={{ width: 14, height: 14, borderRadius: 2, background: cellColor(v * absMax) }} />
        ))}
        <span>Gain</span>
        <span style={{ marginLeft: 16 }}>hover for detail</span>
      </div>
    </div>
  );
}

// ─── Asset Correlation Matrix ─────────────────────────────────────────────────

function CorrelationMatrix() {
  const [data, setData] = useState(null);
  const [days, setDays] = useState(30);
  const [loading, setLoading] = useState(false);
  const [customSymbols, setCustomSymbols] = useState('');
  const [error, setError] = useState('');

  const fetchCorrelation = useCallback(() => {
    setLoading(true);
    setError('');
    const q = customSymbols.trim() ? `&symbols=${encodeURIComponent(customSymbols)}` : '';
    api.get(`/correlation?days=${days}${q}`)
      .then(r => { setData(r.data); setLoading(false); })
      .catch(err => {
        setError(err.response?.data?.message || 'Error loading correlation data');
        setLoading(false);
      });
  }, [days, customSymbols]);

  useEffect(() => { fetchCorrelation(); }, [fetchCorrelation]);

  const corrColor = (v) => {
    if (v === 1) return 'rgba(99,102,241,0.9)';
    if (v > 0.7) return `rgba(34,197,94,${0.3 + v * 0.5})`;
    if (v > 0.3) return `rgba(34,197,94,${0.1 + v * 0.4})`;
    if (v < -0.3) return `rgba(239,68,68,${0.1 + Math.abs(v) * 0.5})`;
    return 'var(--bg-card)';
  };

  return (
    <div>
      <div style={{ fontWeight: 600, marginBottom: 12, color: 'var(--text-primary)' }}>Asset Correlation Matrix</div>
      <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end', marginBottom: 16, flexWrap: 'wrap' }}>
        <div>
          <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Period</div>
          {[7, 14, 30, 90].map(d => (
            <button key={d} onClick={() => setDays(d)} style={{
              marginRight: 6, padding: '4px 10px', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 12,
              background: days === d ? 'var(--green)' : 'var(--bg-card)', color: days === d ? 'white' : 'var(--text-primary)',
            }}>{d}d</button>
          ))}
        </div>
        <div style={{ flex: 1 }}>
          <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Custom symbols (comma-separated, e.g. XBT/USD,ETH/USD)</div>
          <input
            value={customSymbols}
            onChange={e => setCustomSymbols(e.target.value)}
            placeholder="Leave blank to use held assets"
            style={{ padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 12, width: '100%' }}
          />
        </div>
        <button onClick={fetchCorrelation} style={{ padding: '6px 14px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 }}>
          Calculate
        </button>
      </div>

      {loading && <p style={{ color: 'var(--text-muted)' }}>Loading…</p>}
      {error && <p style={{ color: 'var(--red)' }}>{error}</p>}
      {!loading && data && data.symbols?.length >= 2 && (
        <>
          <div style={{ overflowX: 'auto' }}>
            <table style={{ borderCollapse: 'collapse', fontSize: 12 }}>
              <thead>
                <tr>
                  <th style={{ padding: '6px 8px', color: 'var(--text-muted)', textAlign: 'left' }}></th>
                  {data.symbols.map(s => (
                    <th key={s} style={{ padding: '6px 8px', color: 'var(--text-muted)', textAlign: 'center', whiteSpace: 'nowrap', fontWeight: 600 }}>
                      {s.split('/')[0]}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {data.matrix.map((row, i) => (
                  <tr key={i}>
                    <td style={{ padding: '6px 8px', color: 'var(--text-primary)', fontWeight: 600, whiteSpace: 'nowrap' }}>
                      {data.symbols[i].split('/')[0]}
                    </td>
                    {row.map((v, j) => (
                      <td key={j} style={{
                        padding: '6px 10px', textAlign: 'center', borderRadius: 4,
                        background: corrColor(v),
                        color: Math.abs(v) > 0.6 ? 'white' : 'var(--text-primary)',
                        fontWeight: i === j ? 700 : 400,
                      }}>
                        {v.toFixed(2)}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div style={{ marginTop: 12, fontSize: 11, color: 'var(--text-muted)' }}>
            Pearson correlation on daily returns over {data.days} days. +1 = perfect co-movement, -1 = perfect inverse, 0 = uncorrelated.
          </div>
        </>
      )}
      {!loading && data && data.symbols?.length < 2 && (
        <p style={{ color: 'var(--text-muted)' }}>Not enough symbols with daily kline data. Try adding custom symbols above.</p>
      )}
    </div>
  );
}

// ─── Portfolio Metrics ────────────────────────────────────────────────────────

function PortfolioMetrics() {
  const [metrics, setMetrics] = useState(null);
  const [days, setDays] = useState(365);
  const [loading, setLoading] = useState(false);

  const fetchMetrics = useCallback(() => {
    setLoading(true);
    api.get(`/portfolio/metrics?days=${days}`)
      .then(r => { setMetrics(r.data); setLoading(false); })
      .catch(() => setLoading(false));
  }, [days]);

  useEffect(() => { fetchMetrics(); }, [fetchMetrics]);

  const metricCard = (label, value, sub, color) => (
    <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: '16px 20px', minWidth: 160 }}>
      <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>{label}</div>
      <div style={{ fontSize: 28, fontWeight: 700, color: color || 'var(--text-primary)' }}>{value}</div>
      {sub && <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 4 }}>{sub}</div>}
    </div>
  );

  const sharpeColor = (v) => {
    if (v === null || v === undefined) return 'var(--text-muted)';
    if (v > 1) return 'var(--green)';
    if (v > 0) return '#f59e0b';
    return 'var(--red)';
  };

  const ddColor = (v) => {
    if (v === null || v === undefined) return 'var(--text-muted)';
    if (v < 10) return 'var(--green)';
    if (v < 25) return '#f59e0b';
    return 'var(--red)';
  };

  const retColor = (v) => {
    if (v === null || v === undefined) return 'var(--text-muted)';
    return v >= 0 ? 'var(--green)' : 'var(--red)';
  };

  return (
    <div>
      <div style={{ fontWeight: 600, marginBottom: 12, color: 'var(--text-primary)' }}>Portfolio Risk Metrics</div>
      <div style={{ display: 'flex', gap: 8, marginBottom: 20 }}>
        {[90, 180, 365, 730].map(d => (
          <button key={d} onClick={() => setDays(d)} style={{
            padding: '4px 12px', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 12,
            background: days === d ? 'var(--green)' : 'var(--bg-card)', color: days === d ? 'white' : 'var(--text-primary)',
          }}>{d === 730 ? '2yr' : `${d}d`}</button>
        ))}
      </div>
      {loading && <p style={{ color: 'var(--text-muted)' }}>Loading…</p>}
      {!loading && metrics && (
        <>
          {metrics.sampleDays < 5 ? (
            <p style={{ color: 'var(--text-muted)' }}>
              Not enough snapshot history (only {metrics.sampleDays} day{metrics.sampleDays !== 1 ? 's' : ''}). Metrics require at least 5 daily snapshots.
            </p>
          ) : (
            <>
              <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap', marginBottom: 20 }}>
                {metricCard(
                  'Sharpe Ratio (annualised)',
                  metrics.sharpe !== null ? metrics.sharpe.toFixed(2) : '—',
                  '>1 = good, >2 = excellent',
                  sharpeColor(metrics.sharpe)
                )}
                {metricCard(
                  'Max Drawdown',
                  metrics.maxDrawdownPct !== null ? `${metrics.maxDrawdownPct.toFixed(1)}%` : '—',
                  'peak-to-trough decline',
                  ddColor(metrics.maxDrawdownPct)
                )}
                {metricCard(
                  'Annualised Return',
                  metrics.annualReturnPct !== null ? `${metrics.annualReturnPct >= 0 ? '+' : ''}${metrics.annualReturnPct.toFixed(1)}%` : '—',
                  `from ${metrics.sampleDays} daily snapshots`,
                  retColor(metrics.annualReturnPct)
                )}
              </div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.6, maxWidth: 540 }}>
                <strong style={{ color: 'var(--text-primary)' }}>How these are calculated</strong><br />
                Sharpe = annualised daily return ÷ daily return volatility (risk-free rate = 0).<br />
                Max drawdown = largest peak-to-trough decline in portfolio USD value.<br />
                Annualised return = geometric extrapolation of the observed {metrics.sampleDays}-day return.
              </div>
            </>
          )}
        </>
      )}
    </div>
  );
}
