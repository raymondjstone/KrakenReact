import { useState, useEffect, useMemo } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function DelistedPairsPage() {
  const [rowData, setRowData] = useState([]);
  const [filterText, setFilterText] = useState('');
  const { gridTheme } = useTheme();

  const loadDelistedPairs = () => {
    api.get('/delistedpairs')
      .then(r => setRowData(r.data))
      .catch(err => console.error('Failed to load delisted pairs:', err));
  };

  useEffect(() => {
    loadDelistedPairs();
  }, []);

  const columnDefs = useMemo(() => [
    { field: 'symbol', headerName: 'Symbol', minWidth: 150, filter: true },
    { 
      field: 'status', 
      headerName: 'Status', 
      minWidth: 100,
      cellStyle: params => params.value === 'active' 
        ? { color: 'var(--green)', fontWeight: 600 } 
        : { color: 'var(--red)', fontWeight: 600 }
    },
    { 
      field: 'lastPriceDate', 
      headerName: 'Last Price Date', 
      minWidth: 180,
      valueFormatter: p => p.value ? new Date(p.value).toLocaleString() : ''
    },
    { 
      field: 'lastPrice', 
      headerName: 'Last Price', 
      minWidth: 120,
      valueFormatter: p => p.value != null ? '$' + Number(p.value).toFixed(4) : ''
    },
    { 
      field: 'hasHistoricalData', 
      headerName: 'Has CSV Data', 
      minWidth: 120,
      valueFormatter: p => p.value ? 'Yes' : 'No',
      cellStyle: p => p.value ? { color: 'var(--green)' } : {}
    },
  ], []);

  const defaultColDef = useMemo(() => ({ 
    sortable: true, 
    filter: true, 
    resizable: true, 
    flex: 1 
  }), []);

  const filteredData = useMemo(() => {
    if (!filterText) return rowData;
    const lower = filterText.toLowerCase();
    return rowData.filter(row => 
      row.symbol?.toLowerCase().includes(lower) ||
      row.status?.toLowerCase().includes(lower)
    );
  }, [rowData, filterText]);

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <div style={{ padding: '12px 16px', background: 'var(--bg-secondary)', borderBottom: '1px solid var(--border)', display: 'flex', gap: 12, alignItems: 'center' }}>
        <h3 style={{ margin: 0, color: 'var(--text-primary)', flex: 1 }}>
          Delisted & Active Currency Pairs
        </h3>
        <input
          type="text"
          placeholder="Search pairs..."
          value={filterText}
          onChange={e => setFilterText(e.target.value)}
          style={{
            padding: '6px 12px',
            border: '1px solid var(--border)',
            borderRadius: 4,
            background: 'var(--bg-primary)',
            color: 'var(--text-primary)',
            fontSize: 14,
            width: 250
          }}
        />
        <button
          onClick={loadDelistedPairs}
          style={{
            padding: '6px 12px',
            background: 'var(--green)',
            color: 'white',
            border: 'none',
            borderRadius: 4,
            cursor: 'pointer',
            fontWeight: 600
          }}
        >
          Refresh
        </button>
      </div>
      <div style={{ flex: 1 }}>
        <AgGridReact 
          theme={gridTheme}
          rowData={filteredData} 
          columnDefs={columnDefs} 
          defaultColDef={defaultColDef}
        />
      </div>
    </div>
  );
}
