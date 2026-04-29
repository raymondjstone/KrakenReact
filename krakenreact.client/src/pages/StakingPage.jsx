import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

export default function StakingPage() {
  const [data, setData] = useState([]);
  const [loading, setLoading] = useState(true);

  const load = useCallback(() => {
    api.get('/ledger/staking')
      .then(r => { setData(r.data || []); setLoading(false); })
      .catch(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load]);

  const fmt = (n, decimals = 2) => Number(n).toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
  const fmtQty = (n) => Number(n).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 8 });

  const totalUsd = data.reduce((s, r) => s + (r.totalUsd || 0), 0);
  const projTotal = data.reduce((s, r) => s + (r.projectedAnnualUsd || 0), 0);

  const cardStyle = {
    background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8,
    padding: '16px 20px', marginBottom: 12,
  };

  const labelStyle = { fontSize: 11, color: 'var(--text-muted)', marginBottom: 2 };
  const valueStyle = { fontSize: 15, fontWeight: 700, color: 'var(--text-primary)' };

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 16, marginBottom: 24 }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Staking Rewards</h2>
        <button onClick={load} style={{ padding: '4px 12px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-card)', color: 'var(--text-primary)', cursor: 'pointer', fontSize: 12 }}>
          Refresh
        </button>
      </div>

      {/* Summary row */}
      {data.length > 0 && (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 12, marginBottom: 24 }}>
          <div style={cardStyle}>
            <div style={labelStyle}>Total Rewards Value</div>
            <div style={valueStyle}>${fmt(totalUsd)}</div>
          </div>
          <div style={cardStyle}>
            <div style={labelStyle}>Assets Earning</div>
            <div style={valueStyle}>{data.length}</div>
          </div>
          <div style={cardStyle}>
            <div style={labelStyle}>Projected Annual Income</div>
            <div style={{ ...valueStyle, color: 'var(--green)' }}>${fmt(projTotal)}</div>
          </div>
        </div>
      )}

      {loading && <p style={{ color: 'var(--text-muted)' }}>Loading…</p>}

      {!loading && data.length === 0 && (
        <div style={{ color: 'var(--text-muted)', textAlign: 'center', padding: 48 }}>
          No staking rewards found in your ledger history.
        </div>
      )}

      {data.map(row => (
        <div key={row.asset} style={cardStyle}>
          <div style={{ display: 'flex', alignItems: 'flex-start', gap: 24, flexWrap: 'wrap' }}>
            {/* Asset name */}
            <div style={{ minWidth: 80 }}>
              <div style={{ fontWeight: 700, fontSize: 20, color: 'var(--text-primary)' }}>{row.asset}</div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>{row.rewardCount} payments</div>
            </div>

            {/* Metrics grid */}
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: '10px 24px', flex: 1 }}>
              <div>
                <div style={labelStyle}>Total Received</div>
                <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)' }}>{fmtQty(row.totalQty)} {row.asset}</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>${fmt(row.totalUsd)}</div>
              </div>
              <div>
                <div style={labelStyle}>Current Price</div>
                <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)' }}>${fmt(row.currentPrice, 4)}</div>
              </div>
              <div>
                <div style={labelStyle}>Est. APY</div>
                <div style={{ fontSize: 14, fontWeight: 600, color: row.estimatedApy > 0 ? 'var(--green)' : 'var(--text-muted)' }}>
                  {row.estimatedApy > 0 ? `${fmt(row.estimatedApy)}%` : '—'}
                </div>
              </div>
              <div>
                <div style={labelStyle}>Projected Annual</div>
                <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--green)' }}>
                  {row.projectedAnnualUsd > 0 ? `$${fmt(row.projectedAnnualUsd)}` : '—'}
                </div>
              </div>
              <div>
                <div style={labelStyle}>Last 7 days</div>
                <div style={{ fontSize: 13, color: 'var(--text-primary)' }}>{fmtQty(row.recent7dQty)} {row.asset}</div>
              </div>
              <div>
                <div style={labelStyle}>Last 30 days</div>
                <div style={{ fontSize: 13, color: 'var(--text-primary)' }}>{fmtQty(row.recent30dQty)} {row.asset}</div>
              </div>
              <div>
                <div style={labelStyle}>First reward</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>{new Date(row.firstRewardAt).toLocaleDateString()}</div>
              </div>
              <div>
                <div style={labelStyle}>Latest reward</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>{new Date(row.lastRewardAt).toLocaleDateString()}</div>
              </div>
            </div>

            {/* GBP value */}
            {row.totalGbp > 0 && (
              <div style={{ textAlign: 'right' }}>
                <div style={labelStyle}>Value (GBP)</div>
                <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)' }}>£{fmt(row.totalGbp)}</div>
              </div>
            )}
          </div>
        </div>
      ))}

      <div style={{ marginTop: 24, padding: 14, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.6 }}>
        <strong style={{ color: 'var(--text-primary)' }}>Notes</strong><br />
        APY is estimated from the ratio of rewards received to your current held balance over the earning period — it is approximate.
        Staking transfers (moving to/from staking wallet) are excluded; only actual reward payments are counted.
        Projected annual income uses current token price and may differ significantly from actual future earnings.
      </div>
    </div>
  );
}
