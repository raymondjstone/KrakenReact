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
  const [sortBy, setSortBy] = useState('symbol');
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
    : results.map(r => r.symbol); // show what was actually run

  const headerSummary = mode === 'all'
    ? `All active */${currency} pairs`
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
        // UP first, then sort by probability within each group
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
      default: // 'symbol'
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
            onChange={e => setSortBy(e.target.value)}
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
            <div style={{ display: 'flex', gap: 8 }}>
              {modeBtn('specific', 'Specific symbols')}
              {modeBtn('all', 'All active pairs')}
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

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(340px, 1fr))', gap: 20 }}>
        {sortedResults.map(r => <PredictionCard key={r.symbol} result={r} onSymbolClick={onSymbolClick} />)}
      </div>

      <div style={{ marginTop: 32, padding: 16, background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, fontSize: 13, color: 'var(--text-muted)', lineHeight: 1.6 }}>
        <strong style={{ color: 'var(--text-primary)' }}>How it works</strong>
        <br />
        Fetches OHLCV kline data from Kraken and computes technical indicators (RSI, MACD, ATR, Bollinger Bands, volume signals).
        Two models are trained on the most recent 70% of data and evaluated on the remaining 30% — strictly chronological, no data leakage.
        The gradient-boosted model (FastTree) generates the directional prediction; logistic regression is shown for comparison.
        Accuracy above ~55% in a noisy market is genuinely useful — the benchmark columns show context for what random-chance looks like for each asset.
      </div>
    </div>
  );
}

function PredictionCard({ result, onSymbolClick }) {
  const [hovered, setHovered] = useState(false);
  const isSuccess = result.status === 'success';
  const hasError  = result.status === 'error';
  const pct = v => `${(v * 100).toFixed(1)}%`;
  const intervalLabel = INTERVAL_LABELS[result.interval] || result.interval;

  const statusColor = isSuccess ? 'var(--green)' : hasError ? 'var(--red)' : 'var(--text-muted)';
  const statusLabel = isSuccess ? 'success' : hasError ? 'error' : 'insufficient data';

  return (
    <div
      onClick={() => onSymbolClick?.(result.symbol)}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      title={`Open ${result.symbol} chart`}
      style={{
        background: hovered ? 'var(--bg-hover, var(--bg-card))' : 'var(--bg-card)',
        border: `1px solid ${hovered ? 'var(--green)' : 'var(--border)'}`,
        borderRadius: 8, padding: 20, display: 'flex', flexDirection: 'column', gap: 14,
        cursor: 'pointer', transition: 'border-color 0.15s',
      }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <span style={{ fontWeight: 700, fontSize: 20, color: 'var(--text-primary)' }}>{result.symbol}</span>
        <span style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, border: `1px solid ${statusColor}`, color: statusColor, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          {statusLabel}
        </span>
      </div>

      {isSuccess && (
        <>
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

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px 0', borderTop: '1px solid var(--border)', paddingTop: 12 }}>
            <MetricRow label="FastTree acc" value={pct(result.modelAccuracy)} accent={accuracyColor(result.modelAccuracy)} />
            <MetricRow label="AUC-ROC" value={result.modelAuc.toFixed(3)} accent={aucColor(result.modelAuc)} />
            <MetricRow label="LogReg acc" value={pct(result.logRegAccuracy)} accent={accuracyColor(result.logRegAccuracy)} />
            <MetricRow label="Buy & Hold" value={pct(result.benchmarkBuyHold)} />
            <MetricRow label="SMA crossover" value={pct(result.benchmarkSma)} />
          </div>

          <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
            {intervalLabel} candles &middot; {result.trainSamples} train / {result.testSamples} test / {result.totalCandles} total
          </div>
        </>
      )}

      {!isSuccess && result.errorMessage && (
        <div style={{ fontSize: 13, color: 'var(--text-muted)', lineHeight: 1.5 }}>{result.errorMessage}</div>
      )}

      <div style={{ fontSize: 11, color: 'var(--text-muted)', borderTop: '1px solid var(--border)', paddingTop: 8 }}>
        {new Date(result.computedAt).toLocaleString()}
      </div>
    </div>
  );
}

function ProbBar({ value, isUp }) {
  const pct = Math.round(value * 100);
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

function aucColor(auc) {
  if (auc >= 0.60) return 'var(--green)';
  if (auc >= 0.52) return '#f59e0b';
  return 'var(--red)';
}
