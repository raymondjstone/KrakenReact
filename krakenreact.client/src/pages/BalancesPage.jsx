import { useState, useEffect, useMemo } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function BalancesPage() {
  const [rowData, setRowData] = useState([]);
  const [total, setTotal] = useState(0);
  const [usdGbpRate, setUsdGbpRate] = useState(0);
  const { gridTheme } = useTheme();

  useEffect(() => {
    const loadBalances = () => {
      api.get('/balances').then(r => {
        setRowData(r.data.balances);
        setTotal(r.data.balances.reduce((sum, b) => sum + b.latestValue, 0));
        setUsdGbpRate(r.data.usdGbpRate || 0);
      }).catch(console.error);
    };
    loadBalances();

    const refreshInterval = setInterval(loadBalances, 60000);

    const conn = getConnection();
    const handler = (data, rate) => {
      setRowData(data);
      setTotal(data.reduce((sum, b) => sum + b.latestValue, 0));
      if (rate != null) setUsdGbpRate(rate);
    };
    conn.on('BalanceUpdate', handler);
    return () => {
      clearInterval(refreshInterval);
      conn.off('BalanceUpdate', handler);
    };
  }, []);

  const columnDefs = useMemo(() => [
    { field: 'asset', headerName: 'Asset', minWidth: 100 },
    { field: 'total', headerName: 'Total', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'locked', headerName: 'Locked', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'available', headerName: 'Available', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'latestPrice', headerName: 'Price', minWidth: 110, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'latestValue', headerName: 'Value ($)', minWidth: 110, sort: 'desc',
      valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'latestValueGbp', headerName: 'Value (£)', minWidth: 110,
      valueFormatter: p => p.value != null ? '\u00A3' + Number(p.value).toFixed(2) : '' },
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
        {usdGbpRate > 0 && (
          <span style={{ color: 'var(--text-muted)', fontSize: '0.85em', marginLeft: 8 }}>
            ({'\u00A3'}{(total * usdGbpRate).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })})
          </span>
        )}
      </div>
      <div style={{ flex: 1 }}>
        <AgGridReact theme={gridTheme} rowData={rowData} columnDefs={columnDefs} defaultColDef={defaultColDef} />
      </div>
    </div>
  );
}
