import { useState } from 'react';
import { formatPrice } from '../utils/formatters';

export default function Watchlist({ tickers, heldAssets, selectedSymbol, onSelect, pinnedSet, onPin, onUnpin }) {
  const [filter, setFilter] = useState('');
  const [tab, setTab] = useState('all');
  const [sortBy, setSortBy] = useState(null); // null | 'name' | 'price' | 'change'
  const [sortDir, setSortDir] = useState('desc');

  const toggleSort = (col) => {
    if (sortBy === col) {
      setSortDir(d => d === 'desc' ? 'asc' : 'desc');
    } else {
      setSortBy(col);
      setSortDir('desc');
    }
  };

  const search = filter.toLowerCase();
  const filtered = tickers.filter(t =>
    !search || (t.displaySymbol || t.symbol).toLowerCase().includes(search) || (t.base && t.base.toLowerCase().includes(search))
  );

  const held = filtered.filter(t => {
    const base = t.base || t.symbol?.split('/')[0];
    return heldAssets.has(base);
  });
  const notHeld = filtered.filter(t => {
    const base = t.base || t.symbol?.split('/')[0];
    return !heldAssets.has(base);
  });

  const applySort = (list) => {
    if (!sortBy) return list;
    const sorted = [...list];
    sorted.sort((a, b) => {
      if (sortBy === 'name') {
        const na = (a.displaySymbol || a.symbol || '').toLowerCase();
        const nb = (b.displaySymbol || b.symbol || '').toLowerCase();
        return sortDir === 'desc' ? nb.localeCompare(na) : na.localeCompare(nb);
      }
      const va = sortBy === 'price' ? (a.closePrice ?? 0) : (a.closePriceMovement ?? 0);
      const vb = sortBy === 'price' ? (b.closePrice ?? 0) : (b.closePriceMovement ?? 0);
      return sortDir === 'desc' ? vb - va : va - vb;
    });
    return sorted;
  };

  const displayList = applySort(tab === 'holdings' ? held : [...held, ...notHeld]);

  return (
    <div className="panel" style={{ height: '100%' }}>
      <div className="panel-header" style={{ flexDirection: 'column', height: 'auto', padding: '8px 12px', gap: 6 }}>
        <div style={{ display: 'flex', width: '100%', alignItems: 'center', gap: 8 }}>
          <span className="panel-title" style={{ flex: 1 }}>Watchlist</span>
          <div className="panel-tabs" style={{ gap: 8 }}>
            <button className={`panel-tab${tab === 'all' ? ' active' : ''}`} onClick={() => setTab('all')} style={{ padding: '4px 0', fontSize: 11 }}>All</button>
            <button className={`panel-tab${tab === 'holdings' ? ' active' : ''}`} onClick={() => setTab('holdings')} style={{ padding: '4px 0', fontSize: 11 }}>Holdings</button>
          </div>
        </div>
        <input
          type="text"
          placeholder="Search pairs..."
          value={filter}
          onChange={e => setFilter(e.target.value)}
          style={{
            width: '100%', padding: '5px 8px', background: 'var(--bg-input)', border: '1px solid var(--border)',
            color: 'var(--text-primary)', borderRadius: 4, fontSize: 12, outline: 'none', boxSizing: 'border-box',
          }}
        />
      </div>
      <div className="watchlist-header-row">
        <button className={`watchlist-sort-btn${sortBy === 'name' ? ' active' : ''}`} onClick={() => toggleSort('name')} style={{ flex: 1, textAlign: 'left' }}>
          Pair {sortBy === 'name' ? (sortDir === 'desc' ? '\u25BC' : '\u25B2') : ''}
        </button>
        <button className={`watchlist-sort-btn${sortBy === 'price' ? ' active' : ''}`} onClick={() => toggleSort('price')}>
          Price {sortBy === 'price' ? (sortDir === 'desc' ? '\u25BC' : '\u25B2') : ''}
        </button>
        <button className={`watchlist-sort-btn${sortBy === 'change' ? ' active' : ''}`} onClick={() => toggleSort('change')}>
          24h {sortBy === 'change' ? (sortDir === 'desc' ? '\u25BC' : '\u25B2') : ''}
        </button>
      </div>
      <div className="panel-body">
        {displayList.length === 0 && (
          <div style={{ padding: 16, textAlign: 'center', color: 'var(--text-muted)', fontSize: 12 }}>
            {filter ? 'No matching pairs' : tab === 'holdings' ? 'No held assets' : 'No pairs available'}
          </div>
        )}
        {displayList.map(t => {
          const change = t.closePriceMovement ?? 0;
          const isHeld = heldAssets.has(t.base || t.symbol?.split('/')[0]);
          const isPinned = pinnedSet?.has(t.symbol);
          return (
            <div
              key={t.symbol}
              className={`watchlist-row${t.symbol === selectedSymbol ? ' active' : ''}`}
              onClick={() => onSelect(t.symbol)}
            >
              {onPin && (
                <button
                  className={`watchlist-pin${isPinned ? ' pinned' : ''}`}
                  onClick={(e) => { e.stopPropagation(); isPinned ? onUnpin(t.symbol) : onPin(t.symbol); }}
                  title={isPinned ? 'Remove from ticker bar' : 'Add to ticker bar'}
                >{isPinned ? '\u2605' : '\u2606'}</button>
              )}
              <div className="watchlist-symbol">
                {t.displaySymbol || t.symbol}
                {isHeld && <span style={{ marginLeft: 4, fontSize: 9, color: 'var(--yellow)', verticalAlign: 'middle' }}>*</span>}
              </div>
              <div className="watchlist-price">{formatPrice(t.closePrice)}</div>
              <div className={`watchlist-change ${change >= 0 ? 'positive' : 'negative'}`}>
                {change >= 0 ? '+' : ''}{change.toFixed(2)}%
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
