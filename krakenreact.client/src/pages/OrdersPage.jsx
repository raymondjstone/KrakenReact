import { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { formatPrice, colorForValue } from '../utils/formatters';
import OrderDialog from '../components/OrderDialog';
import { useTheme } from '../context/ThemeContext';

ModuleRegistry.registerModules([AllCommunityModule]);

const OPEN_STATUSES = new Set(['open', 'new', 'partially_filled', 'pending_new']);
const isOpenOrder = (status) => OPEN_STATUSES.has((status || '').toLowerCase());

export default function OrdersPage() {
  const [rowData, setRowData] = useState([]);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editOrder, setEditOrder] = useState(null);
  const [symbols, setSymbols] = useState([]);
  const [expanded, setExpanded] = useState({});
  const [ladderOpen, setLadderOpen] = useState(false);
  const gridRefs = useRef({});
  const { gridTheme } = useTheme();

  const loadOrders = useCallback(() => {
    api.get('/orders').then(r => setRowData(r.data)).catch(console.error);
  }, []);

  useEffect(() => {
    loadOrders();
    api.get('/symbols').then(r => setSymbols(r.data.map(s => s.websocketName))).catch(console.error);
    const conn = getConnection();
    conn.on('ExecutionUpdate', loadOrders);
    const orderHandler = (data) => setRowData(data);
    conn.on('OrderUpdate', orderHandler);
    return () => {
      conn.off('ExecutionUpdate', loadOrders);
      conn.off('OrderUpdate', orderHandler);
    };
  }, [loadOrders]);

  const handleCancel = useCallback(async (order) => {
    if (!confirm(`Cancel ${order.side} ${order.symbol} @${order.price} x${order.quantity}?`)) return;
    try { await api.delete(`/orders/${order.id}`); loadOrders(); } catch { alert('Failed to cancel'); }
  }, [loadOrders]);

  const toggleGroup = useCallback((key) => setExpanded(prev => ({ ...prev, [key]: !prev[key] })), []);

  const columnDefs = useMemo(() => [
    { headerName: 'Edit', flex: 0, width: 70, cellRenderer: p => {
      if (!p.data || !isOpenOrder(p.data.status)) return null;
      return <button onClick={() => { setEditOrder(p.data); setDialogOpen(true); }} style={{ padding: '2px 8px', fontSize: 11, cursor: 'pointer' }}>Edit</button>;
    }},
    { headerName: 'Cancel', flex: 0, width: 85, cellRenderer: p => {
      if (!p.data || !isOpenOrder(p.data.status)) return null;
      return <button onClick={() => handleCancel(p.data)} style={{ padding: '2px 8px', fontSize: 11, cursor: 'pointer', color: 'var(--red)' }}>Cancel</button>;
    }},
    { field: 'symbol', headerName: 'Symbol', minWidth: 100 },
    { field: 'side', headerName: 'Side', flex: 0, width: 60, cellStyle: p => ({ color: (p.value || '').trim().toLowerCase() === 'buy' ? 'var(--green)' : 'var(--red)' }) },
    { field: 'type', headerName: 'Type', flex: 0, width: 70 },
    { field: 'price', headerName: 'Price', minWidth: 110, valueFormatter: p => formatPrice(p.value) },
    { field: 'quantity', headerName: 'Qty', minWidth: 90, valueFormatter: p => p.value != null ? Number(p.value).toFixed(4) : '' },
    { field: 'quantityFilled', headerName: 'Filled', minWidth: 90, valueFormatter: p => p.value != null ? Number(p.value).toFixed(4) : '' },
    { field: 'orderValue', headerName: 'Value', minWidth: 100, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) : '' },
    { field: 'fee', headerName: 'Fee', minWidth: 80, valueFormatter: p => p.value != null ? Number(p.value).toFixed(4) : '' },
    { field: 'averagePrice', headerName: 'AvgPrice', minWidth: 100, valueFormatter: p => formatPrice(p.value) },
    { field: 'latestPrice', headerName: 'Latest', minWidth: 110, valueFormatter: p => formatPrice(p.value) },
    { field: 'distance', headerName: 'Distance', minWidth: 100, valueFormatter: p => formatPrice(p.value), cellStyle: p => ({ color: colorForValue(p.value) }) },
    { field: 'distancePercentage', headerName: 'Dist%', minWidth: 80, valueFormatter: p => p.value != null ? Number(p.value).toFixed(2) + '%' : '', cellStyle: p => ({ color: colorForValue(p.value) }) },
    { field: 'createTime', headerName: 'Created', minWidth: 160, sort: 'desc', valueFormatter: p => p.value ? new Date(p.value).toLocaleString() : '' },
    { field: 'closeTime', headerName: 'Closed', minWidth: 160, valueFormatter: p => p.value ? new Date(p.value).toLocaleString() : '' },
    { field: 'leverage', headerName: 'Leverage', minWidth: 80 },
    { field: 'reason', headerName: 'Reason', minWidth: 120 },
  ], [handleCancel]);

  const closedColumnDefs = useMemo(() =>
    columnDefs.map(col => {
      if (col.field === 'createTime') return { ...col, sort: undefined };
      if (col.field === 'closeTime') return { ...col, sort: 'desc' };
      return col;
    }),
    [columnDefs]
  );

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  const grouped = useMemo(() => {
    const byStatus = {};
    for (const item of rowData) {
      const s = (item.status || '').toLowerCase();
      if (s.includes('canceled')) continue;
      const label = isOpenOrder(item.status) ? 'Open' : (item.status || 'Unknown');
      if (!byStatus[label]) byStatus[label] = [];
      byStatus[label].push(item);
    }
    const sorted = {};
    if (byStatus['Open']) { sorted['Open'] = byStatus['Open']; delete byStatus['Open']; }
    for (const [k, v] of Object.entries(byStatus)) sorted[k] = v;
    return sorted;
  }, [rowData]);

  const groupHeaderStyle = {
    padding: '4px 8px',
    cursor: 'pointer',
    userSelect: 'none',
    fontWeight: 600,
    fontSize: 13,
    color: 'var(--group-l0-color)',
    background: 'var(--group-l0-bg)',
    borderBottom: '1px solid var(--border)',
    display: 'flex',
    alignItems: 'center',
    gap: 8,
  };

  const arrow = (isOpen) => (
    <span style={{ fontSize: 10, display: 'inline-block', transform: isOpen ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }}>{'\u25B6'}</span>
  );

  return (
    <div style={{ height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ padding: '8px 12px', borderBottom: '1px solid var(--border)', display: 'flex', gap: 8 }}>
        <button
          onClick={() => { setEditOrder(null); setDialogOpen(true); }}
          style={{ padding: '4px 12px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 12 }}>
          + New Order
        </button>
        <button
          onClick={() => setLadderOpen(true)}
          style={{ padding: '4px 12px', background: 'transparent', color: 'var(--text-primary)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 12 }}>
          Order Ladder
        </button>
        <button
          onClick={() => {
            const allRefs = Object.values(gridRefs.current);
            if (allRefs.length > 0) allRefs[0]?.api?.exportDataAsCsv({ fileName: 'orders.csv', allColumns: true });
          }}
          style={{ padding: '4px 12px', background: 'transparent', color: 'var(--text-primary)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 12 }}>
          Export CSV
        </button>
      </div>
      {Object.entries(grouped).map(([status, items]) => {
        const isOpen = expanded[status] !== false;
        return (
          <div key={status}>
            <div style={groupHeaderStyle} onClick={() => toggleGroup(status)}>
              {arrow(isOpen)} {status} ({items.length})
            </div>
            {isOpen && (
              <div style={{ height: Math.min(items.length * 30 + 34, 500) }}>
                <AgGridReact
                  ref={el => { gridRefs.current[status] = el; }}
                  theme={gridTheme}
                  rowData={items}
                  columnDefs={status === 'Open' ? columnDefs : closedColumnDefs}
                  defaultColDef={defaultColDef}
                  headerHeight={28}
                  rowHeight={28}
                  getRowId={p => p.data.id}
                  domLayout={items.length <= 15 ? 'autoHeight' : 'normal'}
                />
              </div>
            )}
          </div>
        );
      })}
      {rowData.length === 0 && (
        <div style={{ color: 'var(--text-muted)', padding: 24, textAlign: 'center' }}>Loading orders...</div>
      )}
      <OrderDialog isOpen={dialogOpen} onClose={(ok) => { setDialogOpen(false); setEditOrder(null); if (ok) loadOrders(); }}
        editOrder={editOrder} symbols={symbols} />
      {ladderOpen && <OrderLadderDialog symbols={symbols} onClose={() => { setLadderOpen(false); loadOrders(); }} />}
    </div>
  );
}

function OrderLadderDialog({ symbols, onClose }) {
  const [symbol, setSymbol] = useState('');
  const [side, setSide] = useState('Buy');
  const [totalQty, setTotalQty] = useState('');
  const [startPrice, setStartPrice] = useState('');
  const [endPrice, setEndPrice] = useState('');
  const [count, setCount] = useState(5);
  const [status, setStatus] = useState('');
  const [placing, setPlacing] = useState(false);

  const handlePlace = async () => {
    if (!symbol || !totalQty || !startPrice || !endPrice) return setStatus('All fields required');
    setPlacing(true);
    try {
      const r = await api.post('/orders/ladder', {
        symbol, side,
        totalQty: parseFloat(totalQty),
        startPrice: parseFloat(startPrice),
        endPrice: parseFloat(endPrice),
        count,
      });
      setStatus(r.data.message || 'Placed');
      setTimeout(onClose, 2000);
    } catch (err) {
      setStatus(err.response?.data?.error || 'Error placing ladder');
    } finally {
      setPlacing(false);
    }
  };

  const inputStyle = { padding: '7px 10px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13, width: '100%' };

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000 }} onClick={onClose}>
      <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 10, padding: 24, width: 380, maxWidth: '95vw' }} onClick={e => e.stopPropagation()}>
        <div style={{ fontWeight: 700, fontSize: 17, marginBottom: 16, color: 'var(--text-primary)' }}>Order Ladder</div>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10, marginBottom: 14 }}>
          <div>
            <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>Symbol</div>
            <input list="ladder-symbols" value={symbol} onChange={e => setSymbol(e.target.value)} style={inputStyle} placeholder="XBT/USD" />
            <datalist id="ladder-symbols">{symbols.map(s => <option key={s} value={s} />)}</datalist>
          </div>
          <div>
            <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>Side</div>
            <select value={side} onChange={e => setSide(e.target.value)} style={inputStyle}>
              <option value="Buy">Buy</option>
              <option value="Sell">Sell</option>
            </select>
          </div>
          <div>
            <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>Start Price</div>
            <input type="number" value={startPrice} onChange={e => setStartPrice(e.target.value)} style={inputStyle} placeholder="lowest" />
          </div>
          <div>
            <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>End Price</div>
            <input type="number" value={endPrice} onChange={e => setEndPrice(e.target.value)} style={inputStyle} placeholder="highest" />
          </div>
          <div>
            <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>Total Qty</div>
            <input type="number" value={totalQty} onChange={e => setTotalQty(e.target.value)} style={inputStyle} placeholder="e.g. 0.1" />
          </div>
          <div>
            <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>Order count (2–20)</div>
            <input type="number" min={2} max={20} value={count} onChange={e => setCount(Math.min(20, Math.max(2, parseInt(e.target.value) || 5)))} style={inputStyle} />
          </div>
        </div>
        {startPrice && endPrice && count >= 2 && (
          <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 14 }}>
            {count} orders of {totalQty ? (parseFloat(totalQty) / count).toFixed(6) : '?'} each, from {parseFloat(startPrice).toFixed(2)} to {parseFloat(endPrice).toFixed(2)} (step {((parseFloat(endPrice) - parseFloat(startPrice)) / (count - 1)).toFixed(2)})
          </div>
        )}
        {status && <div style={{ fontSize: 13, marginBottom: 10, color: status.includes('Error') || status.includes('required') ? 'var(--red)' : 'var(--green)' }}>{status}</div>}
        <div style={{ display: 'flex', gap: 8 }}>
          <button onClick={handlePlace} disabled={placing} style={{ flex: 1, padding: '8px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 }}>
            {placing ? 'Placing…' : `Place ${count} Orders`}
          </button>
          <button onClick={onClose} style={{ padding: '8px 16px', background: 'var(--bg-primary)', color: 'var(--text-muted)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer' }}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
