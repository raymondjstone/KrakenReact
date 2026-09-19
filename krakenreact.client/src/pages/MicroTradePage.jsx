import { useState, useEffect, useCallback, useRef } from 'react';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';

const emptyRule = {
  symbol: '', dropPct: 5, dropIntervalHours: 24, risePct: 10, buyOrderTotal: 100,
  maxOrdersPerWindow: 2, windowHours: 2, cooldownHours: 1,
  stopLossEnabled: false, stopLossPct: 95,
  active: true, dryRun: true,
};

const DROP_INTERVALS = [1, 4, 6, 12, 24];
const AMBER = '#f59e0b';

// Non-selected columns are just informational: green if rising, red if falling.
function signColor(changePct) {
  if (changePct == null) return 'var(--text-muted)';
  return changePct >= 0 ? 'var(--green)' : 'var(--red, #ef4444)';
}

// The selected column is the rule's actual trigger, so how close it is to firing matters more than
// its raw sign: amber inside 2 points of the trigger line, red inside 0.5 (including past it).
function proximityColor(changePct, dropPct) {
  if (changePct == null) return 'var(--text-muted)';
  const distanceToTrigger = changePct + dropPct; // <= 0 means already at/past the trigger
  if (distanceToTrigger <= 0.5) return 'var(--red, #ef4444)';
  if (distanceToTrigger <= 2) return AMBER;
  return signColor(changePct);
}

const stepBtnStyle = {
  width: 22, height: 22, padding: 0, lineHeight: 1, fontSize: 14, fontWeight: 700, cursor: 'pointer',
  border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)',
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
  const [prices, setPrices] = useState({}); // { SYMBOL: current price }
  const [references, setReferences] = useState({}); // { SYMBOL: { "1": price N hours ago, ... } }
  const [changes, setChanges] = useState({}); // { SYMBOL: { "1": pct, "4": pct, "6": pct, "12": pct, "24": pct } }
  const [loading, setLoading] = useState(true);
  const [form, setForm] = useState(null);
  const [saving, setSaving] = useState(false);
  const [statusMsg, setStatusMsg] = useState('');
  const [emergencyStop, setEmergencyStop] = useState(false);
  const [stopToggling, setStopToggling] = useState(false);
  const flashTimerRef = useRef(null);

  const fetchEmergencyStop = useCallback(() => {
    api.get('/microtrade/emergency-stop').then(r => setEmergencyStop(!!r.data?.enabled)).catch(() => {});
  }, []);

  const toggleEmergencyStop = async () => {
    const next = !emergencyStop;
    if (next && !window.confirm('Activate emergency stop? No new Micro Trade buys will be placed until this is turned off. Existing open positions will still be monitored and sold as normal.')) return;
    setStopToggling(true);
    try {
      const r = await api.post('/microtrade/emergency-stop', { enabled: next });
      setEmergencyStop(!!r.data?.enabled);
      flash(next ? 'Emergency stop ACTIVE — no new buys will be placed' : 'Emergency stop deactivated');
    } catch {
      flash('Failed to update emergency stop');
    } finally {
      setStopToggling(false);
    }
  };

  // One call per unique symbol across all rules; the server caches the underlying kline fetch, so
  // polling this every 15s doesn't multiply into a Kraken REST call per poll.
  const fetchChanges = useCallback((ruleList) => {
    const symbols = [...new Set(ruleList.map(r => r.symbol).filter(Boolean))];
    Promise.all(symbols.map(sym =>
      api.get(`/prices/quote/${encodeURIComponent(sym.replace('/', '-'))}`).then(r => [sym.toUpperCase(), r.data.price]).catch(() => [sym.toUpperCase(), null])
    )).then(pairs => {
      setPrices(prev => {
        const next = { ...prev };
        pairs.forEach(([key, val]) => { if (val) next[key] = val; });
        return next;
      });
    });
    Promise.all(symbols.map(sym =>
      api.get(`/prices/${encodeURIComponent(sym)}/changes`).then(r => [sym.toUpperCase(), r.data]).catch(() => [sym.toUpperCase(), null])
    )).then(pairs => {
      setChanges(prev => {
        const next = { ...prev };
        pairs.forEach(([key, val]) => { if (val) next[key] = val; });
        return next;
      });
    });
    Promise.all(symbols.map(sym =>
      api.get(`/prices/${encodeURIComponent(sym)}/references`).then(r => [sym.toUpperCase(), r.data]).catch(() => [sym.toUpperCase(), null])
    )).then(pairs => {
      setReferences(prev => {
        const next = { ...prev };
        pairs.forEach(([key, val]) => { if (val) next[key] = val; });
        return next;
      });
    });
  }, []);

  const fetchAll = useCallback(() => {
    api.get('/microtrade').then(r => {
      const list = r.data || [];
      setRules(list);
      setLoading(false);
      fetchChanges(list);
    }).catch(() => setLoading(false));
    api.get('/microtrade/orders').then(r => setOrders(r.data || [])).catch(() => {});
  }, [fetchChanges]);

  // Live price: the server pushes a TickerUpdate on every tick, so the current price moves with the
  // market between the 15s polls above instead of only changing on refresh.
  useEffect(() => {
    const conn = getConnection();
    const norm = s => (s || '').toUpperCase().replace(/[^A-Z0-9]/g, '').replace(/^XBT/, 'BTC');
    const handler = (data) => {
      if (!data?.closePrice || !data.symbol) return;
      const incoming = norm(data.symbol);
      setPrices(prev => {
        let next = null;
        for (const key of Object.keys(prev)) {
          if (norm(key) === incoming && prev[key] !== data.closePrice) {
            next = next || { ...prev };
            next[key] = data.closePrice;
          }
        }
        return next || prev;
      });
    };
    conn.on('TickerUpdate', handler);
    return () => conn.off('TickerUpdate', handler);
  }, []);

  useEffect(() => {
    fetchAll();
    fetchEmergencyStop();
    const interval = setInterval(() => { fetchAll(); fetchEmergencyStop(); }, 15000);
    return () => clearInterval(interval);
  }, [fetchAll, fetchEmergencyStop]);

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

  // Nudge a rule's buy margin (dropPct) or sell target (risePct) by +/-1 point straight from its card.
  // Optimistic local update so repeated clicks feel instant; the server value replaces it on the next fetch.
  const handleAdjust = async (rule, field, delta) => {
    const next = Math.round((rule[field] + delta) * 100) / 100;
    if (next <= 0) return flash(`${field === 'dropPct' ? 'Buy margin' : 'Sell target'} must stay above 0%`);
    const updated = { ...rule, [field]: next };
    setRules(prev => prev.map(r => r.id === rule.id ? updated : r));
    try { await api.put(`/microtrade/${rule.id}`, updated); }
    catch (err) { flash(err.response?.data?.message || 'Update failed'); fetchAll(); }
  };

  // Step the rule's drop interval to the previous/next supported window (1/4/6/12/24h); stops at the ends.
  const handleInterval = async (rule, direction) => {
    const current = rule.dropIntervalHours || 24;
    const idx = DROP_INTERVALS.indexOf(current);
    const next = DROP_INTERVALS[idx + direction];
    if (next == null) return;
    const updated = { ...rule, dropIntervalHours: next };
    setRules(prev => prev.map(r => r.id === rule.id ? updated : r));
    try { await api.put(`/microtrade/${rule.id}`, updated); }
    catch (err) { flash(err.response?.data?.message || 'Update failed'); fetchAll(); }
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
        <button
          onClick={toggleEmergencyStop}
          disabled={stopToggling}
          title={emergencyStop ? 'Click to deactivate — buys will resume' : 'Click to stop all new Micro Trade buys immediately'}
          style={{
            padding: '6px 16px', borderRadius: 4, cursor: 'pointer', fontWeight: 700, fontSize: 13,
            border: `1px solid ${emergencyStop ? 'var(--red, #ef4444)' : 'var(--border)'}`,
            background: emergencyStop ? 'var(--red, #ef4444)' : 'var(--bg-primary)',
            color: emergencyStop ? 'white' : 'var(--text-primary)',
          }}
        >
          {emergencyStop ? '■ EMERGENCY STOP ACTIVE — click to resume' : 'Emergency Stop'}
        </button>
        {statusMsg && <span style={{ fontSize: 13, color: statusMsg.includes('failed') || statusMsg.includes('required') || statusMsg.includes('must') || statusMsg.includes('positive') ? 'var(--red)' : 'var(--green)' }}>{statusMsg}</span>}
      </div>

      {emergencyStop && (
        <div style={{
          background: 'var(--red, #ef4444)', color: 'white', borderRadius: 8, padding: '10px 16px',
          marginBottom: 20, fontSize: 13, fontWeight: 600,
        }}>
          Emergency stop is active — no new Micro Trade buy orders will be placed on any rule. Existing open positions are still being monitored and sold as normal.
        </div>
      )}

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
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 4 }}>Buy when drop over &gt;</div>
              <div style={{ display: 'flex', gap: 6 }}>
                <input type="number" min={0.1} step={0.1} value={form.dropPct} onChange={e => setForm(f => ({ ...f, dropPct: parseFloat(e.target.value) || 0 }))} style={inputStyle} />
                <select value={form.dropIntervalHours} onChange={e => setForm(f => ({ ...f, dropIntervalHours: parseInt(e.target.value) }))} style={{ ...inputStyle, width: 80, flexShrink: 0 }}>
                  {DROP_INTERVALS.map(h => <option key={h} value={h}>{h}h</option>)}
                </select>
              </div>
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

      {rules.map(rule => {
        const symbolChanges = changes[rule.symbol?.toUpperCase()];
        const currentPrice = prices[rule.symbol?.toUpperCase()];
        const symbolRefs = references[rule.symbol?.toUpperCase()];
        // Buy fires when price <= reference * (1 - drop%), the same test the % change uses.
        const refPrice = symbolRefs?.[rule.dropIntervalHours || 24];
        const triggerPrice = refPrice ? refPrice * (1 - rule.dropPct / 100) : null;
        const intervalHours = rule.dropIntervalHours || 24;
        return (
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
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>Price change</div>
              <div style={{ display: 'flex', gap: 6 }}>
                {DROP_INTERVALS.map(h => {
                  const val = symbolChanges?.[h];
                  const isSelected = h === intervalHours;
                  const color = isSelected ? proximityColor(val, rule.dropPct) : signColor(val);
                  return (
                    <div key={h} title={isSelected ? `Trigger: buy when the ${h}h change is <= -${rule.dropPct}%` : `${h}h change`} style={{
                      padding: '2px 7px', borderRadius: 4, textAlign: 'center', minWidth: 52,
                      border: isSelected ? `1px solid ${color}` : '1px solid transparent',
                    }}>
                      <div style={{ fontSize: 10, color: isSelected ? 'var(--text-primary)' : 'var(--text-muted)' }}>{h}h</div>
                      <div style={{ fontSize: 13, fontWeight: 700, color }}>
                        {val == null ? '—' : `${val >= 0 ? '+' : ''}${val.toFixed(2)}%`}
                      </div>
                      {isSelected && <div style={{ fontSize: 9, color: 'var(--text-primary)', whiteSpace: 'nowrap' }}>&le; -{rule.dropPct}%</div>}
                    </div>
                  );
                })}
              </div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Current price</div>
              <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)' }}>
                {currentPrice == null ? '—' : currentPrice}
              </div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Buy trigger price</div>
              <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)' }} title={refPrice ? `${intervalHours}h ago: ${refPrice} → buy at or below ${triggerPrice}` : undefined}>
                {triggerPrice == null ? '—' : `≤ ${Number(triggerPrice.toPrecision(6))}`}
              </div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 2 }}>Interval</div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <button onClick={() => handleInterval(rule, -1)} disabled={DROP_INTERVALS.indexOf(intervalHours) <= 0} title="Use the next shorter interval" style={{ ...stepBtnStyle, opacity: DROP_INTERVALS.indexOf(intervalHours) <= 0 ? 0.35 : 1 }}>&minus;</button>
                <span style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)', minWidth: 32, textAlign: 'center' }}>{intervalHours}h</span>
                <button onClick={() => handleInterval(rule, 1)} disabled={DROP_INTERVALS.indexOf(intervalHours) >= DROP_INTERVALS.length - 1} title="Use the next longer interval" style={{ ...stepBtnStyle, opacity: DROP_INTERVALS.indexOf(intervalHours) >= DROP_INTERVALS.length - 1 ? 0.35 : 1 }}>+</button>
              </div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 2 }}>Buy margin</div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <button onClick={() => handleAdjust(rule, 'dropPct', -1)} title="Decrease buy margin by 1%" style={stepBtnStyle}>&minus;</button>
                <span style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)', minWidth: 38, textAlign: 'center' }}>-{rule.dropPct}%</span>
                <button onClick={() => handleAdjust(rule, 'dropPct', 1)} title="Increase buy margin by 1%" style={stepBtnStyle}>+</button>
              </div>
            </div>
            <div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 2 }}>Sell target</div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <button onClick={() => handleAdjust(rule, 'risePct', -1)} title="Decrease sell target by 1%" style={stepBtnStyle}>&minus;</button>
                <span style={{ fontSize: 14, fontWeight: 600, color: 'var(--green)', minWidth: 62, textAlign: 'center', whiteSpace: 'nowrap' }}>buy + {rule.risePct}%</span>
                <button onClick={() => handleAdjust(rule, 'risePct', 1)} title="Increase sell target by 1%" style={stepBtnStyle}>+</button>
              </div>
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
        );
      })}

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
Each rule shows the pair's price change over every window (1h/4h/6h/12h/24h) so you can see the trend at a glance; the 24h figure comes straight from Kraken's live ticker, the shorter windows are approximated from hourly candles. The boxed column is the one the rule actually triggers on — it turns <span style={{ color: AMBER }}>amber</span> within 2 points of the trigger and <span style={{ color: 'var(--red, #ef4444)' }}>red</span> within 0.5. Every 15 minutes each active rule checks that window; if the price has dropped more than the configured <strong>Drop %</strong>,
        a limit buy is placed for the <strong>Buy order total</strong> at 0.1% below the current price.
        When that buy fills, a limit sell is automatically placed at the fill price plus the <strong>Rise %</strong>.
        The <strong>rate limit</strong> caps how many buy orders a rule can place within its rolling window, so a pair that keeps dropping doesn't get bought over and over.
        The <strong>cooldown</strong> is an additional guard: no order is placed on a pair — from any rule — within that many hours of the last order on the same pair.
        <strong>Stop loss</strong> is optional and off by default: when on, if the price falls to or below the configured % of the buy price while the profit-target sell is still resting, that sell is cancelled and re-placed near the current (lower) price so it can actually fill and cut the loss, instead of sitting forever above a market that kept dropping.
        Turn on <strong>Dry run</strong> to see what a rule would do — via Pushover notifications and the order log — without placing real orders.
        The <strong>Emergency Stop</strong> button blocks every rule from placing new buy orders immediately; existing open positions keep being monitored and sold as normal until you turn it off.
        Before placing a real buy, the balance actually available in that pair's currency is checked so an order isn't placed against funds that are already tied up elsewhere.
        When a buy fills, the automatic sell is placed for the exact quantity Kraken says was bought; if Kraken rejects that (a rounding mismatch) the quantity is trimmed slightly and retried a few times.
      </div>
    </div>
  );
}
