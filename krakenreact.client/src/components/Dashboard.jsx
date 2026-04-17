import { useState, useEffect, useMemo, useRef, useCallback } from 'react';
import GridLayout, { WidthProvider } from 'react-grid-layout/legacy';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';

ModuleRegistry.registerModules([AllCommunityModule]);
import TickerCard from './TickerCard';
import Watchlist from './Watchlist';
import OpenOrdersGrid from './OpenOrdersGrid';
import OrderDialog from './OrderDialog';
import ChartPage from '../pages/ChartPage';
import OrderBook from './OrderBook';
import { formatPrice, formatNumber } from '../utils/formatters';
import { useTheme } from '../context/ThemeContext';

const ResponsiveGrid = WidthProvider(GridLayout);

const LAYOUT_STORAGE_KEY = 'kraken_dashboard_layout_v1';
const GRID_COLS = 12;
const GRID_ROW_HEIGHT = 40;

const DEFAULT_LAYOUT = [
  { i: 'chart',     x: 0, y: 0,  w: 7, h: 12, minW: 3, minH: 4 },
  { i: 'orderbook', x: 7, y: 0,  w: 2, h: 12, minW: 2, minH: 4 },
  { i: 'bottom',    x: 0, y: 12, w: 9, h: 6,  minW: 3, minH: 3 },
  { i: 'watchlist', x: 9, y: 0,  w: 3, h: 18, minW: 2, minH: 4 },
];

function loadLayout() {
  try {
    const raw = localStorage.getItem(LAYOUT_STORAGE_KEY);
    if (!raw) return DEFAULT_LAYOUT;
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return DEFAULT_LAYOUT;
    // Merge with defaults so newly added keys still appear if schema changes
    const byId = new Map(parsed.map(l => [l.i, l]));
    return DEFAULT_LAYOUT.map(d => ({ ...d, ...(byId.get(d.i) || {}) }));
  } catch {
    return DEFAULT_LAYOUT;
  }
}

export default function Dashboard({ config, pinnedSymbols, pinnedSet, onPin, onUnpin, largeMovementThreshold = 5, hideAlmostZeroBalances, orderPriceOffsets, orderQtyPercentages, orderBookDepth }) {
  const [tickers, setTickers] = useState([]);
  const [selectedSymbol, setSelectedSymbol] = useState(() => localStorage.getItem('kraken_selected_pair') || '');
  const [bottomTab, setBottomTab] = useState('orders');
  const [orders, setOrders] = useState([]);
  const [balances, setBalances] = useState([]);
  const [symbols, setSymbols] = useState([]);
  const [orderDialogOpen, setOrderDialogOpen] = useState(false);
  const [orderBalanceCtx, setOrderBalanceCtx] = useState(null);
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

  const getUsdAvailable = () => {
    const usdBal = balances.find(b => b.asset === 'USD');
    return usdBal?.available || 0;
  };

  const openBalanceOrder = (balanceRow) => {
    // Find the matching ticker to get the websocket symbol for order placement
    const ticker = tickers.find(t => t.base === balanceRow.asset && t.ccy === 'USD');
    if (!ticker) return;
    setOrderBalanceCtx({
      symbol: ticker.symbol.replace('/', ''),
      price: ticker.closePrice || balanceRow.latestPrice,
      available: balanceRow.available,
      uncoveredQty: balanceRow.orderUncoveredQty || 0,
      usdAvailable: getUsdAvailable(),
    });
    setOrderDialogOpen(true);
  };

  const openTickerOrder = (tickerData) => {
    if (!tickerData) return;
    // Find balance for this ticker's base asset to get available/uncovered info
    const bal = balances.find(b => b.asset === tickerData.base);
    setOrderBalanceCtx({
      symbol: tickerData.symbol.replace('/', ''),
      price: tickerData.closePrice || 0,
      available: bal?.available || 0,
      uncoveredQty: bal?.orderUncoveredQty || 0,
      usdAvailable: getUsdAvailable(),
    });
    setOrderDialogOpen(true);
  };

  // Map an order symbol (e.g. "XBTUSD") to a ticker symbol (e.g. "XBT/USD")
  const selectOrderSymbol = (orderSymbol) => {
    // Try direct match with slash inserted (orders strip the slash)
    const match = tickers.find(t => t.symbol.replace('/', '') === orderSymbol);
    if (match) { selectSymbol(match.symbol); return; }
    // Try matching by normalized base
    const ticker = tickers.find(t => (t.displaySymbol || t.symbol).replace('/', '') === orderSymbol);
    if (ticker) { selectSymbol(ticker.symbol); return; }
  };

  // Map a balance asset (e.g. "BTC") to its USD ticker symbol
  const selectBalanceAsset = (asset) => {
    const match = tickers.find(t => t.base === asset && t.ccy === 'USD');
    if (match) { selectSymbol(match.symbol); return; }
    // Fallback: first ticker with matching base
    const fallback = tickers.find(t => t.base === asset);
    if (fallback) selectSymbol(fallback.symbol);
  };

  const fiatAssets = new Set(['USD', 'USDT', 'USDC', 'GBP', 'EUR', 'CAD', 'AUD', 'JPY', 'CHF']);

  const balanceCols = useMemo(() => [
    { headerName: '', flex: 0, width: 65, cellRenderer: p => {
      if (!p.data || fiatAssets.has(p.data.asset)) return null;
      return <button onClick={() => openBalanceOrder(p.data)} style={{ padding: '2px 6px', fontSize: 10, cursor: 'pointer', fontWeight: 600 }}>Order</button>;
    }},
    { field: 'asset', minWidth: 80, cellRenderer: p => (
      <span style={{ fontWeight: 600, cursor: 'pointer', color: 'var(--yellow)', textDecoration: 'underline' }} onClick={() => selectBalanceAsset(p.value)}>{p.value}</span>
    )},
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
  ], [selectBalanceAsset, openBalanceOrder]);

  const balanceDefaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  const [layout, setLayout] = useState(loadLayout);
  const layoutContainerRef = useRef(null);

  const onLayoutChange = useCallback((next) => {
    setLayout(next);
    try { localStorage.setItem(LAYOUT_STORAGE_KEY, JSON.stringify(next)); } catch { /* quota — ignore */ }
  }, []);

  // RGL resizes panel containers directly; the chart listens to window resize,
  // so nudge it when a panel changes size.
  const nudgeResize = useCallback(() => {
    window.dispatchEvent(new Event('resize'));
  }, []);

  const panels = {
    chart: (
      <div key="chart" className="grid-panel">
        <div className="grid-panel-header panel-drag-handle">
          <span className="grid-panel-title">Chart</span>
        </div>
        <div className="grid-panel-body">
          {selectedSymbol ? (
            <ChartPage
              symbol={selectedSymbol}
              displaySymbol={tickers.find(t => t.symbol === selectedSymbol)?.displaySymbol}
            />
          ) : (
            <div className="grid-panel-empty">Select a pair to view chart</div>
          )}
        </div>
      </div>
    ),
    orderbook: (
      <div key="orderbook" className="grid-panel">
        <div className="grid-panel-header panel-drag-handle">
          <span className="grid-panel-title">Order Book</span>
        </div>
        <div className="grid-panel-body">
          {selectedSymbol ? (
            <OrderBook symbol={selectedSymbol} depth={orderBookDepth || 25} />
          ) : (
            <div className="grid-panel-empty">No pair selected</div>
          )}
        </div>
      </div>
    ),
    bottom: (
      <div key="bottom" className="grid-panel">
        <div className="grid-panel-header panel-drag-handle">
          <div className="panel-tabs">
            <button className={`panel-tab${bottomTab === 'orders' ? ' active' : ''}`} onClick={() => setBottomTab('orders')}>Open Orders</button>
            <button className={`panel-tab${bottomTab === 'balances' ? ' active' : ''}`} onClick={() => setBottomTab('balances')}>Balances</button>
          </div>
        </div>
        <div className="grid-panel-body">
          <div style={{ height: '100%', width: '100%' }}>
            {bottomTab === 'orders' && (
              <OpenOrdersGrid
                orders={orders}
                symbols={symbols}
                onOrderChanged={loadOrders2}
                onSymbolClick={selectOrderSymbol}
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
    ),
    watchlist: (
      <div key="watchlist" className="grid-panel">
        <div className="grid-panel-header panel-drag-handle">
          <span className="grid-panel-title">Watchlist</span>
        </div>
        <div className="grid-panel-body">
          <Watchlist
            tickers={allSorted}
            heldAssets={heldAssets}
            selectedSymbol={selectedSymbol}
            onSelect={selectSymbol}
            pinnedSet={pinnedSet}
            onPin={onPin}
            onUnpin={onUnpin}
            onOrder={openTickerOrder}
          />
        </div>
      </div>
    ),
  };

  const visibleIds = [
    config.showChart ? 'chart' : null,
    config.showChart ? 'orderbook' : null,
    config.showOrders ? 'bottom' : null,
    config.showWatchlist ? 'watchlist' : null,
  ].filter(Boolean);

  const activeLayout = layout.filter(l => visibleIds.includes(l.i));

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
              onOrder={openTickerOrder}
            />
          ))}
          {tempPinned.map(t => (
            <TickerCard
              key={t.symbol}
              data={t}
              selected={t.symbol === selectedSymbol}
              onClick={selectSymbol}
              tempPinned
              onOrder={openTickerOrder}
            />
          ))}
        </div>
      )}

      <div className="dashboard-grid-wrap" ref={layoutContainerRef}>
        <ResponsiveGrid
          className="dashboard-grid"
          layout={activeLayout}
          cols={GRID_COLS}
          rowHeight={GRID_ROW_HEIGHT}
          margin={[6, 6]}
          containerPadding={[6, 6]}
          draggableHandle=".panel-drag-handle"
          onLayoutChange={onLayoutChange}
          onResize={nudgeResize}
          onResizeStop={nudgeResize}
          onDragStop={nudgeResize}
          compactType="vertical"
          preventCollision={false}
        >
          {visibleIds.map(id => panels[id])}
        </ResponsiveGrid>
      </div>

      <OrderDialog
        isOpen={orderDialogOpen}
        onClose={(ok) => { setOrderDialogOpen(false); setOrderBalanceCtx(null); if (ok) loadOrders2(); }}
        symbols={symbols}
        balanceContext={orderBalanceCtx}
        priceOffsets={orderPriceOffsets}
        qtyPercentages={orderQtyPercentages}
      />
    </div>
  );
}
