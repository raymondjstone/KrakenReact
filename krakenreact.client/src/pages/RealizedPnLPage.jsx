import { useState, useEffect, useCallback, useRef } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function RealizedPnLPage() {
  const [rowData, setRowData] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [summary, setSummary] = useState(null);
  const gridRef = useRef(null);
  const { gridTheme } = useTheme();

  const load = useCallback(() => {
    setLoading(true);
    api.get('/trades/pnl')
      .then(r => {
        const data = r.data || [];
        setRowData(data);
        if (data.length > 0) {
          const totalPnl = data.reduce((sum, t) => sum + (t.pnl || 0), 0);
          const wins = data.filter(t => t.pnl > 0).length;
          const losses = data.filter(t => t.pnl < 0).length;
          setSummary({ totalPnl, wins, losses, total: data.length });
        }
        setLoading(false);
      })
      .catch(err => {
        setError(err.response?.data?.message || 'Failed to load P&L data');
        setLoading(false);
      });
  }, []);

  useEffect(() => { load(); }, [load]);

  const pnlStyle = (value) => ({
    color: value > 0 ? 'var(--green)' : value < 0 ? 'var(--red)' : 'var(--text-muted)',
    fontWeight: Math.abs(value) > 0 ? 600 : 400,
  });

  const columnDefs = [
    {
      field: 'timestamp', headerName: 'Date',
      valueFormatter: p => p.value ? new Date(p.value).toLocaleString() : '',
      minWidth: 160, sort: 'asc',
    },
    { field: 'symbol', headerName: 'Symbol', minWidth: 100 },
    {
      field: 'price', headerName: 'Sell Price',
      valueFormatter: p => p.value ? Number(p.value).toLocaleString(undefined, { maximumFractionDigits: 8 }) : '',
      minWidth: 110,
    },
    {
      field: 'quantity', headerName: 'Qty',
      valueFormatter: p => p.value ? Number(p.value).toLocaleString(undefined, { maximumFractionDigits: 8 }) : '',
      minWidth: 100,
    },
    {
      field: 'quoteQuantity', headerName: 'Proceeds',
      valueFormatter: p => p.value ? `$${Number(p.value).toLocaleString(undefined, { maximumFractionDigits: 2 })}` : '',
      minWidth: 110,
    },
    {
      field: 'fee', headerName: 'Fee',
      valueFormatter: p => p.value ? `$${Number(p.value).toLocaleString(undefined, { maximumFractionDigits: 4 })}` : '',
      minWidth: 90,
    },
    {
      field: 'avgCostBasis', headerName: 'Avg Cost',
      valueFormatter: p => p.value ? Number(p.value).toLocaleString(undefined, { maximumFractionDigits: 8 }) : '—',
      minWidth: 110,
    },
    {
      field: 'pnl', headerName: 'P&L ($)',
      valueFormatter: p => p.value != null ? `${p.value >= 0 ? '+' : ''}$${Number(p.value).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}` : '—',
      cellStyle: p => pnlStyle(p.value),
      minWidth: 110,
    },
    {
      field: 'pnlPct', headerName: 'P&L %',
      valueFormatter: p => p.value != null ? `${p.value >= 0 ? '+' : ''}${Number(p.value).toFixed(2)}%` : '—',
      cellStyle: p => pnlStyle(p.value),
      minWidth: 90,
    },
    {
      field: 'cumulativePnl', headerName: 'Cumulative P&L',
      valueFormatter: p => p.value != null ? `${p.value >= 0 ? '+' : ''}$${Number(p.value).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}` : '—',
      cellStyle: p => pnlStyle(p.value),
      minWidth: 140,
    },
  ];

  const defaultColDef = { resizable: true, sortable: true, filter: true, flex: 1, minWidth: 80 };

  const exportCsv = () => {
    gridRef.current?.api.exportDataAsCsv({ fileName: 'realized-pnl.csv' });
  };

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'hidden', display: 'flex', flexDirection: 'column', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 16, marginBottom: 16, flexWrap: 'wrap' }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Realized P&amp;L</h2>
        <button
          onClick={exportCsv}
          style={{ padding: '5px 14px', background: 'var(--bg-card)', color: 'var(--text-primary)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 13 }}
        >
          Export CSV
        </button>
        {summary && (
          <div style={{ display: 'flex', gap: 20, flexWrap: 'wrap', marginLeft: 8 }}>
            <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>
              {summary.total} sell trades
            </span>
            <span style={{ fontSize: 13, color: 'var(--green)', fontWeight: 600 }}>
              {summary.wins} wins
            </span>
            <span style={{ fontSize: 13, color: 'var(--red)', fontWeight: 600 }}>
              {summary.losses} losses
            </span>
            <span style={{ fontSize: 13, fontWeight: 700, color: summary.totalPnl >= 0 ? 'var(--green)' : 'var(--red)' }}>
              Total: {summary.totalPnl >= 0 ? '+' : ''}${summary.totalPnl.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
            </span>
          </div>
        )}
      </div>

      {error && <p style={{ color: 'var(--red)', marginBottom: 12 }}>{error}</p>}

      {loading ? (
        <p style={{ color: 'var(--text-muted)' }}>Loading…</p>
      ) : rowData.length === 0 ? (
        <div style={{ color: 'var(--text-muted)', padding: 48, textAlign: 'center' }}>
          No realized P&L data yet. P&L is calculated from sell trades against a running average cost basis.
        </div>
      ) : (
        <div style={{ flex: 1 }} className={gridTheme}>
          <AgGridReact
            ref={gridRef}
            rowData={rowData}
            columnDefs={columnDefs}
            defaultColDef={defaultColDef}
            suppressMovableColumns={false}
          />
        </div>
      )}

      <div style={{ marginTop: 12, fontSize: 11, color: 'var(--text-muted)' }}>
        P&L = (Sell Price − Avg Cost Basis) × Qty − Fee. Cost basis is computed as a running weighted average of all buys per asset, in chronological order.
      </div>
    </div>
  );
}
