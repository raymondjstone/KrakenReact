import { useState, useEffect, useCallback } from 'react';
import api from '../api/apiClient';

const EMPTY_SCHED = { targets: '', cronExpression: '0 9 * * 1', driftMinPct: 5, autoExecute: false, note: '', active: true };

const STORAGE_KEY = 'kraken_rebalance_targets';

function loadTargets() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : [{ asset: 'BTC', pct: 40 }, { asset: 'ETH', pct: 30 }, { asset: 'USD', pct: 30 }];
  } catch { return []; }
}

function saveTargets(targets) {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(targets));
}

export default function RebalancePage() {
  const [targets, setTargets] = useState(loadTargets);
  const [result, setResult] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  // Schedules
  const [schedules, setSchedules] = useState([]);
  const [schedForm, setSchedForm] = useState(EMPTY_SCHED);
  const [schedEditId, setSchedEditId] = useState(null);
  const [showSchedForm, setShowSchedForm] = useState(false);
  const [schedStatus, setSchedStatus] = useState('');

  const loadSchedules = useCallback(() => {
    api.get('/rebalanceschedules').then(r => setSchedules(r.data || [])).catch(() => {});
  }, []);

  useEffect(() => { loadSchedules(); }, [loadSchedules]);

  const totalTargetPct = targets.reduce((s, t) => s + (Number(t.pct) || 0), 0);

  const addRow = () => setTargets(t => [...t, { asset: '', pct: 0 }]);
  const removeRow = (i) => setTargets(t => t.filter((_, idx) => idx !== i));
  const updateRow = (i, field, val) => setTargets(t => t.map((r, idx) => idx === i ? { ...r, [field]: val } : r));

  const handleCalculate = useCallback(async () => {
    const valid = targets.filter(t => t.asset.trim() && Number(t.pct) > 0);
    if (valid.length === 0) return;
    if (Math.abs(totalTargetPct - 100) > 0.1) {
      setError(`Target allocations must sum to 100% (currently ${totalTargetPct.toFixed(1)}%)`);
      return;
    }
    setError('');
    setLoading(true);
    const targetsStr = valid.map(t => `${t.asset.trim()}:${t.pct}`).join(',');
    try {
      const r = await api.get(`/balances/rebalance?targets=${encodeURIComponent(targetsStr)}`);
      setResult(r.data);
      saveTargets(targets);
    } catch (e) {
      setError(e.response?.data || 'Failed to calculate');
    } finally {
      setLoading(false);
    }
  }, [targets, totalTargetPct]);

  const inputStyle = {
    padding: '6px 10px', border: '1px solid var(--border)', borderRadius: 4,
    background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13,
  };

  const actionColor = (action) => action === 'BUY' ? 'var(--green)' : action === 'SELL' ? 'var(--red)' : 'var(--text-muted)';

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <h2 style={{ marginTop: 0, color: 'var(--text-primary)' }}>Portfolio Rebalancing</h2>

      {/* Target allocation editor */}
      <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 20 }}>
        <div style={{ fontWeight: 600, marginBottom: 12, color: 'var(--text-primary)' }}>Target Allocations</div>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 120px 40px', gap: 8, alignItems: 'center', marginBottom: 8 }}>
          <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>Asset</div>
          <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>Target %</div>
          <div />
        </div>
        {targets.map((t, i) => (
          <div key={i} style={{ display: 'grid', gridTemplateColumns: '1fr 120px 40px', gap: 8, alignItems: 'center', marginBottom: 8 }}>
            <input
              value={t.asset}
              onChange={e => updateRow(i, 'asset', e.target.value.toUpperCase())}
              placeholder="e.g. BTC"
              style={{ ...inputStyle, width: '100%', boxSizing: 'border-box' }}
            />
            <input
              type="number" min={0} max={100} step={0.5}
              value={t.pct}
              onChange={e => updateRow(i, 'pct', parseFloat(e.target.value) || 0)}
              style={{ ...inputStyle, width: '100%', boxSizing: 'border-box' }}
            />
            <button onClick={() => removeRow(i)} style={{ padding: '4px 8px', border: '1px solid var(--red, #ef4444)', borderRadius: 4, background: 'transparent', color: 'var(--red, #ef4444)', cursor: 'pointer', fontSize: 14 }}>×</button>
          </div>
        ))}

        <div style={{ display: 'flex', gap: 12, alignItems: 'center', marginTop: 12 }}>
          <button onClick={addRow} style={{ padding: '5px 14px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', cursor: 'pointer', fontSize: 12 }}>
            + Add Asset
          </button>
          <span style={{ fontSize: 13, color: Math.abs(totalTargetPct - 100) < 0.1 ? 'var(--green)' : 'var(--red)' }}>
            Total: {totalTargetPct.toFixed(1)}%
          </span>
          <button
            onClick={handleCalculate}
            disabled={loading || Math.abs(totalTargetPct - 100) > 0.1}
            style={{ padding: '6px 18px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13, opacity: (loading || Math.abs(totalTargetPct - 100) > 0.1) ? 0.5 : 1, marginLeft: 'auto' }}
          >
            {loading ? 'Calculating…' : 'Calculate Rebalance'}
          </button>
        </div>
        {error && <div style={{ marginTop: 8, color: 'var(--red)', fontSize: 12 }}>{error}</div>}
      </div>

      {/* Results table */}
      {result && (
        <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 16 }}>
            <div style={{ fontWeight: 600, color: 'var(--text-primary)' }}>Rebalancing Plan</div>
            <div style={{ fontSize: 13, color: 'var(--text-muted)' }}>Portfolio: <strong style={{ color: 'var(--text-primary)' }}>${Number(result.totalUsd).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</strong></div>
          </div>

          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border)', color: 'var(--text-muted)', textAlign: 'left' }}>
                <th style={{ padding: '6px 8px' }}>Asset</th>
                <th style={{ padding: '6px 8px', textAlign: 'right' }}>Current %</th>
                <th style={{ padding: '6px 8px', textAlign: 'right' }}>Target %</th>
                <th style={{ padding: '6px 8px', textAlign: 'right' }}>Drift</th>
                <th style={{ padding: '6px 8px', textAlign: 'right' }}>Current $</th>
                <th style={{ padding: '6px 8px', textAlign: 'right' }}>Target $</th>
                <th style={{ padding: '6px 8px', textAlign: 'right' }}>Trade $</th>
                <th style={{ padding: '6px 8px', textAlign: 'right' }}>Trade Qty</th>
                <th style={{ padding: '6px 8px', textAlign: 'center' }}>Action</th>
              </tr>
            </thead>
            <tbody>
              {(result.rows || []).map(row => (
                <tr key={row.asset} style={{ borderBottom: '1px solid var(--border)' }}>
                  <td style={{ padding: '8px', fontWeight: 700, color: 'var(--text-primary)' }}>{row.asset}</td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-primary)' }}>{row.currentPct.toFixed(1)}%</td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-muted)' }}>{row.targetPct.toFixed(1)}%</td>
                  <td style={{ padding: '8px', textAlign: 'right', color: Math.abs(row.driftPct) > 5 ? 'var(--red)' : 'var(--text-muted)' }}>
                    {row.driftPct > 0 ? '+' : ''}{row.driftPct.toFixed(1)}%
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-primary)' }}>${row.currentUsd.toLocaleString(undefined, { maximumFractionDigits: 0 })}</td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-muted)' }}>${row.targetUsd.toLocaleString(undefined, { maximumFractionDigits: 0 })}</td>
                  <td style={{ padding: '8px', textAlign: 'right', fontWeight: 600, color: actionColor(row.action) }}>
                    {row.action !== 'HOLD' ? `${row.diffUsd > 0 ? '+' : ''}$${Math.abs(row.diffUsd).toLocaleString(undefined, { maximumFractionDigits: 0 })}` : '—'}
                  </td>
                  <td style={{ padding: '8px', textAlign: 'right', color: 'var(--text-muted)', fontSize: 12 }}>
                    {row.action !== 'HOLD' && row.diffQty !== 0 ? `${row.diffQty > 0 ? '+' : ''}${row.diffQty.toFixed(6)}` : '—'}
                  </td>
                  <td style={{ padding: '8px', textAlign: 'center' }}>
                    <span style={{ padding: '2px 8px', borderRadius: 3, fontSize: 11, fontWeight: 700, background: row.action === 'BUY' ? 'rgba(34,197,94,0.15)' : row.action === 'SELL' ? 'rgba(239,68,68,0.15)' : 'transparent', color: actionColor(row.action) }}>
                      {row.action}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <div style={{ marginTop: 16, fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.6 }}>
            <strong style={{ color: 'var(--text-primary)' }}>Drift</strong> = current allocation minus target. Large drift (&gt;5%) is highlighted in red.
            Trade quantities are indicative — actual orders may need adjustment for minimum sizes and fees.
            USD rows represent fiat and typically require buying/selling the other assets rather than USD itself.
          </div>
        </div>
      )}

      {/* Rebalance Schedules */}
      <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, marginTop: 24 }}>
        <div style={{ padding: '12px 16px', borderBottom: '1px solid var(--border)', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          <div style={{ fontWeight: 600, color: 'var(--text-primary)', fontSize: 14 }}>Automated Rebalance Schedules</div>
          <button onClick={() => { setSchedEditId(null); setSchedForm(EMPTY_SCHED); setShowSchedForm(true); }}
            style={{ padding: '5px 14px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 12 }}>
            + New Schedule
          </button>
        </div>

        {schedStatus && (
          <div style={{ padding: '8px 16px', background: schedStatus.includes('Error') ? 'rgba(239,68,68,0.1)' : 'rgba(34,197,94,0.1)', color: schedStatus.includes('Error') ? 'var(--red)' : 'var(--green)', fontSize: 12 }}>{schedStatus}</div>
        )}

        {showSchedForm && (
          <div style={{ padding: 20, borderBottom: '1px solid var(--border)', background: 'var(--bg-secondary)' }}>
            <div style={{ fontWeight: 600, marginBottom: 12, color: 'var(--text-primary)', fontSize: 13 }}>{schedEditId ? 'Edit Schedule' : 'New Schedule'}</div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 12, marginBottom: 12 }}>
              <div style={{ flex: '1 1 240px' }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 3 }}>Targets (e.g. BTC:40,ETH:30,USD:30)</div>
                <input value={schedForm.targets} onChange={e => setSchedForm(f => ({ ...f, targets: e.target.value }))}
                  placeholder="BTC:40,ETH:30,USD:30" style={{ ...inputStyle, width: '100%', boxSizing: 'border-box' }} />
              </div>
              <div>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 3 }}>Cron expression</div>
                <input value={schedForm.cronExpression} onChange={e => setSchedForm(f => ({ ...f, cronExpression: e.target.value }))}
                  placeholder="0 9 * * 1" style={{ ...inputStyle, width: 140 }} />
              </div>
              <div>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 3 }}>Min drift % to act</div>
                <input type="number" min={1} max={50} step={0.5} value={schedForm.driftMinPct}
                  onChange={e => setSchedForm(f => ({ ...f, driftMinPct: parseFloat(e.target.value) || 5 }))}
                  style={{ ...inputStyle, width: 80 }} />
              </div>
              <div style={{ flex: '1 1 160px' }}>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 3 }}>Note (optional)</div>
                <input value={schedForm.note} onChange={e => setSchedForm(f => ({ ...f, note: e.target.value }))}
                  style={{ ...inputStyle, width: '100%', boxSizing: 'border-box' }} />
              </div>
            </div>
            <div style={{ display: 'flex', gap: 16, marginBottom: 12 }}>
              <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13, cursor: 'pointer' }}>
                <input type="checkbox" checked={schedForm.autoExecute} onChange={e => setSchedForm(f => ({ ...f, autoExecute: e.target.checked }))} />
                <span style={{ color: 'var(--text-muted)' }}>Auto-execute orders (otherwise alert only)</span>
              </label>
              <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13, cursor: 'pointer' }}>
                <input type="checkbox" checked={schedForm.active} onChange={e => setSchedForm(f => ({ ...f, active: e.target.checked }))} />
                <span style={{ color: 'var(--text-muted)' }}>Active</span>
              </label>
            </div>
            <div style={{ display: 'flex', gap: 10 }}>
              <button onClick={() => {
                if (!schedForm.targets.trim()) return;
                const req = schedEditId
                  ? api.put(`/rebalanceschedules/${schedEditId}`, schedForm)
                  : api.post('/rebalanceschedules', schedForm);
                req.then(() => { loadSchedules(); setShowSchedForm(false); setSchedEditId(null); setSchedStatus(schedEditId ? 'Schedule updated' : 'Schedule created'); setTimeout(() => setSchedStatus(''), 3000); })
                  .catch(() => setSchedStatus('Error saving schedule'));
              }} style={{ padding: '6px 16px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13 }}>
                {schedEditId ? 'Update' : 'Create'}
              </button>
              <button onClick={() => { setShowSchedForm(false); setSchedEditId(null); setSchedForm(EMPTY_SCHED); }}
                style={{ padding: '6px 14px', background: 'none', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', color: 'var(--text-muted)', fontSize: 13 }}>
                Cancel
              </button>
            </div>
          </div>
        )}

        {schedules.length === 0 && !showSchedForm ? (
          <div style={{ padding: 24, textAlign: 'center', color: 'var(--text-muted)', fontSize: 13 }}>No schedules configured.</div>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ background: 'var(--bg-secondary)' }}>
                {['Targets', 'Cron', 'Drift Min', 'Mode', 'Note', 'Status', 'Last Run', ''].map(h => (
                  <th key={h} style={{ padding: '8px 12px', textAlign: 'left', color: 'var(--text-muted)', fontWeight: 500, fontSize: 11 }}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {schedules.map(s => (
                <tr key={s.id} style={{ borderTop: '1px solid var(--border)', opacity: s.active ? 1 : 0.6 }}>
                  <td style={{ padding: '8px 12px', color: 'var(--text-primary)', maxWidth: 200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{s.targets}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)', fontFamily: 'monospace', fontSize: 12 }}>{s.cronExpression}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)' }}>{s.driftMinPct}%</td>
                  <td style={{ padding: '8px 12px' }}>
                    <span style={{ color: s.autoExecute ? 'var(--green)' : 'var(--yellow)', fontSize: 11, fontWeight: 600 }}>
                      {s.autoExecute ? 'AUTO-EXECUTE' : 'ALERT ONLY'}
                    </span>
                  </td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)' }}>{s.note}</td>
                  <td style={{ padding: '8px 12px', fontSize: 11, color: 'var(--text-muted)', maxWidth: 160, overflow: 'hidden', textOverflow: 'ellipsis' }}>{s.lastRunResult || '—'}</td>
                  <td style={{ padding: '8px 12px', color: 'var(--text-muted)', fontSize: 11 }}>{s.lastRunAt ? new Date(s.lastRunAt).toLocaleString() : '—'}</td>
                  <td style={{ padding: '8px 12px', whiteSpace: 'nowrap' }}>
                    <button onClick={() => { setSchedEditId(s.id); setSchedForm({ targets: s.targets, cronExpression: s.cronExpression, driftMinPct: s.driftMinPct, autoExecute: s.autoExecute, note: s.note || '', active: s.active }); setShowSchedForm(true); }}
                      style={{ background: 'none', border: 'none', color: 'var(--blue)', cursor: 'pointer', fontSize: 13, padding: '0 8px 0 0' }}>Edit</button>
                    <button onClick={() => { api.post(`/rebalanceschedules/${s.id}/trigger`).then(() => setSchedStatus('Triggered')).catch(() => setSchedStatus('Error')); setTimeout(() => setSchedStatus(''), 3000); }}
                      style={{ background: 'none', border: 'none', color: 'var(--yellow)', cursor: 'pointer', fontSize: 13, padding: '0 8px 0 0' }}>Run</button>
                    <button onClick={() => { if (confirm('Delete schedule?')) api.delete(`/rebalanceschedules/${s.id}`).then(loadSchedules).catch(() => {}); }}
                      style={{ background: 'none', border: 'none', color: 'var(--red)', cursor: 'pointer', fontSize: 13, padding: 0 }}>Delete</button>
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
