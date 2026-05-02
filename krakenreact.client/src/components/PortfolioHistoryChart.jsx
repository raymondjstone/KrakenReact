import { useState } from 'react';

export default function PortfolioHistoryChart({ data }) {
  const [mode, setMode] = useState('value'); // 'value' | 'pnl'

  if (!data || data.length === 0) {
    return (
      <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', color: 'var(--text-muted)', fontSize: 13 }}>
        No snapshot history yet — a nightly snapshot runs at 23:55.
        <button
          onClick={() => fetch('/api/portfolio/snapshot', { method: 'POST' })}
          style={{ marginLeft: 12, padding: '2px 8px', fontSize: 11, cursor: 'pointer', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-input)', color: 'var(--text-primary)' }}
        >
          Take snapshot now
        </button>
      </div>
    );
  }

  const rawValues = data.map(d => Number(d.totalUsd));
  const baseline = rawValues[0];

  const values = mode === 'pnl'
    ? rawValues.map(v => baseline > 0 ? ((v - baseline) / baseline * 100) : 0)
    : rawValues;

  const minV = Math.min(...values);
  const maxV = Math.max(...values);
  const range = maxV - minV || 1;
  const W = 100;
  const H = 100;
  const PAD = 5;

  const points = values.map((v, i) => {
    const x = PAD + (i / Math.max(values.length - 1, 1)) * (W - PAD * 2);
    const y = PAD + (1 - (v - minV) / range) * (H - PAD * 2);
    return `${x},${y}`;
  }).join(' ');

  const lastRaw = rawValues[rawValues.length - 1];
  const firstRaw = rawValues[0];
  const trend = lastRaw >= firstRaw ? 'var(--green)' : 'var(--red)';
  const changePct = firstRaw > 0 ? ((lastRaw - firstRaw) / firstRaw * 100).toFixed(1) : '0.0';
  const changeUsd = (lastRaw - firstRaw);
  const sign = lastRaw >= firstRaw ? '+' : '';

  const formatDate = (d) => {
    const dt = new Date(d.date);
    return `${dt.getMonth() + 1}/${dt.getDate()}`;
  };

  const modeBtn = (m, label) => (
    <button
      onClick={() => setMode(m)}
      style={{
        padding: '1px 7px', fontSize: 10, border: '1px solid var(--border)', borderRadius: 3, cursor: 'pointer',
        background: mode === m ? 'var(--green)' : 'var(--bg-input)',
        color: mode === m ? 'white' : 'var(--text-muted)',
      }}
    >{label}</button>
  );

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', gap: 4 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 11, color: 'var(--text-muted)' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 5 }}>
          <span style={{ color: 'var(--text-primary)', fontWeight: 600, marginRight: 4 }}>
            {mode === 'pnl' ? 'Cumulative P&L' : `Portfolio ${data.length}d`}
          </span>
          {modeBtn('value', 'Value')}
          {modeBtn('pnl', 'P&L %')}
        </div>
        <span style={{ color: trend, fontWeight: 600 }}>
          {sign}{changePct}%&nbsp;({sign}${Math.abs(changeUsd).toLocaleString(undefined, { maximumFractionDigits: 0 })})
        </span>
      </div>
      <div style={{ flex: 1, position: 'relative' }}>
        <svg
          viewBox={`0 0 ${W} ${H}`}
          preserveAspectRatio="none"
          style={{ width: '100%', height: '100%', display: 'block' }}
        >
          {mode === 'pnl' && minV < 0 && maxV > 0 && (
            <line
              x1={PAD} y1={PAD + (1 - (0 - minV) / range) * (H - PAD * 2)}
              x2={W - PAD} y2={PAD + (1 - (0 - minV) / range) * (H - PAD * 2)}
              stroke="var(--border)" strokeWidth="0.5" strokeDasharray="2,2" vectorEffect="non-scaling-stroke"
            />
          )}
          <polyline
            points={points}
            fill="none"
            stroke={trend}
            strokeWidth="2"
            vectorEffect="non-scaling-stroke"
          />
          {values.length > 0 && (
            <>
              <circle cx={PAD} cy={PAD + (1 - (values[0] - minV) / range) * (H - PAD * 2)} r="1.5" fill={trend} vectorEffect="non-scaling-stroke" />
              <circle
                cx={PAD + (W - PAD * 2)}
                cy={PAD + (1 - (values[values.length - 1] - minV) / range) * (H - PAD * 2)}
                r="1.5" fill={trend} vectorEffect="non-scaling-stroke"
              />
            </>
          )}
        </svg>
        <div style={{ position: 'absolute', top: 0, right: 0, fontSize: 10, color: 'var(--text-muted)' }}>
          {mode === 'pnl'
            ? `${maxV >= 0 ? '+' : ''}${maxV.toFixed(1)}%`
            : `$${maxV.toLocaleString(undefined, { maximumFractionDigits: 0 })}`}
        </div>
        <div style={{ position: 'absolute', bottom: 0, right: 0, fontSize: 10, color: 'var(--text-muted)' }}>
          {mode === 'pnl'
            ? `${minV >= 0 ? '+' : ''}${minV.toFixed(1)}%`
            : `$${minV.toLocaleString(undefined, { maximumFractionDigits: 0 })}`}
        </div>
        {data.length > 0 && (
          <div style={{ position: 'absolute', bottom: 0, left: 0, fontSize: 10, color: 'var(--text-muted)' }}>
            {formatDate(data[0])} → {formatDate(data[data.length - 1])}
          </div>
        )}
      </div>
    </div>
  );
}
