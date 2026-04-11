import { useState, useMemo, useCallback } from 'react';
import { AgGridReact } from 'ag-grid-react';
import api from '../api/apiClient';
import { formatPrice, colorForValue } from '../utils/formatters';
import OrderDialog from './OrderDialog';
import { useTheme } from '../context/ThemeContext';

const OPEN_STATUSES = new Set(['open', 'new', 'partially_filled', 'pending_new']);
const isOpenOrder = (status) => OPEN_STATUSES.has((status || '').toLowerCase());

export default function OpenOrdersGrid({ orders, symbols, onOrderChanged, onSymbolClick, headerHeight = 28, rowHeight = 28 }) {
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editOrder, setEditOrder] = useState(null);
  const { gridTheme } = useTheme();

  const openOrders = useMemo(() =>
    (orders || []).filter(o => isOpenOrder(o.status)),
    [orders]
  );

  const handleCancel = useCallback(async (order) => {
    if (!confirm(`Cancel ${order.side} ${order.symbol} @${order.price} x${order.quantity}?`)) return;
    try {
      await api.delete(`/orders/${order.id}`);
      if (onOrderChanged) onOrderChanged();
    } catch {
      alert('Failed to cancel');
    }
  }, [onOrderChanged]);

  const columnDefs = useMemo(() => [
    { headerName: 'Edit', flex: 0, width: 70, cellRenderer: p => {
      if (!p.data || !isOpenOrder(p.data.status)) return null;
      return <button onClick={() => { setEditOrder(p.data); setDialogOpen(true); }} style={{ padding: '2px 8px', fontSize: 11, cursor: 'pointer' }}>Edit</button>;
    }},
    { headerName: 'Cancel', flex: 0, width: 80, cellRenderer: p => {
      if (!p.data || !isOpenOrder(p.data.status)) return null;
      return <button onClick={() => handleCancel(p.data)} style={{ padding: '2px 8px', fontSize: 11, cursor: 'pointer', color: 'var(--red)' }}>Cancel</button>;
    }},
    { field: 'symbol', headerName: 'Symbol', minWidth: 100, cellRenderer: p => {
      if (!p.value || !onSymbolClick) return p.value;
      return <span style={{ cursor: 'pointer', color: 'var(--yellow)', textDecoration: 'underline' }} onClick={() => onSymbolClick(p.value)}>{p.value}</span>;
    }},
    { field: 'side', headerName: 'Side', flex: 0, width: 60, cellStyle: p => ({ color: p.value === 'Buy' ? 'var(--green)' : 'var(--red)' }) },
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
    { field: 'createTime', headerName: 'Created', minWidth: 160, valueFormatter: p => p.value ? new Date(p.value).toLocaleString() : '' },
    { field: 'leverage', headerName: 'Leverage', minWidth: 80 },
  ], [handleCancel, onSymbolClick]);

  const defaultColDef = useMemo(() => ({ sortable: true, filter: true, resizable: true, flex: 1 }), []);

  return (
    <>
      <AgGridReact
        theme={gridTheme}
        rowData={openOrders}
        columnDefs={columnDefs}
        defaultColDef={defaultColDef}
        headerHeight={headerHeight}
        rowHeight={rowHeight}
        getRowId={p => p.data.id}
        suppressCellFocus
      />
      <OrderDialog
        isOpen={dialogOpen}
        onClose={(ok) => { setDialogOpen(false); setEditOrder(null); if (ok && onOrderChanged) onOrderChanged(); }}
        editOrder={editOrder}
        symbols={symbols}
      />
    </>
  );
}
