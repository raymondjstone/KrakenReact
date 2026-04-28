export default function PortfolioHistoryChart({ data }) {
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

  const values = data.map(d => Number(d.totalUsd));
  const minV = Math.min(...values);
  const maxV = Math.max(...values);
  const range = maxV - minV || 1;
  const W = 100; // SVG viewBox width (%)
  const H = 100; // SVG viewBox height (%)
  const PAD = 5;

  const points = values.map((v, i) => {
    const x = PAD + (i / Math.max(values.length - 1, 1)) * (W - PAD * 2);
    const y = PAD + (1 - (v - minV) / range) * (H - PAD * 2);
    return `${x},${y}`;
  }).join(' ');

  const lastVal = values[values.length - 1];
  const firstVal = values[0];
  const trend = lastVal >= firstVal ? 'var(--green)' : 'var(--red)';
  const change = firstVal > 0 ? ((lastVal - firstVal) / firstVal * 100).toFixed(1) : '0.0';
  const sign = lastVal >= firstVal ? '+' : '';

  const formatDate = (d) => {
    const dt = new Date(d.date);
    return `${dt.getMonth() + 1}/${dt.getDate()}`;
  };

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', gap: 4 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 11, color: 'var(--text-muted)' }}>
        <span style={{ color: 'var(--text-primary)', fontWeight: 600 }}>Portfolio 30-day history</span>
        <span style={{ color: trend, fontWeight: 600 }}>{sign}{change}% over {data.length} days</span>
      </div>
      <div style={{ flex: 1, position: 'relative' }}>
        <svg
          viewBox={`0 0 ${W} ${H}`}
          preserveAspectRatio="none"
          style={{ width: '100%', height: '100%', display: 'block' }}
        >
          <polyline
            points={points}
            fill="none"
            stroke={trend}
            strokeWidth="2"
            vectorEffect="non-scaling-stroke"
          />
          {/* dots at first and last */}
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
        {/* min/max labels */}
        <div style={{ position: 'absolute', top: 0, right: 0, fontSize: 10, color: 'var(--text-muted)' }}>
          ${maxV.toLocaleString(undefined, { maximumFractionDigits: 0 })}
        </div>
        <div style={{ position: 'absolute', bottom: 0, right: 0, fontSize: 10, color: 'var(--text-muted)' }}>
          ${minV.toLocaleString(undefined, { maximumFractionDigits: 0 })}
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
