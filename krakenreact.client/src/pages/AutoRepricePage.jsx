import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

const EMPTY = {
  symbol: '', maxDeviationPct: 2, minAgeMinutes: 15, maxAgeMinutes: 0,
  repriceBuys: true, repriceSells: false, newPriceOffsetPct: 0, active: true,
};

export default function AutoRepricePage() {
  const [rules, setRules] = useState([]);
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState(null);
  const [saving, setSaving] = useState(false);
  const [statusMsg, setStatusMsg] = useState('');

  const flash = (msg) => { setStatusMsg(msg); setTimeout(() => setStatusMsg(''), 4000); };

  const load = useCallback(() => {
    api.get('/autoreprice').then(r => { setRules(r.data || []); setLoading(false); }).catch(() => setLoading(false));
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleSave = async () => {
    if (!form.symbol.trim()) return flash('Symbol is required');
    if (form.maxDeviationPct <= 0) return flash('Deviation must be positive');
    if (form.minAgeMinutes < 1) return flash('Min age must be at least 1 minute');
    if (!form.repriceBuys && !form.repriceSells) return flash('Enable at least one side (Buy or Sell)');
    setSaving(true);
    try {
      if (form.id) await api.put(`/autoreprice/${form.id}`, form);
      else await api.post('/autoreprice', form);
      load();
      setForm(null);
    } catch (err) {
      flash(err.response?.data?.message || 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id) => {
    if (!window.confirm('Delete this reprice rule?')) return;
    try { await api.delete(`/autoreprice/${id}`); load(); }
    catch { flash('Delete failed'); }
  };

  const inp = { padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13, width: '100%' };

  const sidesLabel = (rule) => {
    if (rule.repriceBuys && rule.repriceSells) return 'Buys + Sells';
    if (rule.repriceBuys) return 'Buys only';
    return 'Sells only';
  };

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 20 }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Smart Repricing</h2>
        <button onClick={() => setForm({ ...EMPTY })} style={{ padding: '6px 16px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13 }}>
          + New Rule
        </button>
        <button onClick={async () => {
          try {
            await api.post('/autoreprice/trigger');
            flash('Job triggered — refresh in a few seconds');
            setTimeout(load, 4000);
          } catch { flash('Trigger failed'); }
        }} style={{ padding: '6px 14px', background: 'var(--bg-primary)', color: 'var(--text-muted)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 13 }}>
          Run Now
        </button>
        {statusMsg && <span style={{ fontSize: 13, color: statusMsg.includes('failed') || statusMsg.includes('required') || statusMsg.includes('least') ? 'var(--red)' : 'var(--green)' }}>{statusMsg}</span>}
      </div>

      {form && (
        <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 24 }}>
          <div style={{ fontWeight: 600, marginBottom: 16, color: 'var(--text-primary)' }}>{form.id ? 'Edit Rule' : 'New Reprice Rule'}</div>

          {/* Row 1: Symbol + Deviation + Min age */}
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 12, marginBottom: 12 }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Symbol</div>
              <input value={form.symbol} onChange={e => setForm(f => ({ ...f, symbol: e.target.value }))} style={inp} placeholder="XBT/USD" />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Trigger — deviation % from market</div>
              <input type="number" min={0.1} step={0.1} value={form.maxDeviationPct}
                onChange={e => setForm(f => ({ ...f, maxDeviationPct: parseFloat(e.target.value) || 2 }))} style={inp} />
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>Reprice when order price drifts this far</div>
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>New order price offset %</div>
              <input type="number" min={0} step={0.1} value={form.newPriceOffsetPct}
                onChange={e => setForm(f => ({ ...f, newPriceOffsetPct: parseFloat(e.target.value) || 0 }))} style={inp} />
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>
                {form.newPriceOffsetPct > 0
                  ? `Buy: ${form.newPriceOffsetPct}% below market · Sell: ${form.newPriceOffsetPct}% above market`
                  : '0 = quasi-market (0.1% inside bid/ask)'}
              </div>
            </div>
          </div>

          {/* Row 2: Min age + Max age + Sides */}
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 12, marginBottom: 16 }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Min order age (minutes)</div>
              <input type="number" min={1} step={1} value={form.minAgeMinutes}
                onChange={e => setForm(f => ({ ...f, minAgeMinutes: parseInt(e.target.value) || 15 }))} style={inp} />
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>Only reprice orders at least this old</div>
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Max order age (minutes, 0 = no limit)</div>
              <input type="number" min={0} step={1} value={form.maxAgeMinutes}
                onChange={e => setForm(f => ({ ...f, maxAgeMinutes: parseInt(e.target.value) || 0 }))} style={inp} />
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 3 }}>Exclude orders older than this (0 = include all)</div>
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 8 }}>Sides to reprice</div>
              <div style={{ display: 'flex', gap: 16 }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13, color: 'var(--green)', cursor: 'pointer' }}>
                  <input type="checkbox" checked={!!form.repriceBuys} onChange={e => setForm(f => ({ ...f, repriceBuys: e.target.checked }))} />
                  Buy orders
                </label>
                <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13, color: 'var(--red)', cursor: 'pointer' }}>
                  <input type="checkbox" checked={!!form.repriceSells} onChange={e => setForm(f => ({ ...f, repriceSells: e.target.checked }))} />
                  Sell orders
                </label>
              </div>
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
          No repricing rules yet. Click <strong>+ New Rule</strong> to auto-chase stale limit orders.
        </div>
      )}

      {rules.map(rule => (
        <div key={rule.id} style={{
          background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8,
          padding: '14px 18px', marginBottom: 12, opacity: rule.active ? 1 : 0.65,
          display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap',
        }}>
          <div>
            <div style={{ fontWeight: 700, fontSize: 16, color: 'var(--text-primary)' }}>{rule.symbol}</div>
            <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
              {sidesLabel(rule)} &middot; trigger &gt;{rule.maxDeviationPct}% off
              &middot; age {rule.minAgeMinutes}{rule.maxAgeMinutes > 0 ? `–${rule.maxAgeMinutes}` : '+'}min
              {rule.newPriceOffsetPct > 0 && <> &middot; place {rule.newPriceOffsetPct}% passive</>}
            </div>
          </div>
          <div style={{ fontSize: 11, color: 'var(--text-muted)', flex: 1 }}>
            {rule.active
              ? <span style={{ color: 'var(--green)', fontWeight: 600 }}>Active</span>
              : <span>Paused</span>}
            {rule.lastRunAt && <span style={{ marginLeft: 10 }}>Last: {new Date(rule.lastRunAt).toLocaleString()}</span>}
            {rule.lastResult && (
              <span style={{ marginLeft: 10, color: rule.lastResult.startsWith('Repriced') ? 'var(--green)' : undefined }}>
                {rule.lastResult}
              </span>
            )}
          </div>
          <div style={{ display: 'flex', gap: 6 }}>
            <button onClick={() => setForm({ ...rule })} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', background: 'var(--bg-primary)', color: 'var(--text-primary)' }}>Edit</button>
            <button onClick={() => handleDelete(rule.id)} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--red, #ef4444)', borderRadius: 4, cursor: 'pointer', background: 'transparent', color: 'var(--red, #ef4444)' }}>Delete</button>
          </div>
        </div>
      ))}

      <div style={{ marginTop: 24, padding: 16, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.6 }}>
        <strong style={{ color: 'var(--text-primary)' }}>How it works</strong><br />
        Every 5 minutes, open limit orders matching the rule's symbol and side(s) are checked.
        If the order has been open for at least <em>Min Age</em> minutes (and less than <em>Max Age</em> if set),
        and its price has drifted more than <em>Trigger %</em> from the current market,
        the order is cancelled and re-placed.<br /><br />
        <strong>New order price:</strong> 0% offset places at the current market (0.1% inside for quick fill).
        A positive offset places the order passively — e.g. 1% means buys land 1% below market, sells 1% above.
      </div>
    </div>
  );
}
