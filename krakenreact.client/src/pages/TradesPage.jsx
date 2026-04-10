import { useState, useEffect, useMemo, useCallback } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { formatPrice } from '../utils/formatters';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function TradesPage() {
  const [rowData, setRowData] = useState([]);
  const [selectedTrade, setSelectedTrade] = useState(null);
  const { gridTheme } = useTheme();

  const loadTrades = useCallback(() => {
    api.get('/trades').then(r => setRowData(r.data)).catch(console.error);
  }, []);

  useEffect(() => {
    loadTrades();
    const conn = getConnection();
    conn.on('TradesUpdated', loadTrades);
    return () => conn.off('TradesUpdated', loadTrades);
  }, [loadTrades]);

  const columnDefs = useMemo(() => [
    { field: 'symbol', headerName: 'Symbol', minWidth: 100 },
    { field: 'side', headerName: 'Side', flex: 0, width: 60, cellStyle: p => ({ color: p.value === 'Buy' ? 'var(--green)' : 'var(--red)' }) },
    { field: 'type', headerName: 'Type', flex: 0, width: 70 },
    { field: 'price', headerName: 'Price', minWidth: 110, valueFormatter: p => formatPrice(p.value) },
    { field: 'quantity', headerName: 'Qty', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(6) },
    { field: 'quoteQuantity', headerName: 'Cost', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'fee', headerName: 'Fee', minWidth: 80, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'margin', headerName: 'Margin', minWidth: 80, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'nettTotal', headerName: 'Nett', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'timestamp', headerName: 'Time', minWidth: 160, valueFormatter: p => new Date(p.value).toLocaleString() },
    { field: 'closedProfitLoss', headerName: 'P&L', minWidth: 100, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { field: 'positionStatus', headerName: 'Position', minWidth: 90 },
    { field: 'orderId', headerName: 'OrderId', minWidth: 140 },
  ], []);

  const ledgerColDefs = useMemo(() => [
    { field: 'id', headerName: 'Id', minWidth: 140 },
    { field: 'timestamp', headerName: 'Time', minWidth: 150, valueFormatter: p => new Date(p.value).toLocaleString() },
    { field: 'referenceId', headerName: 'Ref', minWidth: 140 },
    { field: 'type', headerName: 'Type', minWidth: 80 },
    { field: 'subType', headerName: 'SubType', minWidth: 80 },
    { field: 'asset', headerName: 'Asset', minWidth: 80 },
    { field: 'quantity', headerName: 'Quantity', minWidth: 110, valueFormatter: p => Number(p.value).toFixed(6) },
    { field: 'fee', headerName: 'Fee', minWidth: 80, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'balanceAfter', headerName: 'Balance', minWidth: 110, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'feePercentage', headerName: 'Fee%', minWidth: 70, valueFormatter: p => Number(p.value).toFixed(2) + '%' },
  ], []);

  const getRowStyle = useMemo(() => (params) => {
    if (params.data?.side === 'Buy') return { background: 'var(--buy-row-bg)' };
    if (params.data?.side === 'Sell') return { background: 'var(--sell-row-bg)' };
    return {};
  }, []);

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  const ledgerItems = selectedTrade?.ledgerItems || [];

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <div style={{ flex: selectedTrade ? '0 0 60%' : 1 }}>
        <AgGridReact
          theme={gridTheme}
          rowData={rowData}
          columnDefs={columnDefs}
          defaultColDef={defaultColDef}
          getRowStyle={getRowStyle}
          getRowId={p => p.data.id}
          onRowClicked={e => e.data && setSelectedTrade(e.data.id === selectedTrade?.id ? null : e.data)}
          rowSelection="single"
        />
      </div>
      {selectedTrade && (
        <div style={{ flex: '0 0 40%', display: 'flex', flexDirection: 'column', borderTop: '2px solid var(--detail-border)' }}>
          <div style={{ padding: '6px 12px', background: 'var(--detail-header-bg)', color: 'var(--detail-border)', fontWeight: 600, fontSize: 13, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <span>Ledger Items: {selectedTrade.symbol} {selectedTrade.side} — Trade {selectedTrade.id}</span>
            <button onClick={() => setSelectedTrade(null)} style={{ background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', fontSize: 16 }}>{'\u2715'}</button>
          </div>
          <div style={{ flex: 1 }}>
            <AgGridReact
              theme={gridTheme}
              rowData={ledgerItems}
              columnDefs={ledgerColDefs}
              defaultColDef={defaultColDef}
              headerHeight={28}
              rowHeight={26}
              getRowId={p => p.data.id}
            />
          </div>
        </div>
      )}
    </div>
  );
}
