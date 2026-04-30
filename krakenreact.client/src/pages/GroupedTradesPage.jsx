import { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { formatPrice } from '../utils/formatters';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function GroupedTradesPage() {
  const [rowData, setRowData] = useState([]);
  const [selectedGroup, setSelectedGroup] = useState(null);
  const gridRef = useRef(null);
  const { gridTheme } = useTheme();

  const loadGroupedTrades = useCallback(() => {
    api.get('/trades/grouped').then(r => setRowData(r.data)).catch(console.error);
  }, []);

  useEffect(() => {
    loadGroupedTrades();
    const conn = getConnection();
    conn.on('TradesUpdated', loadGroupedTrades);
    return () => conn.off('TradesUpdated', loadGroupedTrades);
  }, [loadGroupedTrades]);

  const columnDefs = useMemo(() => [
    { field: 'symbol', headerName: 'Symbol', minWidth: 100 },
    { field: 'side', headerName: 'Side', flex: 0, width: 60, cellStyle: p => ({ color: p.value === 'Buy' ? 'var(--green)' : 'var(--red)' }) },
    { field: 'price', headerName: 'Avg Price', minWidth: 110, valueFormatter: p => formatPrice(p.value) },
    { field: 'quantity', headerName: 'Total Qty', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(6) },
    { field: 'quoteQuantity', headerName: 'Total Cost', minWidth: 110, valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'fee', headerName: 'Total Fee', minWidth: 90, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'nettTotal', headerName: 'Nett', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'closedProfitLoss', headerName: 'P&L', minWidth: 100, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { field: 'timestamp', headerName: 'Time', minWidth: 160, valueFormatter: p => new Date(p.value).toLocaleString() },
    { field: 'orderId', headerName: 'OrderId', minWidth: 140 },
  ], []);

  const detailColDefs = useMemo(() => [
    { field: 'id', headerName: 'TradeId', minWidth: 140 },
    { field: 'price', headerName: 'Price', minWidth: 110, valueFormatter: p => formatPrice(p.value) },
    { field: 'quantity', headerName: 'Qty', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(6) },
    { field: 'quoteQuantity', headerName: 'Cost', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'fee', headerName: 'Fee', minWidth: 80, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'nettTotal', headerName: 'Nett', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(2) },
    { field: 'timestamp', headerName: 'Time', minWidth: 160, valueFormatter: p => new Date(p.value).toLocaleString() },
    { field: 'closedProfitLoss', headerName: 'P&L', minWidth: 100, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
  ], []);

  const ledgerColDefs = useMemo(() => [
    { field: 'asset', headerName: 'Asset', minWidth: 80 },
    { field: 'type', headerName: 'Type', minWidth: 80 },
    { field: 'quantity', headerName: 'Quantity', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(6) },
    { field: 'fee', headerName: 'Fee', minWidth: 80, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'balanceAfter', headerName: 'Balance', minWidth: 100, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'feePercentage', headerName: 'Fee%', minWidth: 70, valueFormatter: p => Number(p.value).toFixed(2) + '%' },
  ], []);

  const getRowStyle = useMemo(() => (params) => {
    if (params.data?.side === 'Buy') return { background: 'var(--buy-row-bg)' };
    if (params.data?.side === 'Sell') return { background: 'var(--sell-row-bg)' };
    return {};
  }, []);

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  const constituentTrades = selectedGroup?.constituentTrades || [];
  const allLedgerItems = constituentTrades.flatMap(t => (t.ledgerItems || []).map(l => ({ ...l, tradeId: t.id })));

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <div style={{ padding: '8px 12px', borderBottom: '1px solid var(--border)', display: 'flex', justifyContent: 'flex-end' }}>
        <button onClick={() => gridRef.current?.api.exportDataAsCsv({ fileName: 'grouped-trades.csv' })}
          style={{ padding: '5px 14px', background: 'none', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', color: 'var(--text-muted)', fontSize: 12 }}>
          Export CSV
        </button>
      </div>
      <div style={{ flex: selectedGroup ? '0 0 45%' : 1 }}>
        <AgGridReact
          ref={gridRef}
          theme={gridTheme}
          rowData={rowData}
          columnDefs={columnDefs}
          defaultColDef={defaultColDef}
          getRowStyle={getRowStyle}
          getRowId={p => p.data.id}
          onRowClicked={e => e.data && setSelectedGroup(e.data.id === selectedGroup?.id ? null : e.data)}
          rowSelection="single"
        />
      </div>
      {selectedGroup && (
        <div style={{ flex: '0 0 55%', display: 'flex', flexDirection: 'column', borderTop: '2px solid var(--detail-border)' }}>
          <div style={{ padding: '6px 12px', background: 'var(--detail-header-bg)', color: 'var(--detail-border)', fontWeight: 600, fontSize: 13, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <span>Constituent Trades: {selectedGroup.symbol} {selectedGroup.side} — Order {selectedGroup.orderId}</span>
            <button onClick={() => setSelectedGroup(null)} style={{ background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', fontSize: 16 }}>{'\u2715'}</button>
          </div>
          <div style={{ flex: 1, display: 'flex' }}>
            <div style={{ flex: 1 }}>
              <AgGridReact
                theme={gridTheme}
                rowData={constituentTrades}
                columnDefs={detailColDefs}
                defaultColDef={defaultColDef}
                getRowStyle={getRowStyle}
                headerHeight={28}
                rowHeight={26}
                getRowId={p => p.data.id}
              />
            </div>
            {allLedgerItems.length > 0 && (
              <div style={{ flex: 1, borderLeft: '1px solid var(--border)' }}>
                <AgGridReact
                  theme={gridTheme}
                  rowData={allLedgerItems}
                  columnDefs={ledgerColDefs}
                  defaultColDef={defaultColDef}
                  headerHeight={28}
                  rowHeight={26}
                  getRowId={p => p.data.id}
                />
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
