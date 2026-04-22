import { useState, useEffect, useMemo, useCallback } from 'react';
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
    </div>
  );
}
