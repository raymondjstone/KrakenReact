import { useState, useEffect, useMemo, useCallback } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { useTheme } from '../context/ThemeContext';
import OrderDialog from '../components/OrderDialog';
import PortfolioHistoryChart from '../components/PortfolioHistoryChart';

ModuleRegistry.registerModules([AllCommunityModule]);

const FIAT_ASSETS = new Set(['USD', 'USDT', 'USDC', 'GBP', 'EUR', 'CAD', 'AUD', 'JPY', 'CHF']);

export default function BalancesPage({ hideAlmostZeroBalances }) {
  const [rowData, setRowData] = useState([]);
  const [total, setTotal] = useState(0);
  const [totalGbp, setTotalGbp] = useState(0);
  const [symbols, setSymbols] = useState([]);
  const [orderDialogOpen, setOrderDialogOpen] = useState(false);
  const [orderBalanceCtx, setOrderBalanceCtx] = useState(null);
  const [periodPl, setPeriodPl] = useState({});
  const [atrData, setAtrData] = useState({});
  const [showHistory, setShowHistory] = useState(false);
  const [historyData, setHistoryData] = useState([]);
  const [ladderBalance, setLadderBalance] = useState(null);
  const [ladderOrders, setLadderOrders] = useState([]);
  const [serverSettings, setServerSettings] = useState(null);
  const { gridTheme } = useTheme();

  const updateFromBalances = (balances) => {
    setRowData(balances);
    setTotal(balances.reduce((sum, b) => sum + (b.latestValue || 0), 0));
    setTotalGbp(balances.reduce((sum, b) => sum + (b.latestValueGbp || 0), 0));
  };

  useEffect(() => {
    let disposed = false;
    const loadBalances = () => {
      if (disposed) return;
      api.get('/balances').then(r => { if (!disposed) updateFromBalances(r.data.balances || []); }).catch(console.error);
    };
    loadBalances();
    api.get('/symbols').then(r => { if (!disposed) setSymbols(r.data.map(s => s.websocketName)); }).catch(() => {});
    api.get('/balances/period-pl').then(r => {
      if (!disposed) {
        const map = {};
        (r.data || []).forEach(p => { map[p.asset] = p; });
        setPeriodPl(map);
      }
    }).catch(() => {});
    api.get('/balances/atr').then(r => {
      if (!disposed) {
        const map = {};
        (r.data || []).forEach(a => { map[a.asset] = a; });
        setAtrData(map);
      }
    }).catch(() => {});

    api.get('/settings').then(r => { if (!disposed) setServerSettings(r.data); }).catch(() => {});
    const refreshInterval = setInterval(loadBalances, 60000);

    const conn = getConnection();
    const handler = (data) => { if (!disposed) updateFromBalances(data); };
    conn.on('BalanceUpdate', handler);
    return () => {
      disposed = true;
      clearInterval(refreshInterval);
      conn.off('BalanceUpdate', handler);
    };
  }, []);

  const openBalanceOrder = (balanceRow) => {
    const usdBal = rowData.find(b => b.asset === 'USD');
    setOrderBalanceCtx({
      symbol: balanceRow.asset + 'USD',
      price: balanceRow.latestPrice || 0,
      available: balanceRow.available || 0,
      uncoveredQty: balanceRow.orderUncoveredQty || 0,
      usdAvailable: usdBal?.available || 0,
    });
    setOrderDialogOpen(true);
  };

  const loadOrders = () => api.get('/orders').then(() => {}).catch(() => {});

  const openLadder = (balance) => {
    setLadderBalance(balance);
    api.get('/orders').then(r => {
      const assetBase = balance.asset.toUpperCase();
      const relevant = (r.data || []).filter(o => {
        const sym = (o.symbol || o.websocketName || '').toUpperCase().replace('/', '');
        return sym.startsWith(assetBase) && (o.status === 'Open' || o.status === 'PartiallyFilled');
      });
      setLadderOrders(relevant);
    }).catch(() => setLadderOrders([]));
  };

  const loadHistory = useCallback(() => {
    api.get('/portfolio/history?days=30')
      .then(r => setHistoryData(r.data || []))
      .catch(() => {});
  }, []);

  const handleToggleHistory = () => {
    if (!showHistory && historyData.length === 0) loadHistory();
    setShowHistory(v => !v);
  };

  const handleClosePosition = async (balance) => {
    if (!confirm(`Market-sell ALL ${balance.available.toFixed(4)} ${balance.asset} at current price? This cannot be undone.`)) return;
    try {
      const r = await api.post(`/orders/close/${encodeURIComponent(balance.asset)}`);
      alert(r.data.message || 'Close order placed');
      loadOrders();
    } catch (err) {
      alert(err.response?.data?.error || 'Failed to place close order');
    }
  };

  const columnDefs = useMemo(() => [
    { headerName: '', flex: 0, width: 65, cellRenderer: p => {
      if (!p.data || FIAT_ASSETS.has(p.data.asset)) return null;
      return <button onClick={() => openBalanceOrder(p.data)} style={{ padding: '2px 6px', fontSize: 10, cursor: 'pointer', fontWeight: 600 }}>Order</button>;
    }},
    { headerName: 'Close', flex: 0, width: 60, cellRenderer: p => {
      if (!p.data || FIAT_ASSETS.has(p.data.asset) || (p.data.available || 0) <= 0) return null;
      return <button onClick={() => handleClosePosition(p.data)} style={{ padding: '2px 6px', fontSize: 10, cursor: 'pointer', color: 'var(--red)', fontWeight: 600 }}>Close</button>;
    }},
    { headerName: 'Ladder', flex: 0, width: 70, cellRenderer: p => {
      if (!p.data || FIAT_ASSETS.has(p.data.asset) || !p.data.latestPrice) return null;
      return <button onClick={() => openLadder(p.data)} style={{ padding: '2px 6px', fontSize: 10, cursor: 'pointer', color: 'var(--text-secondary)', fontWeight: 600 }}>Ladder</button>;
    }},
    { headerName: '1d%', minWidth: 80,
      valueGetter: p => periodPl[p.data?.asset]?.pl1d ?? null,
      valueFormatter: p => p.value != null ? (p.value >= 0 ? '+' : '') + Number(p.value).toFixed(1) + '%' : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { headerName: '7d%', minWidth: 80,
      valueGetter: p => periodPl[p.data?.asset]?.pl7d ?? null,
      valueFormatter: p => p.value != null ? (p.value >= 0 ? '+' : '') + Number(p.value).toFixed(1) + '%' : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { headerName: '30d%', minWidth: 80,
      valueGetter: p => periodPl[p.data?.asset]?.pl30d ?? null,
      valueFormatter: p => p.value != null ? (p.value >= 0 ? '+' : '') + Number(p.value).toFixed(1) + '%' : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { field: 'asset', headerName: 'Asset', minWidth: 100 },
    { field: 'total', headerName: 'Total', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'locked', headerName: 'Locked', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'available', headerName: 'Available', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'latestPrice', headerName: 'Price', minWidth: 110, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'latestValue', headerName: 'Value ($)', minWidth: 110, sort: 'desc',
      valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'latestValueGbp', headerName: 'Value (£)', minWidth: 110,
      valueFormatter: p => p.value ? '£' + Number(p.value).toFixed(2) : '' },
    { field: 'totalCostBasis', headerName: 'Cost Basis', minWidth: 110,
      valueFormatter: p => p.value != null ? '$' + Number(p.value).toFixed(2) : '' },
    { field: 'totalFees', headerName: 'Fees', minWidth: 90,
      valueFormatter: p => p.value != null ? '$' + Number(p.value).toFixed(2) : '' },
    { field: 'netProfitLoss', headerName: 'P/L ($)', minWidth: 110,
      valueFormatter: p => p.value != null ? '$' + Number(p.value).toFixed(2) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { field: 'netProfitLossPercentage', headerName: 'P/L %', minWidth: 90,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(1) + '%' : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { headerName: 'ATR%', minWidth: 80,
      valueGetter: p => atrData[p.data?.asset]?.atrPct ?? null,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) + '%' : '',
      cellStyle: p => p.value > 5 ? { color: 'var(--red)' } : p.value > 2 ? { color: 'var(--yellow)' } : {} },
    { field: 'portfolioPercentage', headerName: '%', minWidth: 70,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(1) + '%' : '' },
    { field: 'orderCoveredQty', headerName: 'Covered', minWidth: 100,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(4) : '' },
    { field: 'orderUncoveredQty', headerName: 'Uncovered', minWidth: 100,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(4) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--yellow)' } : {} },
    { field: 'orderCoveredValue', headerName: 'Cvrd $', minWidth: 100,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '' },
    { field: 'orderUncoveredValue', headerName: 'Uncvrd $', minWidth: 100,
      valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--yellow)' } : {} },
  ], [periodPl, atrData]);

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <div style={{ padding: '8px 16px', background: 'var(--bg-secondary)', color: 'var(--green)', fontWeight: 'bold', fontSize: 16, borderBottom: '1px solid var(--border)', display: 'flex', alignItems: 'center', gap: 16 }}>
        <span>
          Total Portfolio Value: ${total.toLocaleString(undefined, { minimumFractionDigits: 2 })}
          {totalGbp > 0 && (
            <span style={{ color: 'var(--text-muted)', fontSize: '0.85em', marginLeft: 8 }}>
              ('£'{totalGbp.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })})
            </span>
          )}
        </span>
        <button
          onClick={handleToggleHistory}
          style={{ marginLeft: 'auto', padding: '3px 10px', fontSize: 12, background: showHistory ? 'var(--green)' : 'var(--bg-input)', color: showHistory ? 'white' : 'var(--text-secondary)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontWeight: 500 }}
        >
          {showHistory ? 'Hide Chart' : '30d Chart'}
        </button>
      </div>
      {showHistory && (
        <div style={{ height: 160, flexShrink: 0, borderBottom: '1px solid var(--border)', padding: '8px 16px', background: 'var(--bg-card)' }}>
          <PortfolioHistoryChart data={historyData} />
        </div>
      )}
      <div style={{ flex: 1 }}>
        <AgGridReact theme={gridTheme} rowData={hideAlmostZeroBalances ? rowData.filter(b => {
            if ((b.total || 0) < 0.0001) return false;
            if (!b.latestPrice || b.latestPrice === 0) return true;
            return (b.latestValue || 0) >= 0.01;
          }) : rowData} columnDefs={columnDefs} defaultColDef={defaultColDef} />
      </div>
      <OrderDialog
        isOpen={orderDialogOpen}
        onClose={(ok) => { setOrderDialogOpen(false); setOrderBalanceCtx(null); if (ok) loadOrders(); }}
        symbols={symbols}
        balanceContext={orderBalanceCtx}
        priceOffsets={serverSettings?.orderPriceOffsets}
        qtyPercentages={serverSettings?.orderQtyPercentages}
      />
      {ladderBalance && (
        <OrderLadderModal
          balance={ladderBalance}
          orders={ladderOrders}
          serverSettings={serverSettings}
          onClose={() => { setLadderBalance(null); setLadderOrders([]); }}
        />
      )}
    </div>
  );
}

function OrderLadderModal({ balance, orders, serverSettings, onClose }) {
  const price = balance.latestPrice || 0;
  const costBasis = balance.totalCostBasis && balance.total > 0 ? balance.totalCostBasis / balance.total : null;
  const slPct = serverSettings?.stopLossEnabled ? (serverSettings?.stopLossPct || 0) : null;
  const tpPct = serverSettings?.takeProfitEnabled ? (serverSettings?.takeProfitPct || 0) : null;

  const slPrice = costBasis && slPct ? costBasis * (1 - slPct / 100) : null;
  const tpPrice = costBasis && tpPct ? costBasis * (1 + tpPct / 100) : null;

  // Collect all price levels
  const levels = [];
  if (price) levels.push({ price, type: 'current', label: `Current: $${price.toLocaleString(undefined, { maximumFractionDigits: 4 })}`, color: '#3b82f6' });
  if (costBasis) levels.push({ price: costBasis, type: 'cost', label: `Avg Cost: $${costBasis.toLocaleString(undefined, { maximumFractionDigits: 4 })}`, color: '#f59e0b' });
  if (slPrice) levels.push({ price: slPrice, type: 'sl', label: `Stop Loss (-${slPct}%): $${slPrice.toLocaleString(undefined, { maximumFractionDigits: 4 })}`, color: '#ef4444' });
  if (tpPrice) levels.push({ price: tpPrice, type: 'tp', label: `Take Profit (+${tpPct}%): $${tpPrice.toLocaleString(undefined, { maximumFractionDigits: 4 })}`, color: '#22c55e' });

  orders.forEach(o => {
    const op = Number(o.price || o.orderDetailsPrice || 0);
    if (op > 0) {
      const side = (o.side || '').toLowerCase();
      levels.push({
        price: op, type: 'order',
        label: `${side === 'sell' ? 'Sell' : 'Buy'} Order: $${op.toLocaleString(undefined, { maximumFractionDigits: 4 })} (${Number(o.quantity || 0).toFixed(4)})`,
        color: side === 'sell' ? '#ef4444' : '#22c55e',
        dashed: true,
      });
    }
  });

  const allPrices = levels.map(l => l.price).filter(p => p > 0);
  const minP = Math.min(...allPrices);
  const maxP = Math.max(...allPrices);
  const range = maxP - minP || maxP * 0.1;
  const padded = range * 0.15;
  const lo = minP - padded;
  const hi = maxP + padded;
  const totalRange = hi - lo;

  const pctFromBottom = (p) => ((p - lo) / totalRange) * 100;

  const barH = 400;

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1100 }} onClick={onClose}>
      <div style={{ background: 'var(--dialog-bg, var(--bg-card))', borderRadius: 8, padding: 24, width: 480, border: '1px solid var(--border)', maxHeight: '90vh', overflow: 'auto' }} onClick={e => e.stopPropagation()}>
        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 16 }}>
          <h3 style={{ margin: 0, color: 'var(--text-primary)', fontSize: 15 }}>{balance.asset} Order Ladder</h3>
          <button onClick={onClose} style={{ background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', fontSize: 18, lineHeight: 1 }}>×</button>
        </div>

        <div style={{ display: 'flex', gap: 16 }}>
          {/* Vertical price bar */}
          <div style={{ width: 80, position: 'relative', height: barH, flexShrink: 0 }}>
            {/* Background bar */}
            <div style={{ position: 'absolute', left: 36, top: 0, bottom: 0, width: 8, background: 'var(--bg-input)', borderRadius: 4 }} />

            {levels.map((lvl, i) => {
              const pct = pctFromBottom(lvl.price);
              const top = barH - (pct / 100) * barH;
              return (
                <div key={i} style={{ position: 'absolute', left: 0, top: top - 1, right: 0 }}>
                  <div style={{
                    position: 'absolute', left: 30, width: 20, height: 2,
                    background: lvl.color,
                    borderTop: lvl.dashed ? `2px dashed ${lvl.color}` : undefined,
                  }} />
                  <div style={{
                    position: 'absolute', left: 55, fontSize: 10, color: lvl.color,
                    whiteSpace: 'nowrap', transform: 'translateY(-50%)', fontWeight: lvl.type === 'current' ? 700 : 400,
                  }}>
                    {lvl.type === 'current' ? '●' : lvl.type === 'cost' ? '◆' : lvl.type === 'sl' ? '▼' : lvl.type === 'tp' ? '▲' : '○'}
                  </div>
                </div>
              );
            })}
          </div>

          {/* Legend */}
          <div style={{ flex: 1 }}>
            {[...levels].sort((a, b) => b.price - a.price).map((lvl, i) => (
              <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10, fontSize: 13 }}>
                <div style={{ width: 12, height: 12, borderRadius: 2, background: lvl.color, flexShrink: 0, border: lvl.dashed ? `2px dashed ${lvl.color}` : 'none' }} />
                <span style={{ color: 'var(--text-primary)', fontWeight: lvl.type === 'current' ? 700 : 400 }}>{lvl.label}</span>
              </div>
            ))}
            {costBasis && price && (
              <div style={{ marginTop: 16, padding: '8px 12px', background: 'var(--bg-primary)', borderRadius: 4, fontSize: 12, color: 'var(--text-muted)' }}>
                Change from cost: <strong style={{ color: price >= costBasis ? '#22c55e' : '#ef4444' }}>
                  {price >= costBasis ? '+' : ''}{((price - costBasis) / costBasis * 100).toFixed(2)}%
                </strong>
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
