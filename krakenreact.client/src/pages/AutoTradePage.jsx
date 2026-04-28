import { useState, useEffect, useMemo, useCallback } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function AutoTradePage() {
  const [rowData, setRowData] = useState([]);
  const [expanded, setExpanded] = useState({});
  const [expandedL2, setExpandedL2] = useState({});
  const [expandedL3, setExpandedL3] = useState({});
  const { gridTheme } = useTheme();

  useEffect(() => {
    api.get('/autotrade').then(r => setRowData(r.data)).catch(console.error);

    const conn = getConnection();
    const handler = (data) => {
      setRowData(prev => {
        const idx = prev.findIndex(p => p.symbol === data.symbol);
        if (idx >= 0) { const u = [...prev]; u[idx] = data; return u; }
        return [...prev, data];
      });
    };
    conn.on('AutoTradeUpdate', handler);
    return () => conn.off('AutoTradeUpdate', handler);
  }, []);

  const toggleL1 = useCallback((key) => setExpanded(prev => ({ ...prev, [key]: !prev[key] })), []);
  const toggleL2 = useCallback((key) => setExpandedL2(prev => ({ ...prev, [key]: !prev[key] })), []);
  const toggleL3 = useCallback((key) => setExpandedL3(prev => ({ ...prev, [key]: !prev[key] })), []);

  const grouped = useMemo(() => {
    const byReason = {};
    for (const item of rowData) {
      const r = item.reason || 'Unknown';
      if (!byReason[r]) byReason[r] = [];
      byReason[r].push(item);
    }
    const result = {};
    for (const [reason, items] of Object.entries(byReason)) {
      const byCoinType = {};
      for (const item of items) {
        const ct = item.coinType || 'Unknown';
        if (!byCoinType[ct]) byCoinType[ct] = [];
        byCoinType[ct].push(item);
      }
      const l2 = {};
      for (const [coinType, ctItems] of Object.entries(byCoinType)) {
        const byCcy = {};
        for (const item of ctItems) {
          const ccy = item.ccy || 'USD';
          if (!byCcy[ccy]) byCcy[ccy] = [];
          byCcy[ccy].push(item);
        }
        l2[coinType] = byCcy;
      }
      result[reason] = l2;
    }
    return result;
  }, [rowData]);

  const columnDefs = useMemo(() => [
    { field: 'base', headerName: 'Symbol', minWidth: 100 },
    { field: 'orderRanking', headerName: 'Rank', minWidth: 80, sort: 'desc' },
    { field: 'orderWanted', headerName: 'Wanted', flex: 0, width: 80, cellRenderer: p => p.value ? '\u2713' : '' },
    { field: 'orderMade', headerName: 'Made', flex: 0, width: 70, cellRenderer: p => p.value ? '\u2713' : '' },
    { field: 'closePriceMovement', headerName: 'Day%', minWidth: 80, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { field: 'closePriceMovementWeek', headerName: 'Week%', minWidth: 80, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
    { field: 'closePriceMovementMonth', headerName: 'Month%', minWidth: 80, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '',
      cellStyle: p => p.value > 0 ? { color: 'var(--green)' } : p.value < 0 ? { color: 'var(--red)' } : {} },
  ], []);

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  const groupHeaderStyle = (level) => ({
    padding: `4px ${8 + level * 16}px`,
    cursor: 'pointer',
    userSelect: 'none',
    fontWeight: 600,
    fontSize: 13,
    color: level === 0 ? 'var(--group-l0-color)' : level === 1 ? 'var(--group-l1-color)' : 'var(--group-l2-color)',
    background: level === 0 ? 'var(--group-l0-bg)' : level === 1 ? 'var(--group-l1-bg)' : 'var(--group-l2-bg)',
    borderBottom: '1px solid var(--border)',
    display: 'flex',
    alignItems: 'center',
    gap: 8,
  });

  const arrow = (isOpen) => <span style={{ fontSize: 10, display: 'inline-block', transform: isOpen ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>{'\u25B6'}</span>;

  return (
    <div style={{ height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      {Object.entries(grouped).map(([reason, coinTypes]) => {
        const l1Key = reason;
        const l1Open = expanded[l1Key] !== false;
        const l1Count = Object.values(coinTypes).reduce((sum, byCcy) => sum + Object.values(byCcy).reduce((s, items) => s + items.length, 0), 0);
        return (
          <div key={l1Key}>
            <div style={groupHeaderStyle(0)} onClick={() => toggleL1(l1Key)}>
              {arrow(l1Open)} {reason} ({l1Count})
            </div>
            {l1Open && Object.entries(coinTypes).map(([coinType, byCcy]) => {
              const l2Key = `${reason}|${coinType}`;
              const l2Open = expandedL2[l2Key] !== false;
              const l2Count = Object.values(byCcy).reduce((s, items) => s + items.length, 0);
              return (
                <div key={l2Key}>
                  <div style={groupHeaderStyle(1)} onClick={() => toggleL2(l2Key)}>
                    {arrow(l2Open)} {coinType} ({l2Count})
                  </div>
                  {l2Open && Object.entries(byCcy).map(([ccy, items]) => {
                    const l3Key = `${reason}|${coinType}|${ccy}`;
                    const l3Open = expandedL3[l3Key] !== false;
                    return (
                      <div key={l3Key}>
                        <div style={groupHeaderStyle(2)} onClick={() => toggleL3(l3Key)}>
                          {arrow(l3Open)} {ccy} ({items.length})
                        </div>
                        {l3Open && (
                          <div style={{ height: Math.min(items.length * 30 + 32, 300) }}>
                            <AgGridReact
                              theme={gridTheme}
                              rowData={items}
                              columnDefs={columnDefs}
                              defaultColDef={defaultColDef}
                              headerHeight={28}
                              rowHeight={28}
                              getRowId={p => p.data.symbol}
                              domLayout={items.length <= 8 ? 'autoHeight' : 'normal'}
                            />
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              );
            })}
          </div>
        );
      })}
      {rowData.length === 0 && (
        <div style={{ color: 'var(--text-muted)', padding: 24, textAlign: 'center' }}>Loading auto-trade data...</div>
      )}
      <BacktestPanel />
    </div>
  );
}

function BacktestPanel() {
  const [symbol, setSymbol] = useState('XBT/USD');
  const [result, setResult] = useState(null);
  const [loading, setLoading] = useState(false);

  const run = () => {
    setLoading(true);
    setResult(null);
    api.get(`/backtest?symbol=${encodeURIComponent(symbol)}`)
      .then(r => setResult(r.data))
      .catch(() => setResult({ error: 'Failed to run backtest' }))
      .finally(() => setLoading(false));
  };

  const inp = { padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13 };

  return (
    <div style={{ borderTop: '2px solid var(--border)', margin: '16px 16px 0', padding: '16px 0 24px' }}>
      <div style={{ fontWeight: 700, fontSize: 15, color: 'var(--text-primary)', marginBottom: 12 }}>Rule Backtest</div>
      <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap', marginBottom: 14 }}>
        <input value={symbol} onChange={e => setSymbol(e.target.value)} placeholder="XBT/USD" style={{ ...inp, width: 130 }} />
        <button onClick={run} disabled={loading} style={{ padding: '6px 16px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 }}>
          {loading ? 'Running…' : 'Run Backtest'}
        </button>
        <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>Simulates the buy-low/sell-high rule on daily klines. Starts with $10,000 hypothetical capital.</span>
      </div>
      {result?.error && <div style={{ color: 'var(--red)', fontSize: 13 }}>{result.error}</div>}
      {result?.summary && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div style={{ display: 'flex', gap: 20, flexWrap: 'wrap' }}>
            {[
              ['Trades', result.summary.tradeCount],
              ['Win rate', result.summary.winRate + '%'],
              ['Total P/L', (result.summary.totalPlPct >= 0 ? '+' : '') + result.summary.totalPlPct + '%'],
              ['Final value', '$' + Number(result.summary.finalCash).toLocaleString(undefined, { maximumFractionDigits: 0 })],
            ].map(([label, val]) => (
              <div key={label} style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 6, padding: '8px 16px', minWidth: 100 }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 2 }}>{label}</div>
                <div style={{ fontSize: 16, fontWeight: 700, color: String(val).startsWith('-') ? 'var(--red)' : String(val).startsWith('+') ? 'var(--green)' : 'var(--text-primary)' }}>{val}</div>
              </div>
            ))}
          </div>
          {result.trades?.length > 0 && (
            <div style={{ maxHeight: 220, overflowY: 'auto', border: '1px solid var(--border)', borderRadius: 6 }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
                <thead>
                  <tr style={{ background: 'var(--bg-secondary)' }}>
                    {['Entry date', 'Entry $', 'Exit date', 'Exit $', 'P/L%'].map(h => (
                      <th key={h} style={{ padding: '6px 10px', textAlign: 'left', color: 'var(--text-muted)', fontWeight: 500, fontSize: 11 }}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {result.trades.map((t, i) => (
                    <tr key={i} style={{ borderTop: '1px solid var(--border)' }}>
                      <td style={{ padding: '5px 10px', color: 'var(--text-muted)' }}>{new Date(t.entryDate).toLocaleDateString()}</td>
                      <td style={{ padding: '5px 10px', color: 'var(--text-primary)' }}>${Number(t.entryPrice).toLocaleString()}</td>
                      <td style={{ padding: '5px 10px', color: 'var(--text-muted)' }}>{new Date(t.exitDate).toLocaleDateString()}</td>
                      <td style={{ padding: '5px 10px', color: 'var(--text-primary)' }}>${Number(t.exitPrice).toLocaleString()}</td>
                      <td style={{ padding: '5px 10px', fontWeight: 600, color: t.plPct >= 0 ? 'var(--green)' : 'var(--red)' }}>
                        {t.plPct >= 0 ? '+' : ''}{Number(t.plPct).toFixed(2)}%
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {result.summary.message && <div style={{ fontSize: 13, color: 'var(--text-muted)' }}>{result.summary.message}</div>}
        </div>
      )}
    </div>
  );
}
