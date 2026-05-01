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
        {sectionBtn('perfchart', 'Performance Chart')}
        {sectionBtn('correlation', 'Correlation Matrix')}
        {sectionBtn('metrics', 'Portfolio Metrics')}
      </div>
      {activeSection === 'heatmap'    && <PlCalendar />}
      {activeSection === 'perfchart'  && <PerformanceChart />}
      {activeSection === 'correlation' && <CorrelationMatrix />}
      {activeSection === 'metrics'    && <PortfolioMetrics />}
    </div>
  );
}

// ─── Portfolio Performance Chart ─────────────────────────────────────────────

function PerformanceChart() {
  const [history, setHistory] = useState([]);
  const [days, setDays] = useState(90);
  const [loading, setLoading] = useState(true);
  const [currency, setCurrency] = useState('usd');
  const [hoverIdx, setHoverIdx] = useState(null);

  useEffect(() => {
    setLoading(true);
    api.get(`/portfolio/history?days=${days}`)
      .then(r => { setHistory(r.data || []); setLoading(false); })
      .catch(() => setLoading(false));
  }, [days]);

  if (loading) return <p style={{ color: 'var(--text-muted)' }}>Loading…</p>;
  if (history.length < 2) return (
    <div style={{ color: 'var(--text-muted)', padding: 24 }}>
      Not enough snapshot history (need ≥ 2 days). A nightly snapshot runs at 23:55.
    </div>
  );

  const values = history.map(d => currency === 'usd' ? Number(d.totalUsd) : Number(d.totalGbp));
  const minV = Math.min(...values);
  const maxV = Math.max(...values);
  const range = maxV - minV || 1;
  const W = 900, H = 220, PAD_L = 70, PAD_R = 16, PAD_T = 16, PAD_B = 32;
  const chartW = W - PAD_L - PAD_R;
  const chartH = H - PAD_T - PAD_B;

  const toX = (i) => PAD_L + (i / Math.max(values.length - 1, 1)) * chartW;
  const toY = (v) => PAD_T + (1 - (v - minV) / range) * chartH;

  const points = values.map((v, i) => `${toX(i)},${toY(v)}`).join(' ');
  const areaPoints = `${PAD_L},${PAD_T + chartH} ${points} ${toX(values.length - 1)},${PAD_T + chartH}`;

  const first = values[0], last = values[values.length - 1];
  const trend = last >= first ? 'var(--green)' : 'var(--red)';
  const changePct = first > 0 ? ((last - first) / first * 100) : 0;
  const sign = changePct >= 0 ? '+' : '';
  const sym = currency === 'usd' ? '$' : '£';
  const fmt = (v) => `${sym}${Number(v).toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;

  // Y-axis ticks
  const yTicks = 5;
  const yTickVals = Array.from({ length: yTicks }, (_, i) => minV + (range * i) / (yTicks - 1));

  // X-axis date labels (up to 6)
  const xLabelCount = Math.min(6, history.length);
  const xLabelIdxs = Array.from({ length: xLabelCount }, (_, i) =>
    Math.round(i * (history.length - 1) / (xLabelCount - 1))
  );

  const hoverPoint = hoverIdx !== null ? { x: toX(hoverIdx), y: toY(values[hoverIdx]), val: values[hoverIdx], date: history[hoverIdx]?.date } : null;

  return (
    <div>
      <div style={{ display: 'flex', gap: 12, alignItems: 'center', marginBottom: 16, flexWrap: 'wrap' }}>
        <div style={{ fontWeight: 600, color: 'var(--text-primary)', fontSize: 15 }}>Portfolio Value Over Time</div>
        <div style={{ display: 'flex', gap: 6 }}>
          {[30, 90, 180, 365].map(d => (
            <button key={d} onClick={() => setDays(d)} style={{
              padding: '3px 10px', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 12,
              background: days === d ? 'var(--green)' : 'var(--bg-card)', color: days === d ? 'white' : 'var(--text-primary)',
            }}>{d}d</button>
          ))}
        </div>
        <div style={{ display: 'flex', gap: 6 }}>
          {['usd', 'gbp'].map(c => (
            <button key={c} onClick={() => setCurrency(c)} style={{
              padding: '3px 10px', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 12,
              background: currency === c ? 'var(--green)' : 'var(--bg-card)', color: currency === c ? 'white' : 'var(--text-primary)',
            }}>{c.toUpperCase()}</button>
          ))}
        </div>
        <span style={{ fontSize: 14, fontWeight: 700, color: trend, marginLeft: 8 }}>
          {sign}{changePct.toFixed(2)}% over {history.length} days
        </span>
      </div>

      <div style={{ overflowX: 'auto' }}>
        <svg
          viewBox={`0 0 ${W} ${H}`}
          style={{ width: '100%', maxWidth: W, display: 'block', cursor: 'crosshair' }}
          onMouseMove={e => {
            const rect = e.currentTarget.getBoundingClientRect();
            const svgX = (e.clientX - rect.left) * W / rect.width;
            const relX = svgX - PAD_L;
            const idx = Math.round((relX / chartW) * (values.length - 1));
            setHoverIdx(idx >= 0 && idx < values.length ? idx : null);
          }}
          onMouseLeave={() => setHoverIdx(null)}
        >
          {/* Y-axis grid + labels */}
          {yTickVals.map((v, i) => {
            const y = toY(v);
            return (
              <g key={i}>
                <line x1={PAD_L} y1={y} x2={W - PAD_R} y2={y} stroke="var(--border)" strokeWidth="0.5" />
                <text x={PAD_L - 4} y={y + 4} textAnchor="end" fontSize="10" fill="var(--text-muted)">{fmt(v)}</text>
              </g>
            );
          })}

          {/* Area fill */}
          <polygon points={areaPoints} fill={last >= first ? 'rgba(34,197,94,0.08)' : 'rgba(239,68,68,0.08)'} />

          {/* Line */}
          <polyline points={points} fill="none" stroke={trend} strokeWidth="1.5" vectorEffect="non-scaling-stroke" />

          {/* X-axis labels */}
          {xLabelIdxs.map(i => (
            <text key={i} x={toX(i)} y={H - 6} textAnchor="middle" fontSize="10" fill="var(--text-muted)">
              {new Date(history[i].date).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}
            </text>
          ))}

          {/* Hover crosshair */}
          {hoverPoint && (
            <>
              <line x1={hoverPoint.x} y1={PAD_T} x2={hoverPoint.x} y2={PAD_T + chartH} stroke="var(--text-muted)" strokeWidth="0.5" strokeDasharray="3,3" />
              <circle cx={hoverPoint.x} cy={hoverPoint.y} r="4" fill={trend} stroke="var(--bg-primary)" strokeWidth="1.5" />
              <rect
                x={Math.min(hoverPoint.x + 6, W - PAD_R - 110)}
                y={Math.max(hoverPoint.y - 28, PAD_T)}
                width="104" height="38" rx="4"
                fill="var(--bg-card)" stroke="var(--border)" strokeWidth="0.5"
              />
              <text
                x={Math.min(hoverPoint.x + 58, W - PAD_R - 56)}
                y={Math.max(hoverPoint.y - 13, PAD_T + 14)}
                textAnchor="middle" fontSize="11" fill="var(--text-primary)" fontWeight="600"
              >
                {fmt(hoverPoint.val)}
              </text>
              <text
                x={Math.min(hoverPoint.x + 58, W - PAD_R - 56)}
                y={Math.max(hoverPoint.y + 2, PAD_T + 28)}
                textAnchor="middle" fontSize="10" fill="var(--text-muted)"
              >
                {hoverPoint.date ? new Date(hoverPoint.date).toLocaleDateString() : ''}
              </text>
            </>
          )}
        </svg>
      </div>

      <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 8 }}>
        Based on nightly portfolio snapshots. Current value: <strong style={{ color: trend }}>{fmt(last)}</strong> &middot; Start: {fmt(first)}
      </div>
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
