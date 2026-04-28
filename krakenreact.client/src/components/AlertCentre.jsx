import { useState, useEffect, useRef } from 'react';
import api from '../api/apiClient';

export default function AlertCentre() {
  const [open, setOpen] = useState(false);
  const [alerts, setAlerts] = useState([]);
  const [unread, setUnread] = useState(0);
  const panelRef = useRef(null);

  const load = () => {
    api.get('/alerts?limit=50')
      .then(r => {
        setAlerts(r.data.alerts || []);
        setUnread(r.data.total || 0);
      })
      .catch(() => {});
  };

  useEffect(() => {
    load();
    const t = setInterval(load, 30000);
    return () => clearInterval(t);
  }, []);

  useEffect(() => {
    if (!open) return;
    const handler = (e) => {
      if (panelRef.current && !panelRef.current.contains(e.target)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  const dismiss = (id) => {
    api.delete(`/alerts/${id}`).then(load).catch(() => {});
  };

  const clearAll = () => {
    api.delete('/alerts').then(() => { setAlerts([]); setUnread(0); }).catch(() => {});
  };

  const fmt = (dt) => {
    const d = new Date(dt);
    return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  };

  const typeColor = (type) =>
    type === 'error' ? 'var(--red)' : type === 'warning' ? 'var(--yellow)' : 'var(--text-muted)';

  return (
    <div style={{ position: 'relative' }}>
      <button
        onClick={() => { setOpen(v => !v); if (!open) load(); }}
        style={{ background: 'none', border: 'none', cursor: 'pointer', fontSize: 18, position: 'relative', color: 'var(--text-secondary)', lineHeight: 1, padding: '2px 4px' }}
        title="Notification centre"
      >
        🔔
        {unread > 0 && (
          <span style={{ position: 'absolute', top: -2, right: -2, background: 'var(--red)', color: 'white', borderRadius: '50%', fontSize: 9, minWidth: 14, height: 14, display: 'flex', alignItems: 'center', justifyContent: 'center', fontWeight: 700, lineHeight: 1 }}>
            {unread > 99 ? '99+' : unread}
          </span>
        )}
      </button>

      {open && (
        <div
          ref={panelRef}
          style={{
            position: 'absolute', top: '110%', right: 0, width: 360, maxHeight: 480,
            background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8,
            boxShadow: '0 8px 24px rgba(0,0,0,0.25)', zIndex: 9999, overflow: 'hidden',
            display: 'flex', flexDirection: 'column',
          }}
        >
          <div style={{ padding: '10px 14px', borderBottom: '1px solid var(--border)', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <span style={{ fontWeight: 600, fontSize: 14, color: 'var(--text-primary)' }}>Notifications</span>
            {alerts.length > 0 && (
              <button onClick={clearAll} style={{ fontSize: 11, color: 'var(--red)', background: 'none', border: 'none', cursor: 'pointer', padding: 0 }}>Clear all</button>
            )}
          </div>
          <div style={{ overflowY: 'auto', flex: 1 }}>
            {alerts.length === 0 ? (
              <div style={{ padding: 20, textAlign: 'center', color: 'var(--text-muted)', fontSize: 13 }}>No notifications</div>
            ) : alerts.map(a => (
              <div key={a.id} style={{ padding: '10px 14px', borderBottom: '1px solid var(--border)', display: 'flex', gap: 10 }}>
                <div style={{ flex: 1 }}>
                  <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-primary)', marginBottom: 2 }}>{a.title}</div>
                  <div style={{ fontSize: 12, color: 'var(--text-secondary)', lineHeight: 1.4 }}>{a.text}</div>
                  <div style={{ fontSize: 10, color: typeColor(a.type), marginTop: 4 }}>{fmt(a.createdAt)}</div>
                </div>
                <button
                  onClick={() => dismiss(a.id)}
                  style={{ alignSelf: 'flex-start', background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer', fontSize: 14, padding: 0, lineHeight: 1 }}
                  title="Dismiss"
                >×</button>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
