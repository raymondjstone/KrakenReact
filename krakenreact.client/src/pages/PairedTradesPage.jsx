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

/**
 * Pairs the orders, gives each staking reward a row of its own, and works out the
 * running holding. Rewards take no part in pairing — they are acquisitions with no
 * cost basis — but they do add to the balance, so a row's holding is the quantity
 * held immediately after that row's defining event (its sell, else its buy, else
 * the reward).
 */
function buildRows(orders, rewards, tolerancePct) {
  const rows = [
    ...pairOrders(orders, tolerancePct),
    ...rewards.map(reward => ({ buy: null, sell: null, reward, sortTime: new Date(reward.timestamp).getTime() })),
  ].sort((a, b) => b.sortTime - a.sortTime);

  const events = [
    ...orders.map(o => ({ at: new Date(o.timestamp).getTime(), delta: (o.side === 'Buy' ? 1 : -1) * Number(o.quantity), source: o })),
    ...rewards.map(r => ({ at: new Date(r.timestamp).getTime(), delta: Number(r.quantity), source: r })),
  ].sort((a, b) => a.at - b.at);

  const holdingAfter = new Map();
  let running = 0;
  for (const e of events) {
    running += e.delta;
    holdingAfter.set(e.source, running);
  }

  return rows.map(r => ({ ...r, holding: holdingAfter.get(r.reward || r.sell || r.buy) }));
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
  const [rewards, setRewards] = useState([]);
  const [showRewards, setShowRewards] = useState(true);
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
    if (!asset) { setOrders([]); setRewards([]); setError(''); return; }
    setLoading(true);
    Promise.all([
      api.get(`/trades/grouped?symbol=${encodeURIComponent(asset)}`),
      // Rewards are supplementary — a failure here shouldn't blank out the trades.
      api.get(`/ledger/staking/entries?asset=${encodeURIComponent(asset)}`).catch(() => ({ data: [] })),
    ])
      .then(([tradeRes, rewardRes]) => {
        setOrders(tradeRes.data || []);
        setRewards(rewardRes.data || []);
        setError('');
        setLoading(false);
      })
      .catch(err => {
        setOrders([]);
        setRewards([]);
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

  const activeRewards = useMemo(() => (showRewards ? rewards : []), [showRewards, rewards]);

  const rows = useMemo(
    () => buildRows(orders, activeRewards, Number(tolerance) || 0),
    [orders, activeRewards, tolerance]
  );

  const showSymbol = useMemo(() => new Set(orders.map(o => o.symbol)).size > 1, [orders]);

  const stats = useMemo(() => {
    const paired = rows.filter(r => r.buy && r.sell);
    return {
      paired: paired.length,
      openBuys: rows.filter(r => r.buy && !r.sell).length,
      loneSells: rows.filter(r => r.sell && !r.buy).length,
      pnl: paired.reduce((sum, r) => sum + (r.sell.nettTotal - r.buy.nettTotal), 0),
      rewardCount: activeRewards.length,
      rewardQty: activeRewards.reduce((sum, r) => sum + Number(r.quantity), 0),
      holding: rows.length ? rows[0].holding : 0,
    };
  }, [rows, activeRewards]);

  const inputStyle = {
    padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4,
    background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 12,
  };
  const labelStyle = { display: 'flex', gap: 6, alignItems: 'center', fontSize: 12, color: 'var(--text-muted)' };
  const th = { padding: '4px 8px', textAlign: 'right', whiteSpace: 'nowrap', fontWeight: 600 };
  const td = { padding: '3px 8px', textAlign: 'right', whiteSpace: 'nowrap' };
  const divider = { borderLeft: '2px solid var(--border)' };
  const rewardBg = 'color-mix(in srgb, var(--yellow) 10%, transparent)';

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
        <label style={{ ...labelStyle, cursor: 'pointer' }}>
          <input type="checkbox" checked={showRewards} onChange={e => setShowRewards(e.target.checked)} />
          Staking rewards
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
            {stats.rewardCount > 0 && (
              <>
                {' · '}{stats.rewardCount} rewards{' '}
                <strong style={{ color: 'var(--yellow)' }}>+{formatQty(stats.rewardQty)}</strong>
              </>
            )}
            {' · '}holding{' '}
            <strong style={{ color: 'var(--text-primary)' }}>{formatQty(stats.holding)} {asset}</strong>
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
                <th colSpan={showSymbol ? 5 : 4} style={{ ...th, textAlign: 'center', color: 'var(--green)', borderBottom: '1px solid var(--border)' }}>Buys &amp; Rewards</th>
                <th colSpan={2} style={{ ...th, ...divider, textAlign: 'center', borderBottom: '1px solid var(--border)' }}>Match</th>
                <th colSpan={showSymbol ? 5 : 4} style={{ ...th, ...divider, textAlign: 'center', color: 'var(--red)', borderBottom: '1px solid var(--border)' }}>Sells</th>
                <th style={{ ...th, ...divider, textAlign: 'center', borderBottom: '1px solid var(--border)' }}>Round Trip</th>
                <th style={{ ...th, ...divider, textAlign: 'center', borderBottom: '1px solid var(--border)' }}>Position</th>
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
                <th style={{ ...th, ...divider }}>Holding</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(({ buy, sell, reward, holding }) => {
                const matched = buy && sell;
                const pnl = matched ? sell.nettTotal - buy.nettTotal : null;
                const pnlPct = matched && buy.nettTotal ? pnl / Math.abs(buy.nettTotal) * 100 : null;
                const held = matched ? new Date(sell.timestamp) - new Date(buy.timestamp) : null;
                const buyCell = { ...td, background: reward ? rewardBg : buy ? 'var(--buy-row-bg)' : 'transparent' };
                const sellCell = { ...td, background: sell ? 'var(--sell-row-bg)' : 'transparent' };
                const acquired = reward || buy;
                return (
                  <tr key={reward ? `reward|${reward.id}` : `${buy?.id || ''}|${sell?.id || ''}`} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ ...buyCell, textAlign: 'left', color: 'var(--text-secondary)' }} title={reward ? reward.referenceId : buy?.orderId}>
                      {acquired ? new Date(acquired.timestamp).toLocaleString() : ''}
                      {reward && (
                        <span style={{ marginLeft: 6, padding: '0 4px', borderRadius: 3, fontSize: 10, fontWeight: 600, color: 'var(--yellow)', border: '1px solid var(--yellow)' }}>
                          REWARD
                        </span>
                      )}
                    </td>
                    {showSymbol && <td style={{ ...buyCell, textAlign: 'left' }}>{reward ? reward.asset : buy?.symbol || ''}</td>}
                    <td style={{ ...buyCell, color: reward ? 'var(--yellow)' : buy ? 'var(--green)' : undefined }}>
                      {acquired ? formatQty(acquired.quantity) : ''}
                    </td>
                    <td style={buyCell}>{buy && !reward ? formatPrice(buy.price) : ''}</td>
                    <td style={{ ...buyCell, color: reward ? 'var(--text-muted)' : undefined }}>
                      {reward ? '—' : buy ? formatMoney(buy.nettTotal) : ''}
                    </td>
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
                    <td style={{ ...td, ...divider, color: 'var(--text-primary)' }} title="Quantity held immediately after this row's latest event">
                      {holding == null ? '' : formatQty(holding)}
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
