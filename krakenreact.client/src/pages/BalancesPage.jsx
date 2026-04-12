import { useState, useEffect, useMemo } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { useTheme } from '../context/ThemeContext';
import OrderDialog from '../components/OrderDialog';

ModuleRegistry.registerModules([AllCommunityModule]);

const FIAT_ASSETS = new Set(['USD', 'USDT', 'USDC', 'GBP', 'EUR', 'CAD', 'AUD', 'JPY', 'CHF']);

export default function BalancesPage({ hideAlmostZeroBalances }) {
  const [rowData, setRowData] = useState([]);
  const [total, setTotal] = useState(0);
  const [totalGbp, setTotalGbp] = useState(0);
  const [symbols, setSymbols] = useState([]);
  const [orderDialogOpen, setOrderDialogOpen] = useState(false);
  const [orderBalanceCtx, setOrderBalanceCtx] = useState(null);
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

  const columnDefs = useMemo(() => [
    { headerName: '', flex: 0, width: 65, cellRenderer: p => {
      if (!p.data || FIAT_ASSETS.has(p.data.asset)) return null;
      return <button onClick={() => openBalanceOrder(p.data)} style={{ padding: '2px 6px', fontSize: 10, cursor: 'pointer', fontWeight: 600 }}>Order</button>;
    }},
    { field: 'asset', headerName: 'Asset', minWidth: 100 },
    { field: 'total', headerName: 'Total', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'locked', headerName: 'Locked', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'available', headerName: 'Available', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'latestPrice', headerName: 'Price', minWidth: 110, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'latestValue', headerName: 'Value ($)', minWidth: 110, sort: 'desc',
      valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'latestValueGbp', headerName: 'Value (£)', minWidth: 110,
      valueFormatter: p => p.value ? '\u00A3' + Number(p.value).toFixed(2) : '' },
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
  ], []);

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <div style={{ padding: '8px 16px', background: 'var(--bg-secondary)', color: 'var(--green)', fontWeight: 'bold', fontSize: 16, borderBottom: '1px solid var(--border)' }}>
        Total Portfolio Value: ${total.toLocaleString(undefined, { minimumFractionDigits: 2 })}
        {totalGbp > 0 && (
          <span style={{ color: 'var(--text-muted)', fontSize: '0.85em', marginLeft: 8 }}>
            ({'\u00A3'}{totalGbp.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })})
          </span>
        )}
      </div>
      <div style={{ flex: 1 }}>
        <AgGridReact theme={gridTheme} rowData={hideAlmostZeroBalances ? rowData.filter(b => b.total >= 0.0001 && (b.latestValue || 0) >= 0.01) : rowData} columnDefs={columnDefs} defaultColDef={defaultColDef} />
      </div>
      <OrderDialog
        isOpen={orderDialogOpen}
        onClose={(ok) => { setOrderDialogOpen(false); setOrderBalanceCtx(null); if (ok) loadOrders(); }}
        symbols={symbols}
        balanceContext={orderBalanceCtx}
      />
    </div>
  );
}
