import { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function LedgerPage() {
  const [rowData, setRowData] = useState([]);
  const gridRef = useRef(null);
  const { gridTheme } = useTheme();

  const loadLedger = useCallback(() => {
    api.get('/ledger').then(r => setRowData(r.data)).catch(console.error);
  }, []);

  useEffect(() => {
    loadLedger();
    const conn = getConnection();
    conn.on('TradesUpdated', loadLedger);
    return () => conn.off('TradesUpdated', loadLedger);
  }, [loadLedger]);

  const columnDefs = useMemo(() => [
    { field: 'timestamp', headerName: 'Time', minWidth: 160, valueFormatter: p => new Date(p.value).toLocaleString() },
    { field: 'type', headerName: 'Type', minWidth: 90 },
    { field: 'subType', headerName: 'SubType', minWidth: 90 },
    { field: 'asset', headerName: 'Asset', minWidth: 80 },
    { field: 'quantity', headerName: 'Quantity', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(6) },
    { field: 'fee', headerName: 'Fee', minWidth: 90, valueFormatter: p => Number(p.value).toFixed(6) },
    { field: 'feePercentage', headerName: 'Fee%', minWidth: 70, valueFormatter: p => Number(p.value).toFixed(2) + '%' },
    { field: 'balanceAfter', headerName: 'Balance', minWidth: 120, valueFormatter: p => Number(p.value).toFixed(4) },
    { field: 'referenceId', headerName: 'Ref', minWidth: 140 },
  ], []);

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <div style={{ padding: '4px 8px', borderBottom: '1px solid var(--border)', background: 'var(--bg-secondary)' }}>
        <button onClick={() => gridRef.current?.api.exportDataAsCsv({ fileName: 'ledger.csv' })}
          style={{ padding: '3px 10px', fontSize: 12, border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', cursor: 'pointer' }}>
          Export CSV
        </button>
      </div>
      <div style={{ flex: 1 }}>
        <AgGridReact ref={gridRef} theme={gridTheme} rowData={rowData} columnDefs={columnDefs} defaultColDef={defaultColDef} getRowId={p => p.data.id} />
      </div>
    </div>
  );
}
