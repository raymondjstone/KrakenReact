import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';

const INTERVAL_LABELS = {
  OneMinute: '1m', FiveMinutes: '5m', FifteenMinutes: '15m',
  ThirtyMinutes: '30m', OneHour: '1h', FourHour: '4h', OneDay: '1d',
};

const INTERVAL_OPTIONS = [
  { value: 'OneMinute',      label: '1 Minute' },
  { value: 'FiveMinutes',    label: '5 Minutes' },
  { value: 'FifteenMinutes', label: '15 Minutes' },
  { value: 'ThirtyMinutes',  label: '30 Minutes' },
  { value: 'OneHour',        label: '1 Hour (recommended)' },
  { value: 'FourHour',       label: '4 Hours' },
  { value: 'OneDay',         label: '1 Day' },
];

export default function PredictionPage({ onSymbolClick }) {
  const [results, setResults] = useState([]);
  const [loading, setLoading] = useState(true);
  const [triggerStatus, setTriggerStatus] = useState('');
  const [showConfig, setShowConfig] = useState(false);
  const [symbols, setSymbols] = useState('');
  const [interval, setInterval] = useState('OneHour');
  const [mode, setMode] = useState('specific');
  const [currency, setCurrency] = useState('USD');
  const [availableCurrencies, setAvailableCurrencies] = useState([]);
  const [configStatus, setConfigStatus] = useState('');
  const [sortBy, setSortBy] = useState(() => localStorage.getItem('predictions_sortBy') || 'symbol');
  const triggerTimerRef = useRef(null);
  const configTimerRef = useRef(null);

  const fetchResults = useCallback(() => {
    api.get('/predictions')
      .then(r => { setResults(r.data); setLoading(false); })
      .catch(() => setLoading(false));
  }, []);

  useEffect(() => {
    fetchResults();
    api.get('/predictions/settings')
      .then(r => {
        setSymbols(r.data.symbols || 'XBT/USD,ETH/USD,SOL/USD');
        setInterval(r.data.interval || 'OneHour');
        setMode(r.data.mode || 'specific');
        setCurrency(r.data.currency || 'USD');
        setAvailableCurrencies(r.data.availableCurrencies || []);
      })
      .catch(() => {});

    const conn = getConnection();
    conn.on('PredictionsUpdated', fetchResults);
    return () => {
      conn.off('PredictionsUpdated', fetchResults);
      if (triggerTimerRef.current) clearTimeout(triggerTimerRef.current);
      if (configTimerRef.current) clearTimeout(configTimerRef.current);
    };
  }, [fetchResults]);

  const handleTriggerNow = () => {
    setTriggerStatus('Enqueued — results will update when complete...');
    api.post('/predictions/trigger')
      .then(() => {
        if (triggerTimerRef.current) clearTimeout(triggerTimerRef.current);
        triggerTimerRef.current = setTimeout(() => setTriggerStatus(''), 10000);
      })
      .catch(() => {
        setTriggerStatus('Error triggering job');
        if (triggerTimerRef.current) clearTimeout(triggerTimerRef.current);
        triggerTimerRef.current = setTimeout(() => setTriggerStatus(''), 3000);
      });
  };

  const handleSaveConfig = () => {
    setConfigStatus('Saving...');
    api.post('/settings', {
      predictionSymbols: symbols,
      predictionInterval: interval,
      predictionMode: mode,
      predictionCurrency: currency,
    })
      .then(() => {
        setConfigStatus('Saved');
        if (configTimerRef.current) clearTimeout(configTimerRef.current);
        configTimerRef.current = setTimeout(() => setConfigStatus(''), 3000);
      })
      .catch(() => {
        setConfigStatus('Error saving');
        if (configTimerRef.current) clearTimeout(configTimerRef.current);
        configTimerRef.current = setTimeout(() => setConfigStatus(''), 3000);
      });
  };

  const symbolList = mode === 'specific'
    ? symbols.split(',').map(s => s.trim()).filter(Boolean)
    : results.map(r => r.symbol);

  const headerSummary = mode === 'all'
    ? `All active */${currency} pairs`
    : mode === 'existing'
    ? `${results.length} existing card${results.length !== 1 ? 's' : ''}`
    : `${symbolList.length} symbol${symbolList.length !== 1 ? 's' : ''}`;

  const sortedResults = useMemo(() => {
    const statusRank = { success: 0, insufficient_data: 1, error: 2 };
    const arr = [...results];
    switch (sortBy) {
      case 'confidence':
        return arr.sort((a, b) => (b.probability ?? 0) - (a.probability ?? 0));
      case 'accuracy':
        return arr.sort((a, b) => (b.modelAccuracy ?? 0) - (a.modelAccuracy ?? 0));
      case 'auc':
        return arr.sort((a, b) => (b.modelAuc ?? 0) - (a.modelAuc ?? 0));
      case 'direction':
        return arr.sort((a, b) => {
          if (a.predictedUp !== b.predictedUp) return a.predictedUp ? -1 : 1;
          return (b.probability ?? 0) - (a.probability ?? 0);
        });
      case 'computed':
        return arr.sort((a, b) => new Date(b.computedAt) - new Date(a.computedAt));
      case 'status':
        return arr.sort((a, b) => {
          const sr = (statusRank[a.status] ?? 9) - (statusRank[b.status] ?? 9);
          return sr !== 0 ? sr : a.symbol.localeCompare(b.symbol);
        });
      default:
        return arr.sort((a, b) => a.symbol.localeCompare(b.symbol));
    }
  }, [results, sortBy]);

  const inputStyle = { padding: '7px 10px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 13 };
  const modeBtn = (val, label) => (
    <button
      onClick={() => setMode(val)}
      style={{
        padding: '6px 16px', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 13, fontWeight: mode === val ? 600 : 400,
        background: mode === val ? 'var(--green)' : 'var(--bg-primary)',
        color: mode === val ? 'white' : 'var(--text-primary)',
      }}>
      {label}
    </button>
  );

  return (
    <div style={{ padding: 24, height: '100%', overflow: 'auto', background: 'var(--bg-primary)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 20, flexWrap: 'wrap' }}>
        <h2 style={{ margin: 0, color: 'var(--text-primary)' }}>ML Predictions</h2>
        <button
          onClick={handleTriggerNow}
          style={{ padding: '8px 18px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 }}>
          Run Now
        </button>
        <button
          onClick={fetchResults}
          style={{ padding: '8px 14px', background: 'var(--bg-card)', color: 'var(--text-primary)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer' }}>
          Refresh
        </button>
        <button
          onClick={() => setShowConfig(v => !v)}
          style={{ padding: '8px 14px', background: showConfig ? 'var(--bg-card)' : 'transparent', color: 'var(--text-primary)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer' }}>
          ⚙ Configure
        </button>
        {triggerStatus && (
          <span style={{ fontSize: 13, color: triggerStatus.includes('Error') ? 'var(--red)' : 'var(--green)' }}>
            {triggerStatus}
          </span>
        )}
        <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 8 }}>
          <label style={{ fontSize: 12, color: 'var(--text-muted)', whiteSpace: 'nowrap' }}>Sort by</label>
          <select
            value={sortBy}
            onChange={e => { setSortBy(e.target.value); localStorage.setItem('predictions_sortBy', e.target.value); }}
            style={{ padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 12, cursor: 'pointer' }}>
            <option value="symbol">Symbol A→Z</option>
            <option value="confidence">Confidence ↓</option>
            <option value="accuracy">Model accuracy ↓</option>
            <option value="auc">AUC-ROC ↓</option>
            <option value="direction">Direction (UP first)</option>
            <option value="computed">Most recent first</option>
            <option value="status">Status</option>
          </select>
          <span style={{ fontSize: 12, color: 'var(--text-muted)', whiteSpace: 'nowrap' }}>
            {headerSummary} &middot; {INTERVAL_LABELS[interval] || interval}
          </span>
        </div>
      </div>

      {showConfig && (
        <div style={{ background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 24 }}>
          <div style={{ fontWeight: 600, marginBottom: 16, color: 'var(--text-primary)' }}>Prediction Configuration</div>

          <div style={{ marginBottom: 16 }}>
            <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 8 }}>Symbol mode</div>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              {modeBtn('specific', 'Specific symbols')}
              {modeBtn('all', 'All active pairs')}
              {modeBtn('existing', 'Existing cards')}
            </div>
          </div>

          <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap', alignItems: 'flex-end' }}>
            {mode === 'specific' && (
              <div style={{ flex: '1 1 300px' }}>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 6 }}>Symbols (comma-separated)</div>
                <input
                  type="text"
                  value={symbols}
                  onChange={e => setSymbols(e.target.value)}
                  placeholder="XBT/USD,ETH/USD,SOL/USD"
                  style={{ ...inputStyle, width: '100%', boxSizing: 'border-box' }}
                />
                <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 4 }}>
                  Use Kraken pair format, e.g. XBT/USD, ETH/USD, SOL/USD, ADA/USD
                </div>
              </div>
            )}

            {mode === 'all' && (
              <div>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 6 }}>Quote currency</div>
                <select
                  value={currency}
                  onChange={e => setCurrency(e.target.value)}
                  style={{ ...inputStyle, minWidth: 120 }}>
                  {availableCurrencies.length > 0
                    ? availableCurrencies.map(c => <option key={c} value={c}>{c}</option>)
                    : ['USD', 'GBP', 'EUR', 'USDT', 'USDC'].map(c => <option key={c} value={c}>{c}</option>)
                  }
                </select>
                <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 4 }}>
                  Runs predictions for every active */{currency} pair in your Symbols table
                </div>
              </div>
            )}

            <div>
              <div style={{ fontSize: 12, color: 'var(--text-muted)', marginBottom: 6 }}>Kline interval</div>
              <select
                value={interval}
                onChange={e => setInterval(e.target.value)}
                style={{ ...inputStyle, minWidth: 180 }}>
                {INTERVAL_OPTIONS.map(o => (
                  <option key={o.value} value={o.value}>{o.label}</option>
                ))}
              </select>
            </div>

            <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
              <button
                onClick={handleSaveConfig}
                style={{ padding: '8px 18px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 }}>
                Save
              </button>
              {configStatus && (
                <span style={{ fontSize: 13, color: configStatus.includes('Error') ? 'var(--red)' : 'var(--green)' }}>
                  {configStatus}
                </span>
              )}
            </div>
          </div>

          <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 12 }}>
            {mode === 'all'
              ? `"All active pairs" will run predictions for every non-delisted */${currency} pair currently in your Kraken Symbols table. This can take a while for large universes — 1h interval is recommended.`
              : mode === 'existing'
              ? '"Existing cards" re-runs predictions only for symbols that already have a prediction card. Useful for keeping your current set fresh without managing a manual list.'
              : 'Changes take effect on the next run. Click Run Now after saving to see results immediately.'}
          </div>
        </div>
      )}

      {loading && (
        <div style={{ color: 'var(--text-muted)', textAlign: 'center', padding: 48 }}>Loading predictions...</div>
      )}

      {!loading && results.length === 0 && (
        <div style={{ color: 'var(--text-muted)', textAlign: 'center', padding: 48 }}>
          No predictions yet. Click <strong>Run Now</strong> to run the first analysis.
        </div>
      )}

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(360px, 1fr))', gap: 20 }}>
        {sortedResults.map(r => (
          <PredictionCard
            key={r.symbol}
            result={r}
            onSymbolClick={onSymbolClick}
            onRefreshDone={fetchResults}
            onDelete={() => {
              api.delete(`/predictions?symbol=${encodeURIComponent(r.symbol)}`)
                .then(() => setResults(prev => prev.filter(x => x.symbol !== r.symbol)))
                .catch(() => {});
            }}
          />
        ))}
      </div>

      <div style={{ marginTop: 32, padding: 16, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 13, color: 'var(--text-muted)', lineHeight: 1.6 }}>
        <strong style={{ color: 'var(--text-primary)' }}>How it works</strong>
        <br />
        Fetches OHLCV kline data from Kraken and computes 23 technical features: RSI, MACD, ATR, Bollinger Bands,
        volume percentile, OBV, ADX(14), ROC(10), VWAP ratio, time-of-week seasonality, and BTC market context (zeroed for XBT/USD itself).
        Predictions are generated for 1, 3, and 6 candles ahead.
        Two models (FastTree gradient boosted trees and logistic regression) are evaluated on a strict 70/30 chronological split
        plus walk-forward cross-validation — both models are assessed in each fold.
        A consensus signal shows when multiple horizons agree on direction.
        Accuracy above ~55% in a noisy market is genuinely useful — the benchmark columns show what random-chance looks like for each asset.
      </div>
    </div>
  );
}

// Parse a server DateTime string as UTC regardless of whether the 'Z' suffix is present
function parseUtc(s) {
  if (!s) return new Date(0);
  return new Date(/[Zz]|[+-]\d{2}:\d{2}$/.test(s) ? s : s + 'Z');
}

function ageLabel(computedAt) {
  const mins = Math.floor((Date.now() - parseUtc(computedAt)) / 60000);
  if (mins < 1)  return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  const rem = mins % 60;
  return rem > 0 ? `${hrs}h ${rem}m ago` : `${hrs}h ago`;
}

function intervalToMs(interval) {
  switch (interval) {
    case 'OneMinute':      return 60_000;
    case 'FiveMinutes':    return 5  * 60_000;
    case 'FifteenMinutes': return 15 * 60_000;
    case 'ThirtyMinutes':  return 30 * 60_000;
    case 'FourHour':       return 4  * 60 * 60_000;
    case 'OneDay':         return 24 * 60 * 60_000;
    default:               return 60 * 60_000;
  }
}

function predictionExpiryMs(interval, horizonCandles = 1) {
  return intervalToMs(interval) * horizonCandles * 2;
}

function PredictionCard({ result, onSymbolClick, onRefreshDone, onDelete }) {
  const [refreshing, setRefreshing] = useState(false);
  const [, forceAge] = useState(0);
  const pollRef = useRef(null);
  const originalComputedAtRef = useRef(null);

  const isSuccess = result.status === 'success';
  const hasError  = result.status === 'error';
  const isExpired = !refreshing && (Date.now() - parseUtc(result.computedAt)) > predictionExpiryMs(result.interval, 1);
  const pct = v => `${((v ?? 0) * 100).toFixed(1)}%`;
  const intervalLabel = INTERVAL_LABELS[result.interval] || result.interval;

  // Re-render every minute so age label and expired flag stay current
  useEffect(() => {
    const t = setInterval(() => forceAge(n => n + 1), 60000);
    return () => clearInterval(t);
  }, []);

  // Single badge — priority: refreshing > expired > success/error/insufficient data
  const badgeLabel = refreshing ? 'updating' : isExpired ? 'expired'
    : isSuccess ? 'success' : hasError ? 'error' : 'insufficient data';
  const badgeColor = refreshing ? 'var(--text-muted)' : isExpired ? '#b45309'
    : isSuccess ? 'var(--green)' : hasError ? 'var(--red)' : 'var(--text-muted)';

  // Detect when the parent re-fetches and this card's computedAt changes (stops polling)
  useEffect(() => {
    if (refreshing && originalComputedAtRef.current && result.computedAt !== originalComputedAtRef.current) {
      clearInterval(pollRef.current);
      pollRef.current = null;
      setRefreshing(false);
    }
  }, [result.computedAt, refreshing]);

  useEffect(() => () => clearInterval(pollRef.current), []);

  const handleRefresh = (e) => {
    e.stopPropagation();
    originalComputedAtRef.current = result.computedAt;
    setRefreshing(true);
    api.post(`/predictions/trigger/single?symbol=${encodeURIComponent(result.symbol)}`)
      .then(() => {
        pollRef.current = setInterval(() => onRefreshDone?.(), 3000);
        setTimeout(() => {
          if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
          setRefreshing(false);
        }, 120000);
      })
      .catch(() => setRefreshing(false));
  };

  const iconBtn = (title, content, onClick, disabled) => (
    <button
      title={title}
      onClick={onClick}
      disabled={disabled}
      style={{
        background: 'none', border: '1px solid var(--border)', borderRadius: 4,
        color: 'var(--text-muted)', cursor: disabled ? 'default' : 'pointer',
        padding: '2px 6px', fontSize: 14, lineHeight: 1.4, opacity: disabled ? 0.5 : 1,
        transition: 'color 0.15s, border-color 0.15s',
      }}
      onMouseEnter={e => { if (!disabled) { e.currentTarget.style.color = 'var(--text-primary)'; e.currentTarget.style.borderColor = 'var(--text-primary)'; } }}
      onMouseLeave={e => { e.currentTarget.style.color = 'var(--text-muted)'; e.currentTarget.style.borderColor = 'var(--border)'; }}
    >
      {content}
    </button>
  );

  // Consensus: count how many horizons agree on the majority direction
  const horizonDirections = isSuccess ? [result.predictedUp, result.predictedUp3, result.predictedUp6] : [];
  const upVotes   = horizonDirections.filter(Boolean).length;
  const downVotes = horizonDirections.length - upVotes;
  const consensusDir   = upVotes >= downVotes ? 'UP' : 'DOWN';
  const consensusCount = Math.max(upVotes, downVotes);
  const consensusTotal = horizonDirections.length;
  const consensusStrong = consensusCount === consensusTotal; // all 3 agree

  return (
    <div
      style={{
        background: 'var(--bg-card)',
        border: `1px solid ${isExpired ? '#b45309' : 'var(--border)'}`,
        borderRadius: 8, padding: 20, display: 'flex', flexDirection: 'column', gap: 14,
        opacity: isExpired ? 0.85 : 1,
      }}>
      {/* Row 1: symbol + single status badge */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <span style={{ fontWeight: 700, fontSize: 20, color: 'var(--text-primary)' }}>{result.symbol}</span>
        <span style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, border: `1px solid ${badgeColor}`, color: badgeColor, textTransform: 'uppercase', letterSpacing: '0.05em', whiteSpace: 'nowrap' }}>
          {badgeLabel}
        </span>
      </div>
      {/* Row 2: action buttons */}
      <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
        {iconBtn(`Open ${result.symbol} chart`, '📈', (e) => { e.stopPropagation(); onSymbolClick?.(result.symbol); }, false)}
        {iconBtn(refreshing ? 'Refreshing…' : `Refresh ${result.symbol} prediction`, refreshing ? '⏳' : '↻', handleRefresh, refreshing)}
        {iconBtn(`Delete ${result.symbol} prediction`, '🗑', (e) => { e.stopPropagation(); onDelete?.(); }, false)}
      </div>

      {isSuccess && (
        <>
          {/* Main direction + confidence */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 20 }}>
            <div style={{ fontSize: 34, fontWeight: 700, color: result.predictedUp ? 'var(--green)' : 'var(--red)', lineHeight: 1 }}>
              {result.predictedUp ? '↑ UP' : '↓ DOWN'}
            </div>
            <div>
              <div style={{ fontSize: 24, fontWeight: 600, color: 'var(--text-primary)' }}>{pct(result.probability)}</div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>confidence</div>
            </div>
          </div>

          <ProbBar value={result.probability} isUp={result.predictedUp} />

          {/* Consensus badge */}
          {consensusTotal > 0 && (
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={{
                fontSize: 11, padding: '3px 10px', borderRadius: 10,
                border: `1px solid ${consensusStrong ? (consensusDir === 'UP' ? 'var(--green)' : 'var(--red)') : '#d97706'}`,
                color: consensusStrong ? (consensusDir === 'UP' ? 'var(--green)' : 'var(--red)') : '#d97706',
                fontWeight: 600, letterSpacing: '0.04em',
              }}>
                {consensusCount}/{consensusTotal} horizons → {consensusDir}
              </span>
              <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                {consensusStrong ? 'strong consensus' : 'partial consensus'}
              </span>
            </div>
          )}

          {/* Per-horizon boxes */}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 8 }}>
            {[
              { label: '1c', up: result.predictedUp,  prob: result.probability,  acc: result.modelAccuracy,  wfAcc: result.walkForwardAccuracy,  auc: result.modelAuc,  wfAuc: result.walkForwardAuc,  lrAcc: result.logRegAccuracy,  wfLrAcc: result.walkForwardLogRegAccuracy,  wfLrAuc: result.walkForwardLogRegAuc  },
              { label: '3c', up: result.predictedUp3, prob: result.probability3, acc: result.modelAccuracy3, wfAcc: result.walkForwardAccuracy3, auc: result.modelAuc3, wfAuc: result.walkForwardAuc3, lrAcc: result.logRegAccuracy3, wfLrAcc: result.walkForwardLogRegAccuracy3, wfLrAuc: result.walkForwardLogRegAuc3 },
              { label: '6c', up: result.predictedUp6, prob: result.probability6, acc: result.modelAccuracy6, wfAcc: result.walkForwardAccuracy6, auc: result.modelAuc6, wfAuc: result.walkForwardAuc6, lrAcc: result.logRegAccuracy6, wfLrAcc: result.walkForwardLogRegAccuracy6, wfLrAuc: result.walkForwardLogRegAuc6 },
            ].map(h => (
              <div
                key={h.label}
                style={{
                  border: `1px solid ${h.up ? 'color-mix(in srgb, var(--green) 30%, var(--border))' : 'color-mix(in srgb, var(--red) 30%, var(--border))'}`,
                  borderRadius: 8, padding: '10px 8px', background: 'var(--bg-primary)',
                  display: 'flex', flexDirection: 'column', gap: 3, alignItems: 'center',
                }}>
                <div style={{ fontSize: 10, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>{h.label}</div>
                <div style={{ fontSize: 15, fontWeight: 700, color: h.up ? 'var(--green)' : 'var(--red)' }}>
                  {h.up ? '↑ UP' : '↓ DOWN'}
                </div>
                <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-primary)' }}>{pct(h.prob)}</div>
                <div style={{ width: '100%', height: 3, borderRadius: 2, background: 'var(--border)', overflow: 'hidden', margin: '2px 0' }}>
                  <div style={{ height: '100%', width: `${Math.round((h.prob ?? 0) * 100)}%`, background: h.up ? 'var(--green)' : 'var(--red)', borderRadius: 2 }} />
                </div>
                <div style={{ fontSize: 10, color: 'var(--text-muted)', textAlign: 'center', lineHeight: 1.4 }}>
                  <div>FT acc {pct(h.acc)} · AUC {(h.auc ?? 0).toFixed(3)}</div>
                  <div>WF acc {pct(h.wfAcc)} · AUC {(h.wfAuc ?? 0).toFixed(3)}</div>
                  <div style={{ color: accuracyColor(h.lrAcc ?? 0) }}>LR {pct(h.lrAcc)} · WF {pct(h.wfLrAcc)}</div>
                </div>
              </div>
            ))}
          </div>

          {/* Confidence trend sparkline */}
          <ConfidenceSparkline symbol={result.symbol} currentProb={result.probability} currentUp={result.predictedUp} />

          {/* Benchmarks + summary stats */}
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '6px 0', borderTop: '1px solid var(--border)', paddingTop: 12 }}>
            <MetricRow label="1c Buy & Hold" value={pct(result.benchmarkBuyHold)} />
            <MetricRow label="1c SMA crossover" value={pct(result.benchmarkSma)} />
            <MetricRow label="3c Buy & Hold" value={pct(result.benchmarkBuyHold3)} />
            <MetricRow label="6c Buy & Hold" value={pct(result.benchmarkBuyHold6)} />
            <MetricRow label="WF folds" value={String(result.walkForwardFoldCount ?? 0)} />
            <MetricRow label="Candles" value={`${result.trainSamples}tr / ${result.testSamples}te / ${result.totalCandles}`} />
          </div>
        </>
      )}

      {!isSuccess && result.errorMessage && (
        <div style={{ fontSize: 13, color: 'var(--text-muted)', lineHeight: 1.5 }}>{result.errorMessage}</div>
      )}

      <div style={{ fontSize: 11, color: isExpired ? '#b45309' : 'var(--text-muted)', borderTop: '1px solid var(--border)', paddingTop: 8 }}>
        {refreshing
          ? 'Updating…'
          : `${parseUtc(result.computedAt).toLocaleString()} · ${ageLabel(result.computedAt)}`}
      </div>
    </div>
  );
}

function ProbBar({ value, isUp }) {
  const pct = Math.round((value ?? 0) * 100);
  return (
    <div style={{ height: 6, borderRadius: 3, background: 'var(--border)', overflow: 'hidden' }}>
      <div style={{ height: '100%', width: `${pct}%`, background: isUp ? 'var(--green)' : 'var(--red)', borderRadius: 3, transition: 'width 0.4s' }} />
    </div>
  );
}

function MetricRow({ label, value, accent }) {
  return (
    <>
      <span style={{ fontSize: 12, color: 'var(--text-muted)' }}>{label}</span>
      <span style={{ fontSize: 13, fontWeight: 600, color: accent || 'var(--text-primary)', textAlign: 'right' }}>{value}</span>
    </>
  );
}

function accuracyColor(acc) {
  if (acc >= 0.58) return 'var(--green)';
  if (acc >= 0.52) return '#f59e0b';
  return 'var(--red)';
}

function ConfidenceSparkline({ symbol, currentProb, currentUp }) {
  const [history, setHistory] = useState(null);

  useEffect(() => {
    api.get(`/predictions/history/${encodeURIComponent(symbol)}?limit=20`)
      .then(r => setHistory(r.data || []))
      .catch(() => setHistory([]));
  }, [symbol]);

  if (!history || history.length < 2) return null;

  const W = 100, H = 30, PAD = 2;
  const probs = history.map(h => h.probability);
  const minP = Math.min(...probs, 0.4);
  const maxP = Math.max(...probs, 0.7);
  const range = maxP - minP || 0.1;
  const pts = probs.map((p, i) => {
    const x = PAD + (i / (probs.length - 1)) * (W - PAD * 2);
    const y = PAD + (1 - (p - minP) / range) * (H - PAD * 2);
    return `${x},${y}`;
  }).join(' ');

  const trend = probs[probs.length - 1] >= probs[0] ? 'var(--green)' : 'var(--red)';

  return (
    <div style={{ borderTop: '1px solid var(--border)', paddingTop: 8 }}>
      <div style={{ fontSize: 10, color: 'var(--text-muted)', marginBottom: 4 }}>
        Confidence trend · last {history.length} runs
      </div>
      <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" style={{ width: '100%', height: 30, display: 'block' }}>
        {history.map((h, i) => {
          const x = PAD + (i / (probs.length - 1)) * (W - PAD * 2);
          const y = PAD + (1 - (h.probability - minP) / range) * (H - PAD * 2);
          return <circle key={i} cx={x} cy={y} r={1.2} fill={h.predictedUp ? 'var(--green)' : 'var(--red)'} vectorEffect="non-scaling-stroke" />;
        })}
        <polyline points={pts} fill="none" stroke={trend} strokeWidth="1" strokeDasharray="2,1" vectorEffect="non-scaling-stroke" />
      </svg>
    </div>
  );
}
