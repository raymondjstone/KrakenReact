import { useState, useEffect, useCallback, useMemo } from 'react';
import api from '../api/apiClient';
import { formatPrice } from '../utils/formatters';

const DIRECTION_COLOR = { Up: 'var(--green)', Down: 'var(--red)', None: 'var(--text-muted)' };

// A trend's phase says where in its own life it stands, which matters more than its direction:
// an Up trend in its End phase is the one you sell into.
const PHASE_LABEL = {
  None: 'no trend',
  Start: 'starting — momentum still expanding',
  Middle: 'established',
  End: 'ending — momentum has been decaying',
};

function pct(value, decimals = 2) {
  if (value == null) return '—';
  return `${Number(value).toFixed(decimals)}%`;
}

function formatMoney(value, decimals = 2) {
  if (value == null) return '—';
  return Number(value).toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
}

function signedPct(value, decimals = 2) {
  if (value == null) return '—';
  const n = Number(value);
  return `${n >= 0 ? '+' : ''}${n.toFixed(decimals)}%`;
}

function shortDate(value) {
  if (!value) return '—';
  return new Date(value).toLocaleDateString(undefined, { year: '2-digit', month: 'short', day: 'numeric' });
}

function duration(minutes) {
  if (minutes == null) return '—';
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}

function Card({ title, subtitle, children, accent }) {
  return (
    <div style={{
      background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8,
      padding: '14px 16px', display: 'flex', flexDirection: 'column', gap: 10, minWidth: 0,
    }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
        <h3 style={{ margin: 0, fontSize: 13, letterSpacing: 0.4, textTransform: 'uppercase', color: accent || 'var(--text-secondary)' }}>{title}</h3>
        {subtitle && <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>{subtitle}</span>}
      </div>
      {children}
    </div>
  );
}

function Stat({ label, value, color, hint }) {
  return (
    <div style={{ minWidth: 0 }}>
      <div style={{ fontSize: 10, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: 0.4 }}>{label}</div>
      <div style={{ fontSize: 16, fontWeight: 700, color: color || 'var(--text-primary)', whiteSpace: 'nowrap' }}>{value}</div>
      {hint && <div style={{ fontSize: 10, color: 'var(--text-muted)' }}>{hint}</div>}
    </div>
  );
}

const statGrid = { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(110px, 1fr))', gap: '10px 16px' };
const th = { padding: '4px 8px', textAlign: 'right', whiteSpace: 'nowrap', fontWeight: 600, color: 'var(--text-muted)', fontSize: 11 };
const td = { padding: '3px 8px', textAlign: 'right', whiteSpace: 'nowrap', fontSize: 11.5 };
const tableStyle = { width: '100%', borderCollapse: 'collapse' };

export default function MarketAnalysisPage() {
  const [options, setOptions] = useState([]);
  const [symbol, setSymbol] = useState('');
  const [interval, setIntervalName] = useState('');
  const [minDrop, setMinDrop] = useState(10);
  const [minSurge, setMinSurge] = useState(10);
  const [reversal, setReversal] = useState(3);
  const [stake, setStake] = useState(1000);
  const [report, setReport] = useState(null);
  const [minuteCoverage, setMinuteCoverage] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let disposed = false;
    api.get('/analysis/symbols')
      .then(r => {
        if (disposed) return;
        const list = r.data || [];
        setOptions(list);
        if (list.length) {
          setSymbol(prev => prev || list[0].symbol);
          setIntervalName(prev => prev || list[0].interval);
        }
      })
      .catch(() => setError('Could not list analysable markets'));

    // Minute history is collected forwards only, so this is a progress report rather than a
    // snapshot of something already complete.
    api.get('/analysis/minute-coverage')
      .then(r => { if (!disposed) setMinuteCoverage(r.data); })
      .catch(() => {});

    return () => { disposed = true; };
  }, []);

  const intervalsForSymbol = useMemo(
    () => options.filter(o => o.symbol === symbol).map(o => o.interval),
    [options, symbol]
  );

  // Switching market can strand an interval that market has no candles for, so the interval
  // actually used is derived rather than stored — no effect needs to chase the selection.
  const effectiveInterval = intervalsForSymbol.includes(interval) ? interval : intervalsForSymbol[0] || '';

  const load = useCallback(() => {
    if (!symbol || !effectiveInterval) return;
    setLoading(true);
    const query = new URLSearchParams({
      symbol, interval: effectiveInterval,
      minimumDropPercent: String(minDrop),
      minimumSurgePercent: String(minSurge),
      pivotReversalAtrMultiple: String(reversal),
      stake: String(stake || 1000),
    });
    api.get(`/analysis/market?${query}`)
      .then(r => { setReport(r.data); setError(''); setLoading(false); })
      .catch(e => {
        setReport(null);
        setError(e.response?.data?.error || 'Analysis failed');
        setLoading(false);
      });
  }, [symbol, effectiveInterval, minDrop, minSurge, reversal, stake]);

  // Debounced: the detectors run over the whole series, so a request per keystroke is wasteful.
  useEffect(() => {
    const timer = setTimeout(load, 350);
    return () => clearTimeout(timer);
  }, [load]);

  const symbols = useMemo(() => [...new Set(options.map(o => o.symbol))], [options]);
  const selected = useMemo(
    () => options.find(o => o.symbol === symbol && o.interval === effectiveInterval),
    [options, symbol, effectiveInterval]
  );

  const inputStyle = {
    padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4,
    background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 12,
  };
  const labelStyle = { display: 'flex', gap: 6, alignItems: 'center', fontSize: 12, color: 'var(--text-muted)' };

  const trend = report?.trend;
  const plummets = report?.plummets;
  const surges = report?.surges;
  const levels = report?.levels;
  const backtest = report?.backtest;
  const entrySignals = report?.entrySignals;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: 'var(--bg-primary)' }}>
      <div style={{
        padding: '6px 12px', borderBottom: '1px solid var(--border)', background: 'var(--bg-secondary)',
        display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap',
      }}>
        <label style={labelStyle}>
          Market
          <select value={symbol} onChange={e => setSymbol(e.target.value)} style={{ ...inputStyle, minWidth: 130 }}>
            {symbols.map(s => <option key={s} value={s}>{s}</option>)}
          </select>
        </label>
        <label style={labelStyle}>
          Interval
          <select value={effectiveInterval} onChange={e => setIntervalName(e.target.value)} style={inputStyle}>
            {intervalsForSymbol.map(i => <option key={i} value={i}>{i}</option>)}
          </select>
        </label>
        <label style={labelStyle}>
          Fall ≥
          <input type="number" min="1" max="90" step="1" value={minDrop}
                 onChange={e => setMinDrop(e.target.value)} style={{ ...inputStyle, width: 56 }} />%
        </label>
        <label style={labelStyle}>
          Spike ≥
          <input type="number" min="1" max="500" step="1" value={minSurge}
                 onChange={e => setMinSurge(e.target.value)} style={{ ...inputStyle, width: 56 }} />%
        </label>
        <label style={labelStyle}>
          Pivot reversal
          <input type="number" min="0.5" max="20" step="0.5" value={reversal}
                 onChange={e => setReversal(e.target.value)} style={{ ...inputStyle, width: 56 }} />×ATR
        </label>
        <label style={labelStyle} title="The notional put into each simulated trade.">
          Stake
          <input type="number" min="1" step="100" value={stake}
                 onChange={e => setStake(e.target.value)} style={{ ...inputStyle, width: 72 }} />
        </label>
        <button onClick={load} style={{ ...inputStyle, cursor: 'pointer', background: 'var(--bg-card)' }}>Refresh</button>
        {loading && <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>Analysing…</span>}
        {error && <span style={{ fontSize: 12, color: 'var(--red)' }}>{error}</span>}
        {selected && !loading && (
          <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>
            {selected.candleCount.toLocaleString()} candles {'·'} to {shortDate(selected.lastOpenTime)}
          </span>
        )}
      </div>

      <div style={{ flex: 1, overflow: 'auto', padding: 12 }}>
        {!report && !loading && !error && (
          <div style={{ padding: 24, color: 'var(--text-muted)', fontSize: 13 }}>Choose a market to analyse.</div>
        )}

        {report?.status === 'insufficient_data' && (
          <div style={{ padding: 24, color: 'var(--text-muted)', fontSize: 13 }}>{report.message}</div>
        )}

        {report?.status === 'ok' && (
          <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fit, minmax(420px, 1fr))' }}>

            {/* ── Trend ───────────────────────────────────────────────── */}
            <Card
              title="Trend"
              accent={DIRECTION_COLOR[trend.direction]}
              subtitle={`${trend.direction === 'None' ? 'no direction' : trend.direction.toLowerCase()} · ${PHASE_LABEL[trend.phase]}`}
            >
              <div style={statGrid}>
                <Stat label="Last close" value={formatPrice(report.lastClose)} />
                <Stat
                  label="Strength"
                  value={pct(trend.strengthPercent, 0)}
                  color={trend.isNeutral ? 'var(--text-muted)' : DIRECTION_COLOR[trend.direction]}
                  hint={trend.isNeutral ? 'neutral' : `${trend.fallingBarCount} decaying bars`}
                />
                <Stat label="RSI" value={trend.rsi.toFixed(0)}
                      color={trend.rsi >= 70 ? 'var(--red)' : trend.rsi <= 30 ? 'var(--green)' : undefined} />
                <Stat label="ATR" value={pct(trend.atrPercent)} hint="per candle" />
                <Stat label="Regression" value={signedPct(trend.regressionSlopePercentPerBar, 3)}
                      hint={`R² ${trend.regressionRSquared.toFixed(2)}`}
                      color={trend.regressionSlopePercentPerBar >= 0 ? 'var(--green)' : 'var(--red)'} />
                <Stat label="Flip at" value={formatPrice(trend.directionChangePrice)}
                      hint={`${signedPct(trend.directionChangeDistancePercent)} away`} />
              </div>
              <p style={{ margin: 0, fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.5 }}>
                Flip price is the next close that would reverse the MACD histogram's sign. Strength is this
                market's own momentum range, not an absolute — 100% means the strongest it has been in the
                lookback, so it is not comparable across pairs.
              </p>
            </Card>

            {/* ── Falls ───────────────────────────────────────────────── */}
            <Card
              title="Falls & rebounds"
              accent="var(--red)"
              subtitle={`${plummets.eventCount} detected${plummets.pendingCount ? ` · ${plummets.pendingCount} still unfinished` : ''}`}
            >
              {plummets.eventCount === 0 ? (
                <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
                  No fall of {minDrop}% or more found in the stored history.
                </div>
              ) : (
                <>
                  <div style={statGrid}>
                    <Stat label="Recovered half" value={pct(plummets.recoveredHalfPercent, 0)}
                          color={plummets.recoveredHalfPercent >= 50 ? 'var(--green)' : 'var(--red)'}
                          hint="of settled falls" />
                    <Stat label="Average fall" value={pct(plummets.averageDropPercent)} />
                    <Stat label="Best rebound" value={pct(plummets.averageMaxReboundPercent, 0)} hint="average of peak recovery" />
                    {plummets.liveReboundProbability != null && (
                      <Stat
                        label="Latest fall"
                        value={pct(plummets.liveReboundProbability * 100, 0)}
                        color={plummets.judge?.isReliable
                          ? (plummets.liveReboundProbability >= 0.5 ? 'var(--green)' : 'var(--red)')
                          : 'var(--text-muted)'}
                        hint={plummets.judge?.isReliable ? 'modelled odds of half back' : 'odds of half back — unvalidated'}
                      />
                    )}
                  </div>

                  {plummets.judge && (
                    <div style={{ borderTop: '1px solid var(--border)', paddingTop: 8 }}>
                      <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 6 }}>
                        Fitted on {plummets.judge.trainCount} earlier falls, measured on the {plummets.judge.testCount} later ones —{' '}
                        <strong style={{
                          color: !plummets.judge.isReliable ? 'var(--text-muted)'
                            : plummets.judge.testOrdering >= 0.6 ? 'var(--green)'
                            : plummets.judge.testOrdering >= 0.55 ? 'var(--yellow)' : 'var(--text-muted)',
                        }}>
                          AUC {plummets.judge.testOrdering.toFixed(3)}
                        </strong>{' '}
                        (0.500 is knowing nothing). Base rate {pct(plummets.judge.testSuccessPercent, 0)} out of sample.
                      </div>
                      {!plummets.judge.isReliable && (
                        // An AUC computed against one or two examples of an outcome lands on 1.000
                        // by luck. Saying so is the difference between a model and a number.
                        <div style={{
                          fontSize: 10.5, lineHeight: 1.5, marginBottom: 6, padding: '5px 8px', borderRadius: 4,
                          color: 'var(--text-secondary)',
                          background: 'color-mix(in srgb, var(--yellow) 10%, transparent)',
                          border: '1px solid color-mix(in srgb, var(--yellow) 35%, var(--border))',
                        }}>
                          Treat that score as noise: the out-of-sample set holds only{' '}
                          {plummets.judge.testPositiveCount} recovered and {plummets.judge.testNegativeCount} not.
                          A handful of one outcome pins the AUC at an extreme regardless of whether the
                          model learned anything. It needs a market with far more falls before it means much.
                        </div>
                      )}
                      <table style={tableStyle}>
                        <tbody>
                          {plummets.judge.weights.map(w => (
                            <tr key={w.feature}>
                              <td style={{ ...td, textAlign: 'left', color: 'var(--text-secondary)' }}>{w.feature}</td>
                              <td style={{ ...td, width: 70, fontWeight: 600, color: w.weight >= 0 ? 'var(--green)' : 'var(--red)' }}>
                                {w.weight >= 0 ? '+' : ''}{w.weight.toFixed(3)}
                              </td>
                              <td style={{ ...td, width: '45%' }}>
                                <div style={{ position: 'relative', height: 6, background: 'var(--border)', borderRadius: 3 }}>
                                  <div style={{
                                    position: 'absolute', left: '50%', top: 0, height: 6, borderRadius: 3,
                                    width: `${Math.min(50, Math.abs(w.weight) * 60)}%`,
                                    transform: w.weight >= 0 ? 'none' : 'translateX(-100%)',
                                    background: w.weight >= 0 ? 'var(--green)' : 'var(--red)',
                                  }} />
                                </div>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                      <p style={{ margin: '6px 0 0', fontSize: 10.5, color: 'var(--text-muted)', lineHeight: 1.5 }}>
                        Weights are on standardised readings, so they are comparable with each other. A positive
                        weight means more of that reading made a half recovery more likely. Every reading was
                        knowable at the moment of the low.
                      </p>
                    </div>
                  )}

                  {plummets.decay.length > 0 && (
                    <div style={{ borderTop: '1px solid var(--border)', paddingTop: 8, overflowX: 'auto' }}>
                      <div style={{ fontSize: 11, color: 'var(--text-muted)', marginBottom: 4 }}>
                        Odds of a half recovery, given no bounce of this size yet after this long:
                      </div>
                      <table style={tableStyle}>
                        <thead>
                          <tr>
                            <th style={{ ...th, textAlign: 'left' }}>No bounce of</th>
                            {plummets.decayGridMinutes.map(m => <th key={m} style={th}>{duration(m)}</th>)}
                          </tr>
                        </thead>
                        <tbody>
                          {[...new Set(plummets.decay.map(c => c.onsetFraction))].map(onset => (
                            <tr key={onset}>
                              <td style={{ ...td, textAlign: 'left', color: 'var(--text-secondary)' }}>{(onset * 100).toFixed(0)}%</td>
                              {plummets.decayGridMinutes.map(m => {
                                const cell = plummets.decay.find(c => c.onsetFraction === onset && c.elapsedMinutes === m);
                                if (!cell || cell.pendingCount === 0) return <td key={m} style={{ ...td, color: 'var(--text-muted)' }}>—</td>;
                                return (
                                  <td key={m} style={{ ...td, color: cell.reachedHalfFraction >= 0.5 ? 'var(--green)' : 'var(--red)' }}>
                                    {(cell.reachedHalfFraction * 100).toFixed(0)}%
                                    <span style={{ color: 'var(--text-muted)', fontSize: 10 }}> /{cell.pendingCount}</span>
                                  </td>
                                );
                              })}
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}
            </Card>

            {/* ── Spikes ──────────────────────────────────────────────── */}
            <Card
              title="Spikes & reclaims"
              accent="var(--green)"
              subtitle={`${surges.eventCount} detected${surges.pendingCount ? ` · ${surges.pendingCount} still unfinished` : ''}`}
            >
              {surges.inProgress && (
                <div style={{
                  padding: '8px 10px', borderRadius: 6, marginBottom: 4,
                  background: 'color-mix(in srgb, var(--yellow) 12%, transparent)',
                  border: '1px solid color-mix(in srgb, var(--yellow) 40%, var(--border))',
                }}>
                  <strong style={{ color: 'var(--yellow)', fontSize: 12 }}>
                    {surges.inProgress.isCrashing ? 'Spike turning over now' : 'Spike running now'}
                  </strong>
                  <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', marginTop: 2 }}>
                    {pct(surges.inProgress.risePercent)} from {formatPrice(surges.inProgress.surgeLow)} over{' '}
                    {duration(surges.inProgress.durationMinutes)}, peak {formatPrice(surges.inProgress.surgePeak)} —
                    now {signedPct(surges.inProgress.distanceFromPeakPercent)} off it.
                  </div>
                </div>
              )}
              {surges.eventCount === 0 ? (
                <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
                  No spike of {minSurge}% or more that then broke down.
                </div>
              ) : (
                <div style={statGrid}>
                  <Stat label="Peak reclaimed" value={pct(surges.reclaimedPercent, 0)}
                        color={surges.reclaimedPercent >= 50 ? 'var(--green)' : 'var(--red)'}
                        hint="of settled spikes" />
                  <Stat label="Median time back" value={duration(surges.medianMinutesToReclaim)} />
                  <Stat label="Average rise" value={pct(surges.averageRisePercent)} />
                </div>
              )}
              {surges.recentEvents.length > 0 && (
                <div style={{ overflowX: 'auto' }}>
                  <table style={tableStyle}>
                    <thead>
                      <tr>
                        <th style={{ ...th, textAlign: 'left' }}>Peaked</th>
                        <th style={th}>Rise</th>
                        <th style={th}>Took</th>
                        <th style={th}>Retrace</th>
                        <th style={th}>Back?</th>
                      </tr>
                    </thead>
                    <tbody>
                      {surges.recentEvents.slice(0, 10).map((e, i) => (
                        <tr key={`${e.surgePeakTime}-${i}`} style={{ borderTop: '1px solid var(--border)' }}>
                          <td style={{ ...td, textAlign: 'left', color: 'var(--text-secondary)' }}>
                            {shortDate(e.surgePeakTime)}
                            {e.isWick && <span style={{ marginLeft: 4, fontSize: 9.5, color: 'var(--text-muted)' }}>wick</span>}
                          </td>
                          <td style={{ ...td, color: 'var(--green)' }}>{pct(e.risePercent, 0)}</td>
                          <td style={{ ...td, color: 'var(--text-muted)' }}>{duration(e.durationMinutes)}</td>
                          <td style={{ ...td, color: 'var(--red)' }}>{pct(e.retracePercent, 0)}</td>
                          <td style={{ ...td, color: e.isPending ? 'var(--text-muted)' : e.isReclaimed ? 'var(--green)' : 'var(--red)' }}>
                            {e.isPending ? 'open' : e.isReclaimed ? duration(e.minutesToReclaim) : 'never'}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </Card>

            {/* ── Levels ──────────────────────────────────────────────── */}
            <Card
              title="Levels"
              subtitle={`${levels.levelCount} built from pivots · ${levels.encounterCount} approaches replayed`}
            >
              {levels.byTouchTier.length > 0 && (
                <div style={statGrid}>
                  {levels.byTouchTier.map(t => (
                    <Stat key={t.tier} label={`${t.tier} prior touches`}
                          value={pct(t.rejectionRatePercent, 0)}
                          color={t.rejectionRatePercent >= 50 ? 'var(--green)' : 'var(--red)'}
                          hint={`held ${t.rejected}/${t.total}`} />
                  ))}
                </div>
              )}

              {levels.nearest.length > 0 && (
                <div style={{ overflowX: 'auto' }}>
                  <table style={tableStyle}>
                    <thead>
                      <tr>
                        <th style={{ ...th, textAlign: 'left' }}>Level</th>
                        <th style={th}>Away</th>
                        <th style={th}>Touches</th>
                        <th style={th}>Zone</th>
                        <th style={th}>Held</th>
                      </tr>
                    </thead>
                    <tbody>
                      {levels.nearest.map(l => {
                        const isResistance = l.sideNow === 'Resistance';
                        return (
                          <tr key={l.id} style={{ borderTop: '1px solid var(--border)' }}>
                            <td style={{ ...td, textAlign: 'left', color: isResistance ? 'var(--red)' : 'var(--green)', fontWeight: 600 }}>
                              {formatPrice(l.price)}
                              <span style={{ marginLeft: 6, fontSize: 9.5, fontWeight: 400, color: 'var(--text-muted)' }}>
                                {isResistance ? 'above' : 'below'}
                              </span>
                            </td>
                            <td style={{ ...td, color: 'var(--text-secondary)' }}>{signedPct(l.distancePercent)}</td>
                            <td style={td}>{l.touchCount}</td>
                            <td style={{ ...td, color: 'var(--text-muted)' }}>{pct(l.zoneWidthPercent)}</td>
                            <td style={{ ...td, color: l.encounters === 0 ? 'var(--text-muted)' : l.rejections / l.encounters >= 0.5 ? 'var(--green)' : 'var(--red)' }}>
                              {l.encounters === 0 ? '—' : `${l.rejections}/${l.encounters}`}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
              <p style={{ margin: 0, fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.5 }}>
                Levels are clusters of confirmed swing pivots, so a level is a zone rather than a line —
                the zone column is how wide. Held counts only approaches that had already resolved by the
                time price reached them, judged against the pivots known at that point rather than the
                finished chart.
              </p>
            </Card>

            {/* ── Minute-history progress, full width ─────────────────── */}
            {minuteCoverage && minuteCoverage.trackedPairs > 0 && (
              <div style={{ gridColumn: '1 / -1' }}>
                <Card
                  title="Minute history being collected"
                  subtitle={`${minuteCoverage.pairsReady} of ${minuteCoverage.trackedPairs} pairs deep enough to grade · needs ${minuteCoverage.barsNeededForGrading.toLocaleString()} bars (~30 days)`}
                >
                  <p style={{ margin: 0, fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.5 }}>
                    Kraken serves at most 720 one-minute bars, so this history cannot be backfilled — only
                    accumulated from the moment collection starts. Until a pair fills its bar, the
                    minute-resolution grading stays unavailable for it. Nothing is lost meanwhile; the rest of
                    this page runs on hourly and daily candles.
                  </p>
                  <div style={{ overflowX: 'auto' }}>
                    <table style={tableStyle}>
                      <thead>
                        <tr>
                          <th style={{ ...th, textAlign: 'left' }}>Pair</th>
                          <th style={th}>Bars</th>
                          <th style={th}>Span</th>
                          <th style={{ ...th, width: '45%' }}>Progress</th>
                        </tr>
                      </thead>
                      <tbody>
                        {minuteCoverage.coverage.map(c => (
                          <tr key={c.pair} style={{ borderTop: '1px solid var(--border)' }}>
                            <td style={{ ...td, textAlign: 'left', fontWeight: 600 }}>{c.pair}</td>
                            <td style={td}>{c.candleCount.toLocaleString()}</td>
                            <td style={{ ...td, color: 'var(--text-muted)' }}>
                              {c.candleCount === 0 ? 'not started' : `${c.spanDays}d`}
                            </td>
                            <td style={td}>
                              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                                <div style={{ flex: 1, height: 7, background: 'var(--border)', borderRadius: 4, overflow: 'hidden' }}>
                                  <div style={{
                                    width: `${c.percentComplete}%`, height: '100%',
                                    background: c.readyForGrading ? 'var(--green)' : 'var(--yellow)',
                                  }} />
                                </div>
                                <span style={{ minWidth: 42, textAlign: 'right', color: c.readyForGrading ? 'var(--green)' : 'var(--text-muted)' }}>
                                  {c.percentComplete}%
                                </span>
                              </div>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </Card>
              </div>
            )}

            {/* ── Strategy backtest, full width ───────────────────────── */}
            {backtest && (
              <div style={{ gridColumn: '1 / -1' }}>
                <Card
                  title="What a rule would have done"
                  subtitle={`${backtest.settledEventCount} of ${backtest.eventCount} falls had a full holding window · ${formatMoney(backtest.stake, 0)} a trade · ${pct(backtest.feePercentPerSide)} fee a side + ${pct(backtest.spreadAllowancePercent)} spread`}
                >
                  {backtest.caveat && (
                    <div style={{
                      fontSize: 11, lineHeight: 1.55, padding: '7px 10px', borderRadius: 5,
                      color: 'var(--text-secondary)',
                      background: 'color-mix(in srgb, var(--yellow) 10%, transparent)',
                      border: '1px solid color-mix(in srgb, var(--yellow) 35%, var(--border))',
                    }}>{backtest.caveat}</div>
                  )}

                  {backtest.settledEventCount > 0 && (
                    <div style={{ overflowX: 'auto' }}>
                      <table style={tableStyle}>
                        <thead>
                          <tr>
                            <th style={{ ...th, textAlign: 'left' }}>Rule</th>
                            <th style={th}>Taken</th>
                            <th style={th}>Won</th>
                            <th style={th}>Net</th>
                            <th style={th}>On peak capital</th>
                            <th style={th}>1st half</th>
                            <th style={th}>2nd half</th>
                            <th style={th}>Held</th>
                            <th style={th}>Max open</th>
                          </tr>
                        </thead>
                        <tbody>
                          {backtest.strategies.map(st => {
                            // A rule that made everything early and nothing late has not found an
                            // edge, so the two halves are shown side by side rather than summed away.
                            const faded = st.filled === 0;
                            const inconsistent = st.netProfit > 0 && (st.firstHalfNetProfit <= 0 || st.secondHalfNetProfit <= 0);
                            return (
                              <tr key={st.name} style={{ borderTop: '1px solid var(--border)', opacity: faded ? 0.5 : 1 }}>
                                <td style={{ ...td, textAlign: 'left' }} title={st.description}>
                                  <strong style={{ color: 'var(--text-primary)' }}>{st.name}</strong>
                                  {inconsistent && (
                                    <span style={{ marginLeft: 6, fontSize: 9.5, color: 'var(--yellow)' }} title="All the profit came from one half of the period.">
                                      one-sided
                                    </span>
                                  )}
                                </td>
                                <td style={{ ...td, color: 'var(--text-muted)' }}>{st.filled}/{st.signals}</td>
                                <td style={td}>{st.filled === 0 ? '—' : pct(st.winRatePercent, 0)}</td>
                                <td style={{ ...td, fontWeight: 700, color: st.netProfit > 0 ? 'var(--green)' : st.netProfit < 0 ? 'var(--red)' : 'var(--text-muted)' }}>
                                  {st.netProfit >= 0 ? '+' : '-'}{formatMoney(Math.abs(st.netProfit))}
                                </td>
                                <td style={{ ...td, color: st.netReturnOnPeakCapitalPercent >= 0 ? 'var(--green)' : 'var(--red)' }}>
                                  {st.filled === 0 ? '—' : signedPct(st.netReturnOnPeakCapitalPercent)}
                                </td>
                                <td style={{ ...td, fontSize: 11, color: st.firstHalfNetProfit >= 0 ? 'var(--green)' : 'var(--red)' }}>
                                  {st.filled === 0 ? '—' : formatMoney(st.firstHalfNetProfit, 0)}
                                </td>
                                <td style={{ ...td, fontSize: 11, color: st.secondHalfNetProfit >= 0 ? 'var(--green)' : 'var(--red)' }}>
                                  {st.filled === 0 ? '—' : formatMoney(st.secondHalfNetProfit, 0)}
                                </td>
                                <td style={{ ...td, color: 'var(--text-muted)' }}>{st.filled === 0 ? '—' : `${st.medianHeldHours}h`}</td>
                                <td style={{ ...td, color: 'var(--text-muted)' }}>{st.maxConcurrent || '—'}</td>
                              </tr>
                            );
                          })}
                        </tbody>
                      </table>
                    </div>
                  )}

                  <p style={{ margin: 0, fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.5 }}>
                    Each rule is replayed bar by bar over this market&apos;s own falls, paying the fee on both
                    sides and an allowance for the spread. A bar that gaps through a stop fills at its open, not
                    at the stop, and a bar that touches both the stop and the target is resolved as a loss —
                    the order within a bar is unknowable, so it is read the way that costs the trade.
                    A resting limit that never filled counts as no trade, not a win. Returns are measured
                    against peak capital actually committed, not the sum of the stakes.
                    None of this is a forecast: it is what the rule would have done, on data it has now seen.
                  </p>
                </Card>
              </div>
            )}

            {/* ── Entry detectors, full width ─────────────────────────── */}
            {entrySignals && (
              <div style={{ gridColumn: '1 / -1' }}>
                <Card
                  title="Where a base detector would have bought"
                  subtitle={`each family replayed as non-overlapping trades · +${pct(entrySignals.targetPercent)} target, −${pct(entrySignals.stopPercent)} stop, ${entrySignals.scoredWindowBars} bars max · ${formatMoney(entrySignals.stake, 0)} a trade`}
                >
                  {entrySignals.caveat && (
                    <div style={{
                      fontSize: 11, lineHeight: 1.55, padding: '7px 10px', borderRadius: 5,
                      color: 'var(--text-secondary)',
                      background: 'color-mix(in srgb, var(--yellow) 10%, transparent)',
                      border: '1px solid color-mix(in srgb, var(--yellow) 35%, var(--border))',
                    }}>{entrySignals.caveat}</div>
                  )}

                  <div style={{ overflowX: 'auto' }}>
                    <table style={tableStyle}>
                      <thead>
                        <tr>
                          <th style={{ ...th, textAlign: 'left' }}>Family</th>
                          <th style={th}>Scored</th>
                          <th style={th}>Won</th>
                          <th style={th}>Avg fwd</th>
                          <th style={th}>Med best</th>
                          <th style={th}>Med worst</th>
                          <th style={th}>Net</th>
                          <th style={th}>On stake</th>
                          <th style={th}>1st half</th>
                          <th style={th}>2nd half</th>
                          <th style={th}>Lag</th>
                        </tr>
                      </thead>
                      <tbody>
                        {entrySignals.families.map(f => {
                          const faded = f.scored === 0;
                          const inconsistent = f.netProfit > 0 && (f.firstHalfNetProfit <= 0 || f.secondHalfNetProfit <= 0);
                          return (
                            <tr key={f.name} style={{ borderTop: '1px solid var(--border)', opacity: faded ? 0.5 : 1 }}>
                              <td style={{ ...td, textAlign: 'left' }} title={f.description}>
                                <strong style={{ color: 'var(--text-primary)' }}>{f.name}</strong>
                                {inconsistent && (
                                  <span style={{ marginLeft: 6, fontSize: 9.5, color: 'var(--yellow)' }} title="All the profit came from one half of the period.">
                                    one-sided
                                  </span>
                                )}
                              </td>
                              <td style={{ ...td, color: 'var(--text-muted)' }}>{f.scored}/{f.signals}</td>
                              <td style={td}>{f.scored === 0 ? '—' : pct(f.winRatePercent, 0)}</td>
                              <td style={{ ...td, color: f.averageForwardReturnPercent >= 0 ? 'var(--green)' : 'var(--red)' }}>
                                {f.scored === 0 ? '—' : signedPct(f.averageForwardReturnPercent)}
                              </td>
                              <td style={{ ...td, color: 'var(--text-muted)' }}>{f.scored === 0 ? '—' : signedPct(f.medianMaxFavourablePercent)}</td>
                              <td style={{ ...td, color: 'var(--text-muted)' }}>{f.scored === 0 ? '—' : signedPct(f.medianMaxAdversePercent)}</td>
                              <td style={{ ...td, fontWeight: 700, color: f.netProfit > 0 ? 'var(--green)' : f.netProfit < 0 ? 'var(--red)' : 'var(--text-muted)' }}>
                                {f.netProfit >= 0 ? '+' : '-'}{formatMoney(Math.abs(f.netProfit))}
                              </td>
                              <td style={{ ...td, color: f.netReturnOnStakePercent >= 0 ? 'var(--green)' : 'var(--red)' }}>
                                {f.scored === 0 ? '—' : signedPct(f.netReturnOnStakePercent)}
                              </td>
                              <td style={{ ...td, fontSize: 11, color: f.firstHalfNetProfit >= 0 ? 'var(--green)' : 'var(--red)' }}>
                                {f.scored === 0 ? '—' : formatMoney(f.firstHalfNetProfit, 0)}
                              </td>
                              <td style={{ ...td, fontSize: 11, color: f.secondHalfNetProfit >= 0 ? 'var(--green)' : 'var(--red)' }}>
                                {f.scored === 0 ? '—' : formatMoney(f.secondHalfNetProfit, 0)}
                              </td>
                              <td style={{ ...td, color: 'var(--text-muted)' }}>{f.scored === 0 ? '—' : `${f.medianConfirmationLagBars}b`}</td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>

                  <p style={{ margin: 0, fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.5 }}>
                    Ported from the KrakenPlusPlus base-detector study. Each family names a moment a fall could be
                    bought — a drawdown, a momentum crossing, a band reclaim, a volume climax, a curve turning, a
                    swept level — and every one is measured only from candles up to and including the bar it fires
                    on, so the same reading can be taken live. Trades are replayed one at a time per family: a bar
                    that gaps through the stop fills at its open, a bar touching both stop and target is read as a
                    loss, and a signal with no full window ahead of it is left unscored. &quot;Lag&quot; is the median
                    bars between the low and the confirmation — the distance the entry pays.
                  </p>
                </Card>
              </div>
            )}

            {/* ── Recent falls, full width ────────────────────────────── */}
            {plummets.recentEvents.length > 0 && (
              <div style={{ gridColumn: '1 / -1' }}>
                <Card title="Recent falls" accent="var(--red)" subtitle="newest first">
                  <div style={{ overflowX: 'auto' }}>
                    <table style={tableStyle}>
                      <thead>
                        <tr>
                          <th style={{ ...th, textAlign: 'left' }}>Low</th>
                          <th style={th}>From</th>
                          <th style={th}>To</th>
                          <th style={th}>Fall</th>
                          <th style={th}>Needed</th>
                          <th style={th}>Volume</th>
                          <th style={th}>Best back</th>
                          <th style={th}>To 10%</th>
                          <th style={th}>To 25%</th>
                          <th style={th}>To 50%</th>
                          <th style={th}>To 100%</th>
                        </tr>
                      </thead>
                      <tbody>
                        {plummets.recentEvents.map((e, i) => (
                          <tr key={`${e.eventLowTime}-${i}`} style={{ borderTop: '1px solid var(--border)' }}>
                            <td style={{ ...td, textAlign: 'left', color: 'var(--text-secondary)' }}>
                              {shortDate(e.eventLowTime)}
                              {e.isWick && <span style={{ marginLeft: 4, fontSize: 9.5, color: 'var(--text-muted)' }}>wick</span>}
                              {e.isPending && <span style={{ marginLeft: 4, fontSize: 9.5, color: 'var(--yellow)' }}>open</span>}
                            </td>
                            <td style={td}>{formatPrice(e.referenceHigh)}</td>
                            <td style={td}>{formatPrice(e.eventLow)}</td>
                            <td style={{ ...td, color: 'var(--red)', fontWeight: 600 }}>{pct(e.dropPercent, 1)}</td>
                            <td style={{ ...td, color: 'var(--text-muted)' }}>{pct(e.requiredDropPercent, 1)}</td>
                            <td style={{ ...td, color: 'var(--text-muted)' }}>{e.volumeRatio.toFixed(1)}×</td>
                            <td style={{ ...td, color: e.maxReboundPercent >= 50 ? 'var(--green)' : 'var(--red)', fontWeight: 600 }}>
                              {pct(e.maxReboundPercent, 0)}
                            </td>
                            <td style={{ ...td, color: 'var(--text-muted)' }}>{duration(e.minutesToTenPercent)}</td>
                            <td style={{ ...td, color: 'var(--text-muted)' }}>{duration(e.minutesToQuarter)}</td>
                            <td style={{ ...td, color: 'var(--text-muted)' }}>{duration(e.minutesToHalf)}</td>
                            <td style={{ ...td, color: 'var(--text-muted)' }}>{duration(e.minutesToFull)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <p style={{ margin: 0, fontSize: 11, color: 'var(--text-muted)', lineHeight: 1.5 }}>
                    "Needed" is the fall this market had to make before it counted as a plummet at all — the
                    larger of your threshold and a multiple of how much it was already swinging, so a lively
                    market does not report one every day. "Open" marks a fall whose rebound window runs past
                    the end of the stored candles: its outcome is not settled and it is excluded from the
                    fitted model and the rates above.
                  </p>
                </Card>
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
