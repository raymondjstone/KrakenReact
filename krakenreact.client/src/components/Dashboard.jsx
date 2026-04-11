import { useState, useEffect, useMemo } from 'react';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';

ModuleRegistry.registerModules([AllCommunityModule]);
import TickerCard from './TickerCard';
import Watchlist from './Watchlist';
import OpenOrdersGrid from './OpenOrdersGrid';
import ChartPage from '../pages/ChartPage';
import { formatPrice, formatNumber } from '../utils/formatters';
import { useTheme } from '../context/ThemeContext';

export default function Dashboard({ config, pinnedSymbols, pinnedSet, onPin, onUnpin, largeMovementThreshold = 5, hideAlmostZeroBalances }) {
  const [tickers, setTickers] = useState([]);
  const [selectedSymbol, setSelectedSymbol] = useState(() => localStorage.getItem('kraken_selected_pair') || '');
  const [bottomTab, setBottomTab] = useState('orders');
  const [orders, setOrders] = useState([]);
  const [balances, setBalances] = useState([]);
  const [symbols, setSymbols] = useState([]);
  const { gridTheme } = useTheme();

  useEffect(() => {
    let disposed = false;
    const loadPrices = () => {
      if (disposed) return;
      api.get('/prices').then(r => {
        if (disposed) return;
        setTickers(r.data);
        if (r.data.length > 0) {
          setSelectedSymbol(prev => {
            if (prev && r.data.find(t => t.symbol === prev)) return prev;
            const defaultPair = r.data.find(t => t.symbol === 'XBT/USD');
            return defaultPair ? defaultPair.symbol : r.data.sort((a, b) => (b.volume || 0) - (a.volume || 0))[0].symbol;
          });
        }
      }).catch(() => {});
    };
    const loadOrders = () => { if (disposed) return; api.get('/orders').then(r => { if (!disposed) setOrders(r.data); }).catch(() => {}); };
    const loadBalances = () => { if (disposed) return; api.get('/balances').then(r => { if (!disposed) setBalances(r.data.balances || []); }).catch(() => {}); };

    loadPrices();
    loadOrders();
    loadBalances();
    api.get('/symbols').then(r => { if (!disposed) setSymbols(r.data.map(s => s.websocketName)); }).catch(() => {});

    const refreshInterval = setInterval(() => {
      loadPrices();
      loadOrders();
      loadBalances();
    }, 60000);

    const conn = getConnection();
    const tickerHandler = (data) => {
      setTickers(prev => {
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
    const balanceHandler = (data) => { if (!disposed) setBalances(data); };
    const executionHandler = () => loadOrders();
    const orderHandler = (data) => { if (!disposed) setOrders(data); };

    conn.on('TickerUpdate', tickerHandler);
    conn.on('BalanceUpdate', balanceHandler);
    conn.on('ExecutionUpdate', executionHandler);
    conn.on('OrderUpdate', orderHandler);

    return () => {
      disposed = true;
      clearInterval(refreshInterval);
      conn.off('TickerUpdate', tickerHandler);
      conn.off('BalanceUpdate', balanceHandler);
      conn.off('ExecutionUpdate', executionHandler);
      conn.off('OrderUpdate', orderHandler);
    };
  }, []);

  const selectSymbol = (symbol) => {
    setSelectedSymbol(symbol);
    localStorage.setItem('kraken_selected_pair', symbol);
  };

  const heldAssets = new Set(balances.filter(b => b.total > 0).map(b => b.asset));

  // Sort pinned items by percentage (largest positive to largest negative)
  const topTickers = (pinnedSymbols || [])
    .map(sym => tickers.find(t => t.symbol === sym))
    .filter(Boolean)
    .sort((a, b) => (b.closePriceMovement || 0) - (a.closePriceMovement || 0));

  const tempPinned = tickers
    .filter(t => !pinnedSet.has(t.symbol) && t.closePriceMovement != null && Math.abs(t.closePriceMovement) >= largeMovementThreshold)
    .sort((a, b) => Math.abs(b.closePriceMovement) - Math.abs(a.closePriceMovement));

  const allSorted = [...tickers].sort((a, b) => (b.volume || 0) - (a.volume || 0));

  const loadOrders2 = () => api.get('/orders').then(r => setOrders(r.data)).catch(() => {});

  const balanceCols = useMemo(() => [
    { field: 'asset', minWidth: 80, cellStyle: { fontWeight: 600 } },
    { field: 'total', minWidth: 110, valueFormatter: p => formatNumber(p.value, 4) },
    { field: 'available', minWidth: 110, valueFormatter: p => formatNumber(p.value, 4) },
    { field: 'latestPrice', headerName: 'Price', minWidth: 100, valueFormatter: p => formatPrice(p.value) },
    { field: 'latestValue', headerName: 'Value ($)', minWidth: 100, sort: 'desc',
      valueFormatter: p => p.value != null ? '$' + formatNumber(p.value) : '',
    },
    { field: 'latestValueGbp', headerName: 'Value (£)', minWidth: 100,
      valueFormatter: p => p.value ? '\u00A3' + formatNumber(p.value) : '',
    },
    { field: 'totalCostBasis', headerName: 'Cost', minWidth: 100,
      valueFormatter: p => p.value != null ? '$' + formatNumber(p.value) : '',
    },
    { field: 'netProfitLoss', headerName: 'Profit', minWidth: 100,
      valueFormatter: p => p.value != null ? '$' + formatNumber(p.value) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {},
    },
    { field: 'netProfitLossPercentage', headerName: 'Profit %', minWidth: 90,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(1) + '%' : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {},
    },
    { field: 'portfolioPercentage', headerName: '%', minWidth: 70,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(1) + '%' : '',
    },
    { field: 'orderCoveredQty', headerName: 'Covered', minWidth: 100,
      valueFormatter: p => p.value != null ? formatNumber(p.value, 4) : '',
    },
    { field: 'orderUncoveredQty', headerName: 'Uncovered', minWidth: 100,
      valueFormatter: p => p.value != null ? formatNumber(p.value, 4) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--yellow)' } : {},
    },
  ], []);

  const balanceDefaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  return (
    <div className="dashboard">
      {config.showTickers && (
        <div className="dashboard-tickers">
          {topTickers.map(t => (
            <TickerCard
              key={t.symbol}
              data={t}
              selected={t.symbol === selectedSymbol}
              onClick={selectSymbol}
              onRemove={onUnpin}
            />
          ))}
          {tempPinned.map(t => (
            <TickerCard
              key={t.symbol}
              data={t}
              selected={t.symbol === selectedSymbol}
              onClick={selectSymbol}
              tempPinned
            />
          ))}
        </div>
      )}

      <div className="dashboard-main">
        <div className="dashboard-center">
          {config.showChart && selectedSymbol ? (
            <div className="dashboard-chart">
              <ChartPage symbol={selectedSymbol} />
            </div>
          ) : (
            <div className="dashboard-chart" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', color: 'var(--text-muted)' }}>
              Select a pair to view chart
            </div>
          )}

          {config.showOrders && (
            <div className="dashboard-bottom">
              <div className="panel">
                <div className="panel-header">
                  <div className="panel-tabs">
                    <button className={`panel-tab${bottomTab === 'orders' ? ' active' : ''}`} onClick={() => setBottomTab('orders')}>Open Orders</button>
                    <button className={`panel-tab${bottomTab === 'balances' ? ' active' : ''}`} onClick={() => setBottomTab('balances')}>Balances</button>
                  </div>
                </div>
                <div className="panel-body">
                  <div style={{ height: '100%', width: '100%' }}>
                    {bottomTab === 'orders' && (
                      <OpenOrdersGrid
                        orders={orders}
                        symbols={symbols}
                        onOrderChanged={loadOrders2}
                      />
                    )}
                    {bottomTab === 'balances' && (
                      <AgGridReact
                        theme={gridTheme}
                        rowData={balances.filter(b => b.total > 0 && (!hideAlmostZeroBalances || (b.total >= 0.0001 && (b.latestValue || 0) >= 0.01)))}
                        columnDefs={balanceCols}
                        defaultColDef={balanceDefaultColDef}
                        domLayout="normal"
                        headerHeight={32}
                        rowHeight={30}
                        suppressCellFocus
                      />
                    )}
                  </div>
                </div>
              </div>
            </div>
          )}
        </div>

        {config.showWatchlist && (
          <div className="dashboard-sidebar">
            <Watchlist
              tickers={allSorted}
              heldAssets={heldAssets}
              selectedSymbol={selectedSymbol}
              onSelect={selectSymbol}
              pinnedSet={pinnedSet}
              onPin={onPin}
              onUnpin={onUnpin}
            />
          </div>
        )}
      </div>
    </div>
  );
}
