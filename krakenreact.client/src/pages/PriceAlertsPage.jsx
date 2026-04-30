import { useState, useEffect } from 'react';
import api from '../api/apiClient';

const EMPTY_FORM = {
  symbol: 'XBT/USD',
  targetPrice: '',
  direction: 'above',
  note: '',
  autoOrderEnabled: false,
  autoOrderSide: 'Buy',
  autoOrderQty: '',
  autoOrderOffsetPct: '0',
};

export default function PriceAlertsPage() {
  const [alerts, setAlerts] = useState([]);
  const [form, setForm] = useState(EMPTY_FORM);
  const [editId, setEditId] = useState(null);
  const [status, setStatus] = useState('');
  const [showForm, setShowForm] = useState(false);

  const load = () => {
    api.get('/pricealerts').then(r => setAlerts(r.data || [])).catch(() => {});
  };

  useEffect(() => { load(); }, []);

  const flash = (msg) => { setStatus(msg); setTimeout(() => setStatus(''), 3500); };

  const openCreate = () => {
    setEditId(null);
    setForm(EMPTY_FORM);
    setShowForm(true);
  };

  const openEdit = (a) => {
    setEditId(a.id);
    setForm({
      symbol: a.symbol,
      targetPrice: String(a.targetPrice),
      direction: a.direction,
      note: a.note || '',
      autoOrderEnabled: !!a.autoOrderEnabled,
      autoOrderSide: a.autoOrderSide || 'Buy',
      autoOrderQty: a.autoOrderQty > 0 ? String(a.autoOrderQty) : '',
      autoOrderOffsetPct: String(a.autoOrderOffsetPct ?? 0),
    });
    setShowForm(true);
  };

  const handleSubmit = (e) => {
    e.preventDefault();
    if (!form.symbol || !form.targetPrice) return;

    const body = {
      symbol: form.symbol.trim(),
      targetPrice: parseFloat(form.targetPrice),
      direction: form.direction,
      note: form.note,
      autoOrderEnabled: form.autoOrderEnabled,
      autoOrderSide: form.autoOrderSide,
      autoOrderQty: parseFloat(form.autoOrderQty) || 0,
      autoOrderOffsetPct: parseFloat(form.autoOrderOffsetPct) || 0,
    };

    const req = editId
      ? api.put(`/pricealerts/${editId}`, body)
      : api.post('/pricealerts', body);

    req
      .then(() => {
        load();
        setShowForm(false);
        setEditId(null);
        setForm(EMPTY_FORM);
        flash(editId ? 'Alert updated' : 'Alert created');
      })
      .catch(() => flash('Error saving alert'));
  };

  const handleDelete = (id) => {
    if (!confirm('Delete this alert?')) return;
    api.delete(`/pricealerts/${id}`).then(load).catch(() => {});
  };

  const handleReset = (id) => {
    api.post(`/pricealerts/${id}/reset`)
      .then(() => { load(); flash('Alert reset — will trigger again'); })
      .catch(() => flash('Error resetting alert'));
  };

  const fmt = (dt) => dt ? new Date(dt).toLocaleString() : '';

  const inp = {
    padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4,
    background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13,
  };

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 20 }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Price Alerts</h2>
        <button onClick={openCreate} style={{ padding: '7px 18px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13 }}>
          + New Alert
        </button>
      </div>

      {status && (
        <div style={{ marginBottom: 16, padding: '8px 14px', borderRadius: 4, background: status.includes('Error') ? 'rgba(239,68,68,0.12)' : 'rgba(34,197,94,0.12)', border: `1px solid ${status.includes('Error') ? 'var(--red)' : 'var(--green)'}`, color: status.includes('Error') ? 'var(--red)' : 'var(--green)', fontSize: 13 }}>
          {status}
        </div>
      )}

      {showForm && (
        <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 24 }}>
          <div style={{ fontWeight: 600, marginBottom: 16, color: 'var(--text-primary)' }}>
            {editId ? 'Edit Alert' : 'Create New Alert'}
          </div>
          <form onSubmit={handleSubmit}>
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginBottom: 12 }}>
              <div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Symbol</div>
                <input value={form.symbol} onChange={e => setForm(f => ({ ...f, symbol: e.target.value }))}
                  placeholder="XBT/USD" style={{ ...inp, width: 120 }} />
              </div>
              <div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Direction</div>
                <select value={form.direction} onChange={e => setForm(f => ({ ...f, direction: e.target.value }))} style={{ ...inp }}>
                  <option value="above">Rises above</option>
                  <option value="below">Falls below</option>
                </select>
              </div>
              <div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Target price ($)</div>
                <input type="number" step="any" value={form.targetPrice}
                  onChange={e => setForm(f => ({ ...f, targetPrice: e.target.value }))}
                  placeholder="e.g. 95000" style={{ ...inp, width: 130 }} required />
              </div>
              <div style={{ flex: '1 1 200px' }}>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Note (optional)</div>
                <input value={form.note} onChange={e => setForm(f => ({ ...f, note: e.target.value }))}
                  placeholder="e.g. target take-profit" style={{ ...inp, width: '100%', boxSizing: 'border-box' }} />
              </div>
            </div>

            <div style={{ borderTop: '1px solid var(--border)', paddingTop: 12, marginBottom: 12 }}>
              <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer', marginBottom: form.autoOrderEnabled ? 12 : 0 }}>
                <input type="checkbox" checked={form.autoOrderEnabled}
                  onChange={e => setForm(f => ({ ...f, autoOrderEnabled: e.target.checked }))} />
                <span style={{ fontSize: 13, color: 'var(--text-primary)', fontWeight: 500 }}>Auto-place limit order when triggered</span>
              </label>
              {form.autoOrderEnabled && (
                <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginLeft: 24 }}>
                  <div>
                    <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Side</div>
                    <select value={form.autoOrderSide} onChange={e => setForm(f => ({ ...f, autoOrderSide: e.target.value }))} style={{ ...inp }}>
                      <option value="Buy">Buy</option>
                      <option value="Sell">Sell</option>
                    </select>
                  </div>
                  <div>
                    <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Quantity</div>
                    <input type="number" step="any" min="0" value={form.autoOrderQty}
                      onChange={e => setForm(f => ({ ...f, autoOrderQty: e.target.value }))}
                      placeholder="e.g. 0.01" style={{ ...inp, width: 110 }} />
                  </div>
                  <div>
                    <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Price offset %</div>
                    <input type="number" step="0.1" value={form.autoOrderOffsetPct}
                      onChange={e => setForm(f => ({ ...f, autoOrderOffsetPct: e.target.value }))}
                      placeholder="0" style={{ ...inp, width: 90 }} />
                  </div>
                  <div style={{ fontSize: 12, color: 'var(--text-muted)', alignSelf: 'flex-end', paddingBottom: 8 }}>
                    Limit price = trigger price × (1 + offset%)
                  </div>
                </div>
              )}
            </div>

            <div style={{ display: 'flex', gap: 10 }}>
              <button type="submit" style={{ padding: '7px 18px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 }}>
                {editId ? 'Update Alert' : 'Add Alert'}
              </button>
              <button type="button" onClick={() => { setShowForm(false); setEditId(null); setForm(EMPTY_FORM); }}
                style={{ padding: '7px 14px', background: 'none', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', color: 'var(--text-muted)', fontSize: 13 }}>
                Cancel
              </button>
            </div>
          </form>
        </div>
      )}

      <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, overflow: 'hidden' }}>
        <div style={{ padding: '12px 16px', borderBottom: '1px solid var(--border)', fontWeight: 600, color: 'var(--text-primary)', fontSize: 14 }}>
          Alerts ({alerts.length})
        </div>
        {alerts.length === 0 ? (
          <div style={{ padding: 24, textAlign: 'center', color: 'var(--text-muted)', fontSize: 13 }}>No price alerts configured.</div>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ background: 'var(--bg-secondary)' }}>
                {['Symbol', 'Direction', 'Target', 'Status', 'Auto-Order', 'Note', 'Created', ''].map(h => (
                  <th key={h} style={{ padding: '8px 12px', textAlign: 'left', color: 'var(--text-muted)', fontWeight: 500, fontSize: 11 }}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {alerts.map(a => (
                <tr key={a.id} style={{ borderTop: '1px solid var(--border)', opacity: a.active ? 1 : 0.65 }}>
                  <td style={{ padding: '8px 12px', color: 'var(--text-primary)', fontWeight: 600 }}>{a.symbol}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)' }}>{a.direction === 'above' ? 'Rises above' : 'Falls below'}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--yellow)', fontWeight: 600 }}>${Number(a.targetPrice).toLocaleString()}</td>
                  <td style={{ padding: '8px 12px' }}>
                    {a.active
                      ? <span style={{ color: 'var(--green)', fontWeight: 600 }}>Active</span>
                      : <span style={{ color: 'var(--text-muted)' }}>Triggered {fmt(a.triggeredAt)}</span>}
                  </td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)', fontSize: 12 }}>
                    {a.autoOrderEnabled
                      ? <span style={{ color: 'var(--blue)' }}>{a.autoOrderSide} {a.autoOrderQty} {a.autoOrderOffsetPct !== 0 ? `(${a.autoOrderOffsetPct > 0 ? '+' : ''}{a.autoOrderOffsetPct}%)` : ''}</span>
                      : <span style={{ color: 'var(--text-muted)' }}>—</span>}
                  </td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)' }}>{a.note}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)', fontSize: 11 }}>{fmt(a.createdAt)}</td>
                  <td style={{ padding: '8px 12px', whiteSpace: 'nowrap' }}>
                    <button onClick={() => openEdit(a)} style={{ background: 'none', border: 'none', color: 'var(--blue)', cursor: 'pointer', fontSize: 13, padding: '0 8px 0 0' }}>Edit</button>
                    {!a.active && (
                      <button onClick={() => handleReset(a.id)} style={{ background: 'none', border: 'none', color: 'var(--green)', cursor: 'pointer', fontSize: 13, padding: '0 8px 0 0' }}>Reset</button>
                    )}
                    <button onClick={() => handleDelete(a.id)} style={{ background: 'none', border: 'none', color: 'var(--red)', cursor: 'pointer', fontSize: 13, padding: 0 }}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
