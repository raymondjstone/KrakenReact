import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

const emptyRule = { symbol: '', triggerPct: 20, sellPct: 25, active: true, cooldownHours: 24 };

export default function ProfitLadderPage() {
  const [rules, setRules] = useState([]);
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState(null);
  const [saving, setSaving] = useState(false);
  const [statusMsg, setStatusMsg] = useState('');

  const fetchRules = useCallback(() => {
    api.get('/profitladder').then(r => { setRules(r.data || []); setLoading(false); }).catch(() => setLoading(false));
  }, []);

  useEffect(() => { fetchRules(); }, [fetchRules]);

  const flash = (msg) => {
    setStatusMsg(msg);
    setTimeout(() => setStatusMsg(''), 4000);
  };

  const handleSave = async () => {
    if (!form.symbol.trim()) return flash('Symbol is required');
    if (form.triggerPct <= 0) return flash('Trigger % must be positive');
    if (form.sellPct <= 0 || form.sellPct > 100) return flash('Sell % must be 1–100');
    setSaving(true);
    try {
      if (form.id) {
        await api.put(`/profitladder/${form.id}`, form);
      } else {
        await api.post('/profitladder', form);
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
    if (!window.confirm('Delete this profit ladder rule?')) return;
    try { await api.delete(`/profitladder/${id}`); fetchRules(); }
    catch { flash('Delete failed'); }
  };

  const inputStyle = {
    padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4,
    background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13, width: '100%', boxSizing: 'border-box',
  };

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 20 }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Profit Ladder</h2>
        <button onClick={() => setForm({ ...emptyRule })} style={{ padding: '6px 16px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13 }}>
          + New Rule
        </button>
        {statusMsg && <span style={{ fontSize: 13, color: statusMsg.includes('failed') || statusMsg.includes('required') || statusMsg.includes('must') ? 'var(--red)' : 'var(--green)' }}>{statusMsg}</span>}
      </div>

      {/* Form */}
      {form && (
        <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 24 }}>
          <div style={{ fontWeight: 600, marginBottom: 16, color: 'var(--text-primary)' }}>{form.id ? 'Edit Rule' : 'New Profit Ladder Rule'}</div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr 1fr', gap: 12, marginBottom: 16 }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Symbol (e.g. BTC)</div>
              <input value={form.symbol} onChange={e => setForm(f => ({ ...f, symbol: e.target.value.toUpperCase() }))} style={inputStyle} placeholder="BTC" />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Trigger: profit above cost %</div>
              <input type="number" min={1} step={1} value={form.triggerPct} onChange={e => setForm(f => ({ ...f, triggerPct: parseFloat(e.target.value) || 0 }))} style={inputStyle} />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Sell % of available balance</div>
              <input type="number" min={1} max={100} step={1} value={form.sellPct} onChange={e => setForm(f => ({ ...f, sellPct: parseFloat(e.target.value) || 0 }))} style={inputStyle} />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Cooldown (hours)</div>
              <input type="number" min={0} step={1} value={form.cooldownHours} onChange={e => setForm(f => ({ ...f, cooldownHours: parseInt(e.target.value) || 0 }))} style={inputStyle} />
            </div>
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
          No profit ladder rules yet. Click <strong>+ New Rule</strong> to automate partial profit-taking.
        </div>
      )}

      {rules.map(rule => (
        <div key={rule.id} style={{
          background: 'var(--bg-card)', border: '1px solid var(--border)',
          borderRadius: 8, padding: '14px 18px', marginBottom: 12,
          opacity: rule.active ? 1 : 0.65, display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap',
        }}>
          <div style={{ minWidth: 100 }}>
            <div style={{ fontWeight: 700, fontSize: 16, color: 'var(--text-primary)' }}>{rule.symbol}</div>
            <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
              {rule.active ? <span style={{ color: 'var(--green)', fontWeight: 600 }}>Active</span> : <span>Paused</span>}
            </div>
          </div>

          <div style={{ flex: 1, display: 'flex', gap: 24, flexWrap: 'wrap' }}>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Trigger when</div>
              <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--green)' }}>+{rule.triggerPct}% above cost</div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Sell</div>
              <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)' }}>{rule.sellPct}% of balance</div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Cooldown</div>
              <div style={{ fontSize: 13, color: 'var(--text-muted)' }}>{rule.cooldownHours}h</div>
            </div>
            {rule.lastTriggeredAt && (
              <div>
                <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Last triggered</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>{new Date(rule.lastTriggeredAt).toLocaleString()}</div>
              </div>
            )}
            {rule.lastResult && (
              <div style={{ flex: 1 }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Last result</div>
                <div style={{ fontSize: 12, color: rule.lastResult.startsWith('OK') ? 'var(--green)' : 'var(--red)' }}>
                  {rule.lastResult.substring(0, 80)}
                </div>
              </div>
            )}
          </div>

          <div style={{ display: 'flex', gap: 6 }}>
            <button onClick={() => setForm({ ...rule })} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', background: 'var(--bg-primary)', color: 'var(--text-primary)' }}>Edit</button>
            <button onClick={() => handleDelete(rule.id)} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--red, #ef4444)', borderRadius: 4, cursor: 'pointer', background: 'transparent', color: 'var(--red, #ef4444)' }}>Delete</button>
          </div>
        </div>
      ))}

      <div style={{ marginTop: 24, padding: 16, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.7 }}>
        <strong style={{ color: 'var(--text-primary)' }}>How Profit Ladder Works</strong><br />
        Each rule automatically places a <strong>limit sell</strong> when a held asset's current price rises above your average cost basis by the configured <strong>Trigger %</strong>.
        It sells the specified <strong>Sell %</strong> of your available balance at the current market price.<br />
        A <strong>Cooldown</strong> prevents the same rule from firing again too soon after a trigger.<br />
        Rules are checked every 5 minutes by the same job that handles stop-loss and take-profit.
        Multiple rules on the same asset let you ladder out: e.g. sell 10% at +20%, another 20% at +50%, another 25% at +100%.
      </div>
    </div>
  );
}
