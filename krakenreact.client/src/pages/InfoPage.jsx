import { useState, useEffect, useCallback, useMemo } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { formatPrice, colorForValue } from '../utils/formatters';
import OrderDialog from '../components/OrderDialog';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function InfoPage({ onSymbolClick, pinnedSet, onPin, onUnpin }) {
  const [rowData, setRowData] = useState([]);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [dialogSymbol, setDialogSymbol] = useState('');
  const [symbols, setSymbols] = useState([]);
  const [expanded, setExpanded] = useState({});
  const [expandedL2, setExpandedL2] = useState({});
  const { gridTheme } = useTheme();

  useEffect(() => {
    let disposed = false;
    const loadPrices = () => {
      if (disposed) return;
      api.get('/prices').then(r => { if (!disposed) setRowData(r.data); }).catch(console.error);
    };
    loadPrices();
    api.get('/symbols').then(r => { if (!disposed) setSymbols(r.data.map(s => s.websocketName)); }).catch(console.error);

    const refreshInterval = setInterval(loadPrices, 60000);

    const conn = getConnection();
    const handler = (data) => {
      setRowData(prev => {
        const idx = prev.findIndex(p => p.symbol === data.symbol);
        if (idx >= 0) {
          const updated = [...prev];
          const merged = { ...updated[idx] };
          for (const [key, value] of Object.entries(data)) {
            if (value != null) merged[key] = value;
          }
          updated[idx] = merged;
          return updated;
        }
        return prev;
      });
    };
    conn.on('TickerUpdate', handler);
    return () => {
      disposed = true;
      clearInterval(refreshInterval);
      conn.off('TickerUpdate', handler);
    };
  }, []);

  const toggleL1 = useCallback((key) => setExpanded(prev => ({ ...prev, [key]: !prev[key] })), []);
  const toggleL2 = useCallback((key) => setExpandedL2(prev => ({ ...prev, [key]: !prev[key] })), []);

  const movementCellStyle = useCallback(params => {
    const color = colorForValue(params.value);
    return color ? { color } : {};
  }, []);

  const avgVsCloseStyle = useCallback(params => {
    if (params.value == null || params.data?.closePrice == null) return {};
    return { color: Number(params.value) < Number(params.data.closePrice) ? 'var(--green)' : 'var(--red)' };
  }, []);

  const columnDefs = useMemo(() => [
    { headerName: 'Order', flex: 0, width: 80, cellRenderer: (p) => {
      return <button onClick={(e) => { e.stopPropagation(); setDialogSymbol(p.data.symbol); setDialogOpen(true); }}
        style={{ padding: '2px 6px', cursor: 'pointer', fontSize: 11 }}>Order</button>;
    }},
    { headerName: '', flex: 0, width: 36, cellRenderer: (p) => {
      if (!p.data) return null;
      const isPinned = pinnedSet?.has(p.data.symbol);
      return <button onClick={(e) => { e.stopPropagation(); isPinned ? onUnpin(p.data.symbol) : onPin(p.data.symbol); }}
        className={`watchlist-pin${isPinned ? ' pinned' : ''}`}
        style={{ padding: 0, fontSize: 14, lineHeight: 1 }}
        title={isPinned ? 'Remove from ticker bar' : 'Add to ticker bar'}
      >{isPinned ? '\u2605' : '\u2606'}</button>;
    }},
    { field: 'base', headerName: 'Symbol', minWidth: 80, cellStyle: movementCellStyle },
    { field: 'closePrice', headerName: 'Price', minWidth: 100, valueFormatter: p => formatPrice(p.value) },
    { field: 'openTime', headerName: 'Time', minWidth: 180, valueFormatter: p => p.value ? new Date(p.value).toLocaleString() : '' },
    { field: 'age', headerName: 'Age', minWidth: 80 },
    { field: 'krakenNewPricesLoaded', headerName: 'Loaded', flex: 0, width: 70 },
    { field: 'priceLowerThanBuy', headerName: 'P<Buy', flex: 0, width: 60, cellRenderer: p => p.value ? '\u2713' : '' },
    { field: 'averageBuyPrice', headerName: 'AvgBuy', minWidth: 90, valueFormatter: p => formatPrice(p.value) },
    { field: 'weightedPricePercentage', headerName: 'W%', minWidth: 70, cellStyle: movementCellStyle, valueFormatter: p => p.value != null ? Number(p.value).toFixed(1) : '' },
    { field: 'weightedPrice', headerName: 'WPrice', minWidth: 90, valueFormatter: p => formatPrice(p.value), cellStyle: avgVsCloseStyle },
    { field: 'avgPriceDay', headerName: 'AvgDay', minWidth: 90, valueFormatter: p => formatPrice(p.value), cellStyle: avgVsCloseStyle },
    { field: 'avgPriceWeek', headerName: 'AvgWk', minWidth: 90, valueFormatter: p => formatPrice(p.value), cellStyle: avgVsCloseStyle },
    { field: 'avgPriceMonth', headerName: 'AvgMo', minWidth: 90, valueFormatter: p => formatPrice(p.value), cellStyle: avgVsCloseStyle },
    { field: 'avgPriceYear', headerName: 'AvgYr', minWidth: 90, valueFormatter: p => formatPrice(p.value), cellStyle: avgVsCloseStyle },
    { field: 'closePriceMovement', headerName: 'Day%', minWidth: 70, cellStyle: movementCellStyle, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '' },
    { field: 'closePriceMovementWeek', headerName: 'Wk%', minWidth: 70, cellStyle: movementCellStyle, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '' },
    { field: 'closePriceMovementMonth', headerName: 'Mo%', minWidth: 70, cellStyle: movementCellStyle, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '' },
    { field: 'closePriceDifference', headerName: 'Diff', minWidth: 80, valueFormatter: p => formatPrice(p.value), cellStyle: movementCellStyle },
    { field: 'highPrice', headerName: 'High', minWidth: 90, valueFormatter: p => formatPrice(p.value) },
    { field: 'lowPrice', headerName: 'Low', minWidth: 90, valueFormatter: p => formatPrice(p.value) },
    { field: 'openPrice', headerName: 'Open', minWidth: 90, valueFormatter: p => formatPrice(p.value) },
    { field: 'volumeWeightedAveragePrice', headerName: 'VWAP', minWidth: 90, valueFormatter: p => formatPrice(p.value) },
    { field: 'volume', headerName: 'Volume', minWidth: 90, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '' },
    { field: 'tradeCount', headerName: 'Trades', minWidth: 70 },
  ], [movementCellStyle, avgVsCloseStyle, pinnedSet, onPin, onUnpin]);

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  const grouped = useMemo(() => {
    const byCcy = {};
    for (const item of rowData) {
      const ccy = item.ccy || 'USD';
      if (!byCcy[ccy]) byCcy[ccy] = [];
      byCcy[ccy].push(item);
    }
    const result = {};
    for (const [ccy, items] of Object.entries(byCcy)) {
      const byType = {};
      for (const item of items) {
        const ct = item.coinType || 'Other';
        if (!byType[ct]) byType[ct] = [];
        byType[ct].push(item);
      }
      result[ccy] = byType;
    }
    return result;
  }, [rowData]);

  const groupHeaderStyle = (level) => ({
    padding: `4px ${8 + level * 16}px`,
    cursor: 'pointer',
    userSelect: 'none',
    fontWeight: 600,
    fontSize: 13,
    color: level === 0 ? 'var(--group-l0-color)' : 'var(--group-l1-color)',
    background: level === 0 ? 'var(--group-l0-bg)' : 'var(--group-l1-bg)',
    borderBottom: '1px solid var(--border)',
    display: 'flex',
    alignItems: 'center',
    gap: 8,
  });

  const arrow = (isOpen) => <span style={{ fontSize: 10, display: 'inline-block', transform: isOpen ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>{'\u25B6'}</span>;

  return (
    <div style={{ height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      {Object.entries(grouped).map(([ccy, coinTypes]) => {
        const l1Key = ccy;
        const l1Open = expanded[l1Key] !== false;
        const l1Count = Object.values(coinTypes).reduce((s, items) => s + items.length, 0);
        return (
          <div key={l1Key}>
            <div style={groupHeaderStyle(0)} onClick={() => toggleL1(l1Key)}>
              {arrow(l1Open)} {ccy} ({l1Count})
            </div>
            {l1Open && Object.entries(coinTypes).map(([coinType, items]) => {
              const l2Key = `${ccy}|${coinType}`;
              const l2Open = expandedL2[l2Key] !== false;
              return (
                <div key={l2Key}>
                  <div style={groupHeaderStyle(1)} onClick={() => toggleL2(l2Key)}>
                    {arrow(l2Open)} {coinType} ({items.length})
                  </div>
                  {l2Open && (
                    <div style={{ height: Math.min(items.length * 30 + 34, 400) }}>
                      <AgGridReact
                        theme={gridTheme}
                        rowData={items}
                        columnDefs={columnDefs}
                        defaultColDef={defaultColDef}
                        headerHeight={28}
                        rowHeight={28}
                        getRowId={p => p.data.symbol}
                        onRowClicked={(e) => e.data && onSymbolClick(e.data.symbol)}
                        domLayout={items.length <= 10 ? 'autoHeight' : 'normal'}
                      />
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        );
      })}
      {rowData.length === 0 && (
        <div style={{ color: 'var(--text-muted)', padding: 24, textAlign: 'center' }}>Loading price data...</div>
      )}
      <OrderDialog isOpen={dialogOpen} onClose={(ok) => { setDialogOpen(false); if (ok) api.get('/prices').then(r => setRowData(r.data)); }}
        symbol={dialogSymbol} symbols={symbols} />
    </div>
  );
}
