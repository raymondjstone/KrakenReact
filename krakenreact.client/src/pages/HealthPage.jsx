import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

export default function HealthPage() {
  const [health, setHealth] = useState(null);
  const [loading, setLoading] = useState(true);
  const [lastChecked, setLastChecked] = useState(null);

  const check = useCallback(() => {
    setLoading(true);
    api.get('/health')
      .then(r => { setHealth(r.data); setLastChecked(new Date()); })
      .catch(() => setHealth(null))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    check();
    const interval = setInterval(check, 60000);
    return () => clearInterval(interval);
  }, [check]);

  const cardStyle = (ok) => ({
    background: 'var(--bg-card)',
    border: `1px solid ${ok ? 'var(--green)' : 'var(--red)'}`,
    borderRadius: 8,
    padding: '16px 20px',
    display: 'flex',
    flexDirection: 'column',
    gap: 6,
  });

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 16, marginBottom: 24 }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>System Health</h2>
        <button
          onClick={check}
          disabled={loading}
          style={{ padding: '6px 16px', background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 4, color: 'var(--text-primary)', cursor: 'pointer', fontSize: 13 }}>
          {loading ? 'Checking…' : 'Refresh'}
        </button>
        {lastChecked && (
          <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>
            Last checked: {lastChecked.toLocaleTimeString()}
          </span>
        )}
        {health && (
          <span style={{
            marginLeft: 'auto', fontSize: 13, fontWeight: 700,
            color: health.ok ? 'var(--green)' : 'var(--red)',
            padding: '4px 12px', border: `1px solid ${health.ok ? 'var(--green)' : 'var(--red)'}`, borderRadius: 6,
          }}>
            {health.ok ? 'All systems OK' : 'Issues detected'}
          </span>
        )}
      </div>

      {loading && !health && (
        <div style={{ color: 'var(--text-muted)', textAlign: 'center', padding: 48 }}>Checking system health…</div>
      )}

      {!loading && !health && (
        <div style={{ color: 'var(--red)', textAlign: 'center', padding: 48 }}>Health check failed — server may be unreachable.</div>
      )}

      {health && (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))', gap: 16 }}>
          {health.checks.map(check => (
            <div key={check.name} style={cardStyle(check.ok)}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <span style={{ fontWeight: 600, fontSize: 15, color: 'var(--text-primary)' }}>{check.name}</span>
                <span style={{
                  fontSize: 11, fontWeight: 700, padding: '2px 8px', borderRadius: 10,
                  background: check.ok ? 'var(--green)' : 'var(--red)',
                  color: 'white', letterSpacing: '0.05em',
                }}>
                  {check.ok ? 'OK' : 'FAIL'}
                </span>
              </div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>{check.detail}</div>
            </div>
          ))}
        </div>
      )}

      {health?.checkedAt && (
        <div style={{ marginTop: 20, fontSize: 12, color: 'var(--text-muted)', textAlign: 'center' }}>
          Server timestamp: {new Date(health.checkedAt).toLocaleString()}
        </div>
      )}
    </div>
  );
}
