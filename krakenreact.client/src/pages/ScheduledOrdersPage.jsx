import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

const EMPTY_FORM = {
  symbol: '', side: 'Buy', price: '', quantity: '', scheduledAt: '', note: '',
};

const STATUS_COLORS = {
  Pending: '#f59e0b',
  Executed: 'var(--green)',
  Failed: 'var(--red)',
  Cancelled: 'var(--text-muted)',
};

function toLocalDatetimeInput(utcIso) {
  if (!utcIso) return '';
  const d = new Date(utcIso);
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function toUtcIso(localDatetime) {
  if (!localDatetime) return '';
  return new Date(localDatetime).toISOString();
}

export default function ScheduledOrdersPage() {
  const [orders, setOrders] = useState([]);
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState(null);
  const [saving, setSaving] = useState(false);
  const [statusMsg, setStatusMsg] = useState('');

  const flash = (msg) => {
    setStatusMsg(msg);
    setTimeout(() => setStatusMsg(''), 4000);
  };

  const load = useCallback(() => {
    api.get('/scheduledorders')
      .then(r => { setOrders(r.data || []); setLoading(false); })
      .catch(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleSave = async () => {
    if (!form.symbol.trim()) return flash('Symbol is required');
    if (!form.price || Number(form.price) <= 0) return flash('Price must be positive');
    if (!form.quantity || Number(form.quantity) <= 0) return flash('Quantity must be positive');
    if (!form.scheduledAt) return flash('Scheduled time is required');
    const scheduledUtc = toUtcIso(form.scheduledAt);
    if (new Date(scheduledUtc) <= new Date() && !form.id) return flash('Scheduled time must be in the future');

    setSaving(true);
    try {
      const payload = {
        symbol: form.symbol.trim(),
        side: form.side,
        price: Number(form.price),
        quantity: Number(form.quantity),
        scheduledAt: scheduledUtc,
        note: form.note || '',
      };
      if (form.id) {
        await api.put(`/scheduledorders/${form.id}`, payload);
      } else {
        await api.post('/scheduledorders', payload);
      }
      load();
      setForm(null);
      flash('Saved');
    } catch (err) {
      flash(err.response?.data?.message || 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const handleCancel = async (id) => {
    if (!window.confirm('Cancel this scheduled order?')) return;
    try { await api.post(`/scheduledorders/${id}/cancel`); load(); flash('Cancelled'); }
    catch { flash('Cancel failed'); }
  };

  const handleDelete = async (id) => {
    if (!window.confirm('Delete this order?')) return;
    try { await api.delete(`/scheduledorders/${id}`); load(); flash('Deleted'); }
    catch { flash('Delete failed'); }
  };

  const inputStyle = { padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13, width: '100%', boxSizing: 'border-box' };

  const pending = orders.filter(o => o.status === 'Pending');
  const history = orders.filter(o => o.status !== 'Pending');

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 20 }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Scheduled Orders</h2>
        <button
          onClick={() => setForm({ ...EMPTY_FORM })}
          style={{ padding: '6px 16px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13 }}
        >
          + New Order
        </button>
        {statusMsg && (
          <span style={{ fontSize: 13, color: statusMsg.includes('failed') || statusMsg.includes('required') || statusMsg.includes('future') ? 'var(--red)' : 'var(--green)' }}>
            {statusMsg}
          </span>
        )}
      </div>

      {form && (
        <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 24 }}>
          <div style={{ fontWeight: 600, marginBottom: 16, color: 'var(--text-primary)' }}>{form.id ? 'Edit Order' : 'New Scheduled Order'}</div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 12, marginBottom: 12 }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Symbol</div>
              <input value={form.symbol} onChange={e => setForm(f => ({ ...f, symbol: e.target.value }))} style={inputStyle} placeholder="XBT/USD" disabled={!!form.id} />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Side</div>
              <div style={{ display: 'flex', gap: 6 }}>
                {['Buy', 'Sell'].map(s => (
                  <button key={s} onClick={() => setForm(f => ({ ...f, side: s }))} disabled={!!form.id} style={{
                    flex: 1, padding: '6px 0', border: 'none', borderRadius: 4, cursor: form.id ? 'default' : 'pointer', fontWeight: 600, fontSize: 13,
                    background: form.side === s ? (s === 'Buy' ? 'var(--green)' : 'var(--red)') : 'var(--bg-primary)',
                    color: form.side === s ? 'white' : 'var(--text-muted)',
                  }}>{s}</button>
                ))}
              </div>
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Scheduled At (local time)</div>
              <input type="datetime-local" value={form.scheduledAt} onChange={e => setForm(f => ({ ...f, scheduledAt: e.target.value }))} style={inputStyle} />
            </div>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 12, marginBottom: 12 }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Limit Price</div>
              <input type="number" min={0} step="any" value={form.price} onChange={e => setForm(f => ({ ...f, price: e.target.value }))} style={inputStyle} placeholder="0.00" />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Quantity</div>
              <input type="number" min={0} step="any" value={form.quantity} onChange={e => setForm(f => ({ ...f, quantity: e.target.value }))} style={inputStyle} placeholder="0.00" />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Note (optional)</div>
              <input value={form.note} onChange={e => setForm(f => ({ ...f, note: e.target.value }))} style={inputStyle} placeholder="Optional note" />
            </div>
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={handleSave} disabled={saving} style={{ padding: '6px 16px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13 }}>
              {saving ? 'Saving…' : 'Save'}
            </button>
            <button onClick={() => setForm(null)} style={{ padding: '6px 14px', background: 'var(--bg-primary)', color: 'var(--text-muted)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 13 }}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {loading && <p style={{ color: 'var(--text-muted)' }}>Loading…</p>}

      {!loading && (
        <>
          <div style={{ fontWeight: 600, fontSize: 14, color: 'var(--text-primary)', marginBottom: 10 }}>
            Pending ({pending.length})
          </div>
          {pending.length === 0 ? (
            <div style={{ color: 'var(--text-muted)', marginBottom: 24, fontSize: 13 }}>No pending orders.</div>
          ) : (
            pending.map(o => <OrderRow key={o.id} order={o} onEdit={() => setForm({ ...o, scheduledAt: toLocalDatetimeInput(o.scheduledAt) })} onCancel={() => handleCancel(o.id)} onDelete={() => handleDelete(o.id)} />)
          )}

          {history.length > 0 && (
            <>
              <div style={{ fontWeight: 600, fontSize: 14, color: 'var(--text-primary)', margin: '20px 0 10px' }}>
                History ({history.length})
              </div>
              {history.map(o => <OrderRow key={o.id} order={o} onDelete={() => handleDelete(o.id)} />)}
            </>
          )}
        </>
      )}

      <div style={{ marginTop: 24, padding: 16, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.6 }}>
        <strong style={{ color: 'var(--text-primary)' }}>How it works</strong><br />
        Scheduled orders are checked every minute. When the scheduled time arrives, a limit order is placed at the specified price.
        All times are interpreted as your browser's local timezone. Use the Hangfire dashboard to monitor execution.
      </div>
    </div>
  );
}

function OrderRow({ order, onEdit, onCancel, onDelete }) {
  const STATUS_COLORS = { Pending: '#f59e0b', Executed: 'var(--green)', Failed: 'var(--red)', Cancelled: 'var(--text-muted)' };
  return (
    <div style={{
      background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8,
      padding: '12px 16px', marginBottom: 8, display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap',
    }}>
      <div style={{ minWidth: 90 }}>
        <span style={{ fontWeight: 700, fontSize: 15, color: 'var(--text-primary)' }}>{order.symbol}</span>
        <span style={{ marginLeft: 8, fontSize: 12, fontWeight: 600, color: order.side === 'Buy' ? 'var(--green)' : 'var(--red)' }}>{order.side}</span>
      </div>
      <div style={{ fontSize: 13, color: 'var(--text-primary)' }}>
        {Number(order.quantity).toLocaleString(undefined, { maximumFractionDigits: 8 })} @ ${Number(order.price).toLocaleString(undefined, { maximumFractionDigits: 8 })}
      </div>
      <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
        {new Date(order.scheduledAt).toLocaleString()}
      </div>
      <div style={{ flex: 1, display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap' }}>
        <span style={{ fontSize: 12, fontWeight: 600, color: STATUS_COLORS[order.status] || 'var(--text-muted)' }}>{order.status}</span>
        {order.note && <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>{order.note}</span>}
        {order.errorMessage && <span style={{ fontSize: 11, color: 'var(--red)' }}>{order.errorMessage}</span>}
        {order.executedAt && <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>Executed: {new Date(order.executedAt).toLocaleString()}</span>}
      </div>
      <div style={{ display: 'flex', gap: 6 }}>
        {order.status === 'Pending' && onEdit && (
          <button onClick={onEdit} style={{ padding: '3px 10px', fontSize: 12, border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', background: 'var(--bg-primary)', color: 'var(--text-primary)' }}>Edit</button>
        )}
        {order.status === 'Pending' && onCancel && (
          <button onClick={onCancel} style={{ padding: '3px 10px', fontSize: 12, border: '1px solid #f59e0b', borderRadius: 4, cursor: 'pointer', background: 'transparent', color: '#f59e0b' }}>Cancel</button>
        )}
        <button onClick={onDelete} style={{ padding: '3px 10px', fontSize: 12, border: '1px solid var(--red, #ef4444)', borderRadius: 4, cursor: 'pointer', background: 'transparent', color: 'var(--red, #ef4444)' }}>Delete</button>
      </div>
    </div>
  );
}
