import { useState, useEffect, useMemo, useCallback } from 'react';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { formatPrice } from '../utils/formatters';

const DEFAULT_TOLERANCE = 1;

/** Percentage difference between two quantities, relative to the larger of the two. */
function qtyDiffPct(a, b) {
  const max = Math.max(Math.abs(a), Math.abs(b));
  if (max === 0) return 0;
  return Math.abs(a - b) / max * 100;
}

/**
 * Walks the orders oldest-first and pairs each sell with the most recent
 * still-unmatched buy whose quantity is within tolerance. Unmatched orders get
 * a row to themselves. Returned newest-first; a paired row is dated by its sell.
 */
function pairOrders(orders, tolerancePct) {
  const chrono = [...orders].sort((a, b) => new Date(a.timestamp) - new Date(b.timestamp));
  const openBuys = [];
  const rows = [];

  for (const order of chrono) {
    if (order.side === 'Buy') {
      openBuys.push(order);
      continue;
    }
    let matchIdx = -1;
    for (let i = openBuys.length - 1; i >= 0; i--) {
      if (qtyDiffPct(openBuys[i].quantity, order.quantity) <= tolerancePct) { matchIdx = i; break; }
    }
    if (matchIdx >= 0) {
      const [buy] = openBuys.splice(matchIdx, 1);
      rows.push({ buy, sell: order });
    } else {
      rows.push({ buy: null, sell: order });
    }
  }
  for (const buy of openBuys) rows.push({ buy, sell: null });

  return rows
    .map(r => ({ ...r, sortTime: new Date((r.sell || r.buy).timestamp).getTime() }))
    .sort((a, b) => b.sortTime - a.sortTime);
}

function formatQty(value) {
  if (value == null) return '';
  return Number(value).toLocaleString(undefined, { maximumFractionDigits: 8 });
}

function formatMoney(value, decimals = 2) {
  if (value == null) return '';
  return Number(value).toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
}

function formatHeld(ms) {
  if (ms == null || ms < 0) return '';
  const mins = Math.floor(ms / 60000);
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ${mins % 60}m`;
  const days = Math.floor(hours / 24);
  if (days < 365) return `${days}d ${hours % 24}h`;
  return `${Math.floor(days / 365)}y ${days % 365}d`;
}

export default function PairedTradesPage() {
  const [assets, setAssets] = useState([]);
  const [asset, setAsset] = useState('');
  const [orders, setOrders] = useState([]);
  const [tolerance, setTolerance] = useState(DEFAULT_TOLERANCE);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  // Assets the account has actually traded, most recent first — the first is preselected.
  useEffect(() => {
    let disposed = false;
    api.get('/trades/summary')
      .then(r => {
        if (disposed) return;
        const list = (r.data || []).map(s => s.asset).filter(Boolean);
        setAssets(list);
        setAsset(prev => prev || list[0] || '');
      })
      .catch(() => {});
    return () => { disposed = true; };
  }, []);

  const load = useCallback(() => {
    if (!asset) { setOrders([]); setError(''); return; }
    setLoading(true);
    api.get(`/trades/grouped?symbol=${encodeURIComponent(asset)}`)
      .then(r => { setOrders(r.data || []); setError(''); setLoading(false); })
      .catch(err => {
        setOrders([]);
        setError(err.response?.data?.message || 'Failed to load trades');
        setLoading(false);
      });
  }, [asset]);

  // Debounced so typing in the asset box doesn't fire a request per keystroke
  useEffect(() => {
    const timer = setTimeout(load, 250);
    return () => clearTimeout(timer);
  }, [load]);

  useEffect(() => {
    const conn = getConnection();
    conn.on('TradesUpdated', load);
    return () => conn.off('TradesUpdated', load);
  }, [load]);

  const rows = useMemo(() => pairOrders(orders, Number(tolerance) || 0), [orders, tolerance]);

  const showSymbol = useMemo(() => new Set(orders.map(o => o.symbol)).size > 1, [orders]);

  const stats = useMemo(() => {
    const paired = rows.filter(r => r.buy && r.sell);
    return {
      paired: paired.length,
      openBuys: rows.filter(r => r.buy && !r.sell).length,
      loneSells: rows.filter(r => r.sell && !r.buy).length,
      pnl: paired.reduce((sum, r) => sum + (r.sell.nettTotal - r.buy.nettTotal), 0),
    };
  }, [rows]);

  const inputStyle = {
    padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4,
    background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 12,
  };
  const labelStyle = { display: 'flex', gap: 6, alignItems: 'center', fontSize: 12, color: 'var(--text-muted)' };
  const th = { padding: '4px 8px', textAlign: 'right', whiteSpace: 'nowrap', fontWeight: 600 };
  const td = { padding: '3px 8px', textAlign: 'right', whiteSpace: 'nowrap' };
  const divider = { borderLeft: '2px solid var(--border)' };

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <div style={{ padding: '6px 12px', borderBottom: '1px solid var(--border)', background: 'var(--bg-secondary)', display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap' }}>
        <label style={labelStyle}>
          Asset
          <input
            list="paired-trade-assets"
            value={asset}
            onChange={e => setAsset(e.target.value.trim())}
            placeholder="e.g. BTC"
            style={{ ...inputStyle, width: 120 }}
          />
          <datalist id="paired-trade-assets">
            {assets.map(a => <option key={a} value={a} />)}
          </datalist>
        </label>
        <label style={labelStyle}>
          Qty match within
          <input
            type="number"
            min="0"
            step="0.1"
            value={tolerance}
            onChange={e => setTolerance(e.target.value)}
            style={{ ...inputStyle, width: 60 }}
          />
          %
        </label>
        {loading && <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>Loading...</span>}
        {error && <span style={{ fontSize: 12, color: 'var(--red)' }}>{error}</span>}
        {!loading && !error && orders.length > 0 && (
          <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>
            {orders.length} orders {'·'} <strong style={{ color: 'var(--text-primary)' }}>{stats.paired}</strong> matched pairs {'·'}{' '}
            {stats.openBuys} unmatched buys {'·'} {stats.loneSells} unmatched sells {'·'} matched P&amp;L{' '}
            <strong style={{ color: stats.pnl >= 0 ? 'var(--green)' : 'var(--red)' }}>
              {stats.pnl >= 0 ? '+' : '-'}{formatMoney(Math.abs(stats.pnl))}
            </strong>
          </span>
        )}
      </div>

      <div style={{ flex: 1, overflow: 'auto' }}>
        {!asset ? (
          <div style={{ padding: 24, color: 'var(--text-muted)', fontSize: 13 }}>Choose an asset to see its buys and sells.</div>
        ) : rows.length === 0 && !loading ? (
          <div style={{ padding: 24, color: 'var(--text-muted)', fontSize: 13 }}>No trades found for {asset}.</div>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
            <thead style={{ position: 'sticky', top: 0, zIndex: 1 }}>
              <tr style={{ background: 'var(--detail-header-bg)', color: 'var(--text-secondary)' }}>
                <th colSpan={showSymbol ? 5 : 4} style={{ ...th, textAlign: 'center', color: 'var(--green)', borderBottom: '1px solid var(--border)' }}>Buys</th>
                <th colSpan={2} style={{ ...th, ...divider, textAlign: 'center', borderBottom: '1px solid var(--border)' }}>Match</th>
                <th colSpan={showSymbol ? 5 : 4} style={{ ...th, ...divider, textAlign: 'center', color: 'var(--red)', borderBottom: '1px solid var(--border)' }}>Sells</th>
                <th style={{ ...th, ...divider, textAlign: 'center', borderBottom: '1px solid var(--border)' }}>Round Trip</th>
              </tr>
              <tr style={{ background: 'var(--bg-card)', color: 'var(--text-muted)' }}>
                <th style={{ ...th, textAlign: 'left' }}>Date</th>
                {showSymbol && <th style={{ ...th, textAlign: 'left' }}>Symbol</th>}
                <th style={th}>Qty</th>
                <th style={th}>Price</th>
                <th style={th}>Cost</th>
                <th style={{ ...th, ...divider }}>{'Δ'} Qty</th>
                <th style={th}>Held</th>
                <th style={{ ...th, ...divider, textAlign: 'left' }}>Date</th>
                {showSymbol && <th style={{ ...th, textAlign: 'left' }}>Symbol</th>}
                <th style={th}>Qty</th>
                <th style={th}>Price</th>
                <th style={th}>Proceeds</th>
                <th style={{ ...th, ...divider }}>P&amp;L</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(({ buy, sell }) => {
                const matched = buy && sell;
                const pnl = matched ? sell.nettTotal - buy.nettTotal : null;
                const pnlPct = matched && buy.nettTotal ? pnl / Math.abs(buy.nettTotal) * 100 : null;
                const held = matched ? new Date(sell.timestamp) - new Date(buy.timestamp) : null;
                const buyCell = { ...td, background: buy ? 'var(--buy-row-bg)' : 'transparent' };
                const sellCell = { ...td, background: sell ? 'var(--sell-row-bg)' : 'transparent' };
                return (
                  <tr key={`${buy?.id || ''}|${sell?.id || ''}`} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ ...buyCell, textAlign: 'left', color: 'var(--text-secondary)' }} title={buy?.orderId}>
                      {buy ? new Date(buy.timestamp).toLocaleString() : ''}
                    </td>
                    {showSymbol && <td style={{ ...buyCell, textAlign: 'left' }}>{buy?.symbol || ''}</td>}
                    <td style={{ ...buyCell, color: buy ? 'var(--green)' : undefined }}>{buy ? formatQty(buy.quantity) : ''}</td>
                    <td style={buyCell}>{buy ? formatPrice(buy.price) : ''}</td>
                    <td style={buyCell}>{buy ? formatMoney(buy.nettTotal) : ''}</td>
                    <td style={{ ...td, ...divider, color: 'var(--text-muted)' }}>
                      {matched ? `${qtyDiffPct(buy.quantity, sell.quantity).toFixed(2)}%` : ''}
                    </td>
                    <td style={{ ...td, color: 'var(--text-muted)' }}>{matched ? formatHeld(held) : ''}</td>
                    <td style={{ ...sellCell, ...divider, textAlign: 'left', color: 'var(--text-secondary)' }} title={sell?.orderId}>
                      {sell ? new Date(sell.timestamp).toLocaleString() : ''}
                    </td>
                    {showSymbol && <td style={{ ...sellCell, textAlign: 'left' }}>{sell?.symbol || ''}</td>}
                    <td style={{ ...sellCell, color: sell ? 'var(--red)' : undefined }}>{sell ? formatQty(sell.quantity) : ''}</td>
                    <td style={sellCell}>{sell ? formatPrice(sell.price) : ''}</td>
                    <td style={sellCell}>{sell ? formatMoney(sell.nettTotal) : ''}</td>
                    <td style={{ ...td, ...divider, fontWeight: 600, color: pnl == null ? 'var(--text-muted)' : pnl >= 0 ? 'var(--green)' : 'var(--red)' }}>
                      {pnl == null ? '' : `${pnl >= 0 ? '+' : '-'}${formatMoney(Math.abs(pnl))}`}
                      {pnlPct != null && (
                        <span style={{ fontWeight: 400, fontSize: 11, marginLeft: 4, opacity: 0.75 }}>
                          ({pnlPct >= 0 ? '+' : ''}{pnlPct.toFixed(1)}%)
                        </span>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
