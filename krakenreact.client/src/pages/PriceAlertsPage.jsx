import { useState, useEffect } from 'react';
import api from '../api/apiClient';

export default function PriceAlertsPage() {
  const [alerts, setAlerts] = useState([]);
  const [symbol, setSymbol] = useState('XBT/USD');
  const [targetPrice, setTargetPrice] = useState('');
  const [direction, setDirection] = useState('above');
  const [note, setNote] = useState('');
  const [status, setStatus] = useState('');

  const load = () => {
    api.get('/pricealerts').then(r => setAlerts(r.data || [])).catch(() => {});
  };

  useEffect(() => { load(); }, []);

  const handleCreate = (e) => {
    e.preventDefault();
    if (!symbol || !targetPrice) return;
    api.post('/pricealerts', { symbol, targetPrice: parseFloat(targetPrice), direction, note })
      .then(() => { load(); setTargetPrice(''); setNote(''); setStatus('Alert created'); setTimeout(() => setStatus(''), 3000); })
      .catch(() => setStatus('Error creating alert'));
  };

  const handleDelete = (id) => {
    api.delete(`/pricealerts/${id}`).then(load).catch(() => {});
  };

  const fmt = (dt) => dt ? new Date(dt).toLocaleString() : '';
  const inp = { padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13 };

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <h2 style={{ margin: '0 0 20px', color: 'var(--text-primary)' }}>Price Alerts</h2>

      <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 24 }}>
        <div style={{ fontWeight: 600, marginBottom: 14, color: 'var(--text-primary)' }}>Create new alert</div>
        <form onSubmit={handleCreate} style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <div>
            <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Symbol</div>
            <input value={symbol} onChange={e => setSymbol(e.target.value)} placeholder="XBT/USD" style={{ ...inp, width: 120 }} />
          </div>
          <div>
            <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Direction</div>
            <select value={direction} onChange={e => setDirection(e.target.value)} style={{ ...inp }}>
              <option value="above">Rises above</option>
              <option value="below">Falls below</option>
            </select>
          </div>
          <div>
            <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Target price ($)</div>
            <input type="number" step="any" value={targetPrice} onChange={e => setTargetPrice(e.target.value)} placeholder="e.g. 95000" style={{ ...inp, width: 130 }} required />
          </div>
          <div style={{ flex: '1 1 200px' }}>
            <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Note (optional)</div>
            <input value={note} onChange={e => setNote(e.target.value)} placeholder="e.g. target take-profit" style={{ ...inp, width: '100%', boxSizing: 'border-box' }} />
          </div>
          <button type="submit" style={{ padding: '7px 18px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 }}>
            Add Alert
          </button>
          {status && <span style={{ fontSize: 13, color: status.includes('Error') ? 'var(--red)' : 'var(--green)' }}>{status}</span>}
        </form>
      </div>

      <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, overflow: 'hidden' }}>
        <div style={{ padding: '12px 16px', borderBottom: '1px solid var(--border)', fontWeight: 600, color: 'var(--text-primary)', fontSize: 14 }}>
          Active & triggered alerts ({alerts.length})
        </div>
        {alerts.length === 0 ? (
          <div style={{ padding: 24, textAlign: 'center', color: 'var(--text-muted)', fontSize: 13 }}>No price alerts configured.</div>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ background: 'var(--bg-secondary)' }}>
                {['Symbol', 'Direction', 'Target', 'Status', 'Note', 'Created', ''].map(h => (
                  <th key={h} style={{ padding: '8px 12px', textAlign: 'left', color: 'var(--text-muted)', fontWeight: 500, fontSize: 11 }}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {alerts.map(a => (
                <tr key={a.id} style={{ borderTop: '1px solid var(--border)', opacity: a.active ? 1 : 0.6 }}>
                  <td style={{ padding: '8px 12px', color: 'var(--text-primary)', fontWeight: 600 }}>{a.symbol}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)' }}>{a.direction === 'above' ? 'Rises above' : 'Falls below'}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--yellow)', fontWeight: 600 }}>${Number(a.targetPrice).toLocaleString()}</td>
                  <td style={{ padding: '8px 12px' }}>
                    {a.active
                      ? <span style={{ color: 'var(--green)', fontWeight: 600 }}>Active</span>
                      : <span style={{ color: 'var(--text-muted)' }}>Triggered {fmt(a.triggeredAt)}</span>}
                  </td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)' }}>{a.note}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)', fontSize: 11 }}>{fmt(a.createdAt)}</td>
                  <td style={{ padding: '8px 12px' }}>
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
