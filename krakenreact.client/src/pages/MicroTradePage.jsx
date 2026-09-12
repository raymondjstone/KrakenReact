import { useState, useEffect, useCallback, useRef } from 'react';
import api from '../api/apiClient';

const emptyRule = {
  symbol: '', dropPct: 5, risePct: 10, buyOrderTotal: 100,
  maxOrdersPerWindow: 2, windowHours: 2, cooldownHours: 1,
  stopLossEnabled: false, stopLossPct: 95,
  active: true, dryRun: true,
};

const STATUS_COLORS = {
  Buying: 'var(--text-muted)',
  Selling: 'var(--green)',
  Sold: 'var(--green)',
  Cancelled: 'var(--red, #ef4444)',
  DryRun: 'var(--text-muted)',
};

export default function MicroTradePage() {
  const [rules, setRules] = useState([]);
  const [orders, setOrders] = useState([]);
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState(null);
  const [saving, setSaving] = useState(false);
  const [statusMsg, setStatusMsg] = useState('');
  const flashTimerRef = useRef(null);

  const fetchAll = useCallback(() => {
    api.get('/microtrade').then(r => { setRules(r.data || []); setLoading(false); }).catch(() => setLoading(false));
    api.get('/microtrade/orders').then(r => setOrders(r.data || [])).catch(() => {});
  }, []);

  useEffect(() => {
    fetchAll();
    const interval = setInterval(fetchAll, 15000);
    return () => clearInterval(interval);
  }, [fetchAll]);

  const flash = (msg) => {
    setStatusMsg(msg);
    if (flashTimerRef.current) clearTimeout(flashTimerRef.current);
    flashTimerRef.current = setTimeout(() => setStatusMsg(''), 4000);
  };

  const handleSave = async () => {
    if (!form.symbol.trim()) return flash('Symbol is required');
    if (form.dropPct <= 0) return flash('Drop % must be positive');
    if (form.risePct <= 0) return flash('Rise % must be positive');
    if (form.buyOrderTotal <= 0) return flash('Buy order total must be positive');
    if (form.maxOrdersPerWindow < 1) return flash('Max orders per window must be at least 1');
    if (form.windowHours < 1) return flash('Window hours must be at least 1');
    if (form.cooldownHours < 0) return flash('Cooldown hours cannot be negative');
    if (form.stopLossEnabled && (form.stopLossPct <= 0 || form.stopLossPct >= 100)) return flash('Stop loss % must be between 0 and 100');
    setSaving(true);
    try {
      if (form.id) {
        await api.put(`/microtrade/${form.id}`, form);
      } else {
        await api.post('/microtrade', form);
      }
      fetchAll();
      setForm(null);
    } catch (err) {
      flash(err.response?.data?.message || 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id) => {
    if (!window.confirm('Delete this micro trade rule? Order history is kept.')) return;
    try { await api.delete(`/microtrade/${id}`); fetchAll(); }
    catch { flash('Delete failed'); }
  };

  const handleTrigger = async (id) => {
    try {
      await api.post(`/microtrade/${id}/trigger`);
      flash('Check enqueued — see Hangfire for progress');
    } catch { flash('Trigger failed'); }
  };

  const handleCancelOrder = async (id) => {
    if (!window.confirm('Cancel the open leg of this order?')) return;
    try { await api.post(`/microtrade/orders/${id}/cancel`); fetchAll(); }
    catch { flash('Cancel failed'); }
  };

  const inputStyle = {
    padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4,
    background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13, width: '100%', boxSizing: 'border-box',
  };

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 20 }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>Micro Trading</h2>
        <button onClick={() => setForm({ ...emptyRule })} style={{ padding: '6px 16px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13 }}>
          + New Rule
        </button>
        {statusMsg && <span style={{ fontSize: 13, color: statusMsg.includes('failed') || statusMsg.includes('required') || statusMsg.includes('must') || statusMsg.includes('positive') ? 'var(--red)' : 'var(--green)' }}>{statusMsg}</span>}
      </div>

      {/* Form */}
      {form && (
        <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 24 }}>
          <div style={{ fontWeight: 600, marginBottom: 16, color: 'var(--text-primary)' }}>{form.id ? 'Edit Rule' : 'New Micro Trade Rule'}</div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 12, marginBottom: 12 }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Symbol / Pair (e.g. XBT/USD)</div>
              <input value={form.symbol} onChange={e => setForm(f => ({ ...f, symbol: e.target.value.toUpperCase() }))} style={inputStyle} placeholder="XBT/USD" />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Buy order total ($)</div>
              <input type="number" min={1} step={1} value={form.buyOrderTotal} onChange={e => setForm(f => ({ ...f, buyOrderTotal: parseFloat(e.target.value) || 0 }))} style={inputStyle} />
            </div>
            <div />
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr 1fr 1fr', gap: 12, marginBottom: 16 }}>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Buy when 24h drop &gt;</div>
              <input type="number" min={0.1} step={0.1} value={form.dropPct} onChange={e => setForm(f => ({ ...f, dropPct: parseFloat(e.target.value) || 0 }))} style={inputStyle} />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Sell target above buy +</div>
              <input type="number" min={0.1} step={0.1} value={form.risePct} onChange={e => setForm(f => ({ ...f, risePct: parseFloat(e.target.value) || 0 }))} style={inputStyle} />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Max orders per window</div>
              <input type="number" min={1} step={1} value={form.maxOrdersPerWindow} onChange={e => setForm(f => ({ ...f, maxOrdersPerWindow: parseInt(e.target.value) || 1 }))} style={inputStyle} />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Window (hours)</div>
              <input type="number" min={1} step={1} value={form.windowHours} onChange={e => setForm(f => ({ ...f, windowHours: parseInt(e.target.value) || 1 }))} style={inputStyle} />
            </div>
            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Cooldown between orders (hours)</div>
              <input type="number" min={0} step={1} value={form.cooldownHours} onChange={e => setForm(f => ({ ...f, cooldownHours: parseInt(e.target.value) || 0 }))} style={inputStyle} />
            </div>
          </div>

          <div style={{ marginBottom: 16 }}>
            <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 8 }}>Stop Loss</div>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, color: 'var(--text-primary)', cursor: 'pointer', marginBottom: 8 }}>
              <input type="checkbox" checked={!!form.stopLossEnabled} onChange={e => setForm(f => ({ ...f, stopLossEnabled: e.target.checked }))} />
              Reprice the sell down if the price keeps falling
            </label>
            {form.stopLossEnabled && (
              <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginLeft: 24 }}>
                <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>Trigger at:</span>
                <input type="number" min={1} max={99.9} step={0.1} value={form.stopLossPct}
                  onChange={e => setForm(f => ({ ...f, stopLossPct: parseFloat(e.target.value) || 0 }))}
                  style={{ ...inputStyle, width: 90 }} />
                <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                  % of buy price ({(100 - (form.stopLossPct || 0)).toFixed(1)}% drop from buy) — cancels the resting sell and re-places it near the current price
                </span>
              </div>
            )}
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: 20 }}>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, color: 'var(--text-primary)', cursor: 'pointer' }}>
              <input type="checkbox" checked={form.active} onChange={e => setForm(f => ({ ...f, active: e.target.checked }))} />
              Active
            </label>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, color: 'var(--text-primary)', cursor: 'pointer' }}>
              <input type="checkbox" checked={form.dryRun} onChange={e => setForm(f => ({ ...f, dryRun: e.target.checked }))} />
              Dry run (simulate — no real orders)
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
          No micro trade rules yet. Click <strong>+ New Rule</strong> to start dip-buying a pair.
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
              {rule.dryRun && <span style={{ marginLeft: 8, color: 'var(--text-muted)' }}>· DRY RUN</span>}
            </div>
          </div>

          <div style={{ flex: 1, display: 'flex', gap: 24, flexWrap: 'wrap' }}>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Buy trigger</div>
              <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--red, #ef4444)' }}>24h drop &gt; {rule.dropPct}%</div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Sell target</div>
              <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--green)' }}>buy + {rule.risePct}%</div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Buy size</div>
              <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)' }}>${rule.buyOrderTotal}</div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Rate limit</div>
              <div style={{ fontSize: 13, color: 'var(--text-muted)' }}>{rule.maxOrdersPerWindow} / {rule.windowHours}h</div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Cooldown</div>
              <div style={{ fontSize: 13, color: 'var(--text-muted)' }}>{rule.cooldownHours}h between orders</div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Stop loss</div>
              <div style={{ fontSize: 13, color: rule.stopLossEnabled ? 'var(--red, #ef4444)' : 'var(--text-muted)' }}>
                {rule.stopLossEnabled ? `${rule.stopLossPct}% of buy` : 'Off'}
              </div>
            </div>
            {rule.lastCheckedAt && (
              <div>
                <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Last checked</div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>{new Date(rule.lastCheckedAt).toLocaleString()}</div>
              </div>
            )}
            {rule.lastResult && (
              <div style={{ flex: 1, minWidth: 180 }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Last result</div>
                <div style={{ fontSize: 12, color: rule.lastResult.startsWith('OK') || rule.lastResult.startsWith('DRY RUN') ? 'var(--green)' : 'var(--text-muted)' }}>
                  {rule.lastResult.substring(0, 90)}
                </div>
              </div>
            )}
          </div>

          <div style={{ display: 'flex', gap: 6 }}>
            <button onClick={() => handleTrigger(rule.id)} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', background: 'var(--bg-primary)', color: 'var(--text-primary)' }}>Check Now</button>
            <button onClick={() => setForm({ ...rule })} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', background: 'var(--bg-primary)', color: 'var(--text-primary)' }}>Edit</button>
            <button onClick={() => handleDelete(rule.id)} style={{ padding: '4px 10px', fontSize: 12, border: '1px solid var(--red, #ef4444)', borderRadius: 4, cursor: 'pointer', background: 'transparent', color: 'var(--red, #ef4444)' }}>Delete</button>
          </div>
        </div>
      ))}

      {/* Order history */}
      <div style={{ marginTop: 28 }}>
        <div style={{ fontWeight: 600, marginBottom: 10, color: 'var(--text-primary)' }}>Recent Orders</div>
        {orders.length === 0 ? (
          <div style={{ color: 'var(--text-muted)', fontSize: 13 }}>No micro trade orders yet.</div>
        ) : (
          <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, overflow: 'hidden' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
              <thead>
                <tr style={{ textAlign: 'left', color: 'var(--text-muted)', borderBottom: '1px solid var(--border)' }}>
                  <th style={{ padding: '8px 12px' }}>Symbol</th>
                  <th style={{ padding: '8px 12px' }}>Status</th>
                  <th style={{ padding: '8px 12px' }}>Qty</th>
                  <th style={{ padding: '8px 12px' }}>Buy Price</th>
                  <th style={{ padding: '8px 12px' }}>Sell Price</th>
                  <th style={{ padding: '8px 12px' }}>Created</th>
                  <th style={{ padding: '8px 12px' }}></th>
                </tr>
              </thead>
              <tbody>
                {orders.map(o => (
                  <tr key={o.id} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ padding: '8px 12px', color: 'var(--text-primary)', fontWeight: 600 }}>{o.symbol}</td>
                    <td style={{ padding: '8px 12px', color: STATUS_COLORS[o.status] || 'var(--text-muted)', fontWeight: 600 }}>
                      {o.status}
                      {o.stopLossTriggered && <span title="Stop loss repriced this sell" style={{ marginLeft: 6, fontSize: 10, color: 'var(--red, #ef4444)' }}>SL</span>}
                    </td>
                    <td style={{ padding: '8px 12px', color: 'var(--text-primary)' }}>{o.quantity}</td>
                    <td style={{ padding: '8px 12px', color: 'var(--text-primary)' }}>{o.buyPrice}</td>
                    <td style={{ padding: '8px 12px', color: 'var(--text-primary)' }}>{o.sellPrice > 0 ? o.sellPrice : '—'}</td>
                    <td style={{ padding: '8px 12px', color: 'var(--text-muted)' }}>{new Date(o.createdAt).toLocaleString()}</td>
                    <td style={{ padding: '8px 12px' }}>
                      {(o.status === 'Buying' || o.status === 'Selling') && (
                        <button onClick={() => handleCancelOrder(o.id)} style={{ padding: '3px 8px', fontSize: 11, border: '1px solid var(--red, #ef4444)', borderRadius: 4, cursor: 'pointer', background: 'transparent', color: 'var(--red, #ef4444)' }}>Cancel</button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div style={{ marginTop: 24, padding: 16, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.7 }}>
        <strong style={{ color: 'var(--text-primary)' }}>How Micro Trading Works</strong><br />
        Every 15 minutes each active rule checks its pair's 24-hour price change. If the price has dropped more than the configured <strong>Drop %</strong>,
        a limit buy is placed for the <strong>Buy order total</strong> at 0.1% below the current price.
        When that buy fills, a limit sell is automatically placed at the fill price plus the <strong>Rise %</strong>.
        The <strong>rate limit</strong> caps how many buy orders a rule can place within its rolling window, so a pair that keeps dropping doesn't get bought over and over.
        The <strong>cooldown</strong> is an additional guard: no order is placed on a pair — from any rule — within that many hours of the last order on the same pair.
        <strong>Stop loss</strong> is optional and off by default: when on, if the price falls to or below the configured % of the buy price while the profit-target sell is still resting, that sell is cancelled and re-placed near the current (lower) price so it can actually fill and cut the loss, instead of sitting forever above a market that kept dropping.
        Turn on <strong>Dry run</strong> to see what a rule would do — via Pushover notifications and the order log — without placing real orders.
      </div>
    </div>
  );
}
