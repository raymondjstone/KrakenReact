import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

const CRON_PRESETS = [
  { label: 'Daily 9am',    value: '0 9 * * *' },
  { label: 'Mon 9am',      value: '0 9 * * 1' },
  { label: 'Mon+Thu 9am',  value: '0 9 * * 1,4' },
  { label: '1st of month', value: '0 9 1 * *' },
];

const emptyRule = { symbol: '', amountUsd: 50, cronExpression: '0 9 * * 1', active: true };

export default function DcaPage() {
  const [rules, setRules] = useState([]);
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState(null); // null = closed, object = new/edit rule
  const [saving, setSaving] = useState(false);
  const [statusMsg, setStatusMsg] = useState('');

  const fetchRules = useCallback(() => {
    api.get('/dca').then(r => { setRules(r.data || []); setLoading(false); }).catch(() => setLoading(false));
  }, []);

  useEffect(() => { fetchRules(); }, [fetchRules]);

  const flash = (msg) => {
    setStatusMsg(msg);
    setTimeout(() => setStatusMsg(''), 4000);
  };

  const handleSave = async () => {
    if (!form.symbol.trim()) return flash('Symbol is required');
    if (form.amountUsd <= 0) return flash('Amount must be positive');
    setSaving(true);
    try {
      if (form.id) {
        await api.put(`/dca/${form.id}`, form);
      } else {
        await api.post('/dca', form);
      }
      fetchRules();
      setForm(null);
    } catch (err) {
      flash(err.response?.data?.message || 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id) => {
    if (!window.confirm('Delete this DCA rule?')) return;
    try { await api.delete(`/dca/${id}`); fetchRules(); }
    catch { flash('Delete failed'); }
  };

  const handleTrigger = async (id) => {
    try {
      await api.post(`/dca/${id}/trigger`);
      flash('Buy enqueued — check Hangfire for progress');
    } catch { flash('Trigger failed'); }
  };

  const inputStyle = { padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13, width: '100%' };

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 20 }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Dollar-Cost Averaging</h2>
        <button onClick={() => setForm({ ...emptyRule })} style={{ padding: '6px 16px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13 }}>
          + New Rule
        </button>
        {statusMsg && <span style={{ fontSize: 13, color: statusMsg.includes('failed') || statusMsg.includes('required') ? 'var(--red)' : 'var(--green)' }}>{statusMsg}</span>}
      </div>

      {form && (
        <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 24 }}>
          <div style={{ fontWeight: 600, marginBottom: 16, color: 'var(--text-primary)' }}>{form.id ? 'Edit Rule' : 'New DCA Rule'}</div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 16 }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Symbol (e.g. XBT/USD)</div>
              <input value={form.symbol} onChange={e => setForm(f => ({ ...f, symbol: e.target.value }))} style={inputStyle} placeholder="XBT/USD" />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Buy amount (USD)</div>
              <input type="number" min={1} value={form.amountUsd} onChange={e => setForm(f => ({ ...f, amountUsd: parseFloat(e.target.value) || 0 }))} style={inputStyle} />
            </div>
          </div>
          <div style={{ marginBottom: 16 }}>
            <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 6 }}>Schedule (cron)</div>
            <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 8 }}>
              {CRON_PRESETS.map(p => (
                <button key={p.value} onClick={() => setForm(f => ({ ...f, cronExpression: p.value }))} style={{
                  padding: '3px 10px', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 12,
                  background: form.cronExpression === p.value ? 'var(--green)' : 'var(--bg-primary)', color: form.cronExpression === p.value ? 'white' : 'var(--text-primary)',
                }}>{p.label}</button>
              ))}
            </div>
            <input value={form.cronExpression} onChange={e => setForm(f => ({ ...f, cronExpression: e.target.value }))} style={inputStyle} placeholder="0 9 * * 1" />
            <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 4 }}>Standard 5-field cron. Times are local server time.</div>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, color: 'var(--text-primary)', cursor: 'pointer' }}>
              <input type="checkbox" checked={form.active} onChange={e => setForm(f => ({ ...f, active: e.target.checked }))} />
              Active
            </label>
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

      {!loading && rules.length === 0 && !form && (
        <div style={{ color: 'var(--text-muted)', textAlign: 'center', padding: 48 }}>
          No DCA rules yet. Click <strong>+ New Rule</strong> to set up recurring buys.
        </div>
      )}

      {rules.map(rule => (
        <div key={rule.id} style={{
          background: 'var(--bg-card)', border: `1px solid ${rule.active ? 'var(--border)' : 'var(--border)'}`,
          borderRadius: 8, padding: '14px 18px', marginBottom: 12,
          opacity: rule.active ? 1 : 0.65, display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap',
        }}>
          <div>
            <div style={{ fontWeight: 700, fontSize: 16, color: 'var(--text-primary)' }}>{rule.symbol}</div>
            <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
              ${rule.amountUsd} USD &middot; <code style={{ fontSize: 11 }}>{rule.cronExpression}</code>
            </div>
          </div>
          <div style={{ fontSize: 11, color: 'var(--text-muted)', flex: 1 }}>
            {rule.active ? (
              <span style={{ color: 'var(--green)', fontWeight: 600 }}>Active</span>
            ) : (
              <span style={{ color: 'var(--text-muted)' }}>Paused</span>
            )}
            {rule.lastRunAt && (
              <span style={{ marginLeft: 10 }}>Last run: {new Date(rule.lastRunAt).toLocaleString()}</span>
            )}
            {rule.lastRunResult && (
              <span style={{ marginLeft: 10, color: rule.lastRunResult.startsWith('OK') ? 'var(--green)' : 'var(--red)' }}>
                {rule.lastRunResult.substring(0, 60)}
              </span>
            )}
          </div>
          <div style={{ display: 'flex', gap: 6 }}>
            <button onClick={() => handleTrigger(rule.id)} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', background: 'var(--bg-primary)', color: 'var(--text-primary)' }}>Buy Now</button>
            <button onClick={() => setForm({ ...rule })} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', background: 'var(--bg-primary)', color: 'var(--text-primary)' }}>Edit</button>
            <button onClick={() => handleDelete(rule.id)} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--red, #ef4444)', borderRadius: 4, cursor: 'pointer', background: 'transparent', color: 'var(--red, #ef4444)' }}>Delete</button>
          </div>
        </div>
      ))}

      <div style={{ marginTop: 24, padding: 16, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.6 }}>
        <strong style={{ color: 'var(--text-primary)' }}>How it works</strong><br />
        Each rule places a limit buy at 0.2% above the current market price on the configured schedule.
        This behaves like a near-market-order (Post-Only limit) and will fill quickly in normal conditions.
        Hangfire executes the job in the background — use <a href="/hangfire" target="_blank" style={{ color: 'var(--green)' }}>Hangfire Dashboard</a> to monitor runs.
      </div>
    </div>
  );
}
