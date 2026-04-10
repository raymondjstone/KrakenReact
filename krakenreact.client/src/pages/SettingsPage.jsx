import { useState, useEffect } from 'react';
import api from '../api/apiClient';

const SETTINGS_KEY = 'kraken_app_settings';

export function loadSettings() {
  try {
    const stored = localStorage.getItem(SETTINGS_KEY);
    if (stored) return { ...defaultSettings, ...JSON.parse(stored) };
  } catch { /* ignore */ }
  return { ...defaultSettings };
}

export function saveSettings(settings) {
  localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
}

const defaultSettings = {
  largeMovementThreshold: 5,
};

export default function SettingsPage({ settings, onSettingsChange }) {
  const [threshold, setThreshold] = useState(settings.largeMovementThreshold);
  const [activeTab, setActiveTab] = useState('general');

  // API Settings
  const [krakenApiKey, setKrakenApiKey] = useState('');
  const [krakenApiSecret, setKrakenApiSecret] = useState('');
  const [pushoverUserKey, setPushoverUserKey] = useState('');
  const [pushoverApiToken, setPushoverApiToken] = useState('');

  // Trading Lists
  const [baseCurrencies, setBaseCurrencies] = useState('');
  const [blacklist, setBlacklist] = useState('');
  const [majorCoin, setMajorCoin] = useState('');
  const [currency, setCurrency] = useState('');
  const [badPairs, setBadPairs] = useState('');
  const [defaultPairs, setDefaultPairs] = useState('');

  // Asset Normalizations
  const [normalizations, setNormalizations] = useState('');

  const [saveStatus, setSaveStatus] = useState('');
  const saveTimerRef = { current: null };

  const loadServerSettings = () => {
    api.get('/settings').then(r => {
      const data = r.data;
      setKrakenApiKey(data.krakenApiKey || '');
      setKrakenApiSecret(data.krakenApiSecret || '');
      setPushoverUserKey(data.pushoverUserKey || '');
      setPushoverApiToken(data.pushoverApiToken || '');
      setBaseCurrencies((data.baseCurrencies || []).join(', '));
      setBlacklist((data.blacklist || []).join(', '));
      setMajorCoin((data.majorCoin || []).join(', '));
      setCurrency((data.currency || []).join(', '));
      setBadPairs((data.badPairs || []).join(', '));
      setDefaultPairs((data.defaultPairs || []).join(', '));

      // Convert normalization dict to string
      if (data.assetNormalizations) {
        const normStr = Object.entries(data.assetNormalizations)
          .map(([k, v]) => `${k}=${v}`)
          .join('\n');
        setNormalizations(normStr);
      }
    }).catch(err => console.error('Failed to load settings:', err));
  };

  useEffect(() => {
    loadServerSettings();
    return () => { if (saveTimerRef.current) clearTimeout(saveTimerRef.current); };
  }, []);

  const handleThresholdChange = (val) => {
    const num = Math.max(0, Math.min(100, Number(val) || 0));
    setThreshold(num);
    const updated = { ...settings, largeMovementThreshold: num };
    onSettingsChange(updated);
  };

  const handleSave = () => {
    setSaveStatus('Saving...');

    const payload = {
      krakenApiKey: krakenApiKey || undefined,
      krakenApiSecret: krakenApiSecret || undefined,
      pushoverUserKey: pushoverUserKey || undefined,
      pushoverApiToken: pushoverApiToken || undefined,
      baseCurrencies: baseCurrencies.split(',').map(s => s.trim()).filter(Boolean),
      blacklist: blacklist.split(',').map(s => s.trim()).filter(Boolean),
      majorCoin: majorCoin.split(',').map(s => s.trim()).filter(Boolean),
      currency: currency.split(',').map(s => s.trim()).filter(Boolean),
      badPairs: badPairs.split(',').map(s => s.trim()).filter(Boolean),
      defaultPairs: defaultPairs.split(',').map(s => s.trim()).filter(Boolean),
    };

    // Parse normalizations
    const normDict = {};
    normalizations.split('\n').forEach(line => {
      const [k, v] = line.split('=').map(s => s.trim());
      if (k && v) normDict[k] = v;
    });
    payload.assetNormalizations = normDict;

    api.post('/settings', payload)
      .then(() => {
        setSaveStatus('Saved successfully!');
        if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
        saveTimerRef.current = setTimeout(() => setSaveStatus(''), 3000);
      })
      .catch(err => {
        setSaveStatus('Error saving settings');
        console.error(err);
        if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
        saveTimerRef.current = setTimeout(() => setSaveStatus(''), 3000);
      });
  };

  const cardStyle = { background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 20 };
  const labelStyle = { marginBottom: 8, fontWeight: 600, color: 'var(--text-primary)' };
  const inputStyle = { width: '100%', padding: '8px 12px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 14 };
  const textareaStyle = { ...inputStyle, minHeight: 100, fontFamily: 'monospace', resize: 'vertical' };
  const hintStyle = { color: 'var(--text-muted)', fontSize: 13, marginTop: 4 };
  const buttonStyle = { padding: '10px 20px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 };

  return (
    <div style={{ padding: 24, maxWidth: 900 }}>
      <h2 style={{ margin: '0 0 24px', color: 'var(--text-primary)' }}>Settings</h2>

      <div style={{ display: 'flex', gap: 8, marginBottom: 24, borderBottom: '1px solid var(--border)' }}>
        <button 
          onClick={() => setActiveTab('general')}
          style={{ padding: '8px 16px', background: activeTab === 'general' ? 'var(--bg-card)' : 'transparent', border: 'none', borderBottom: activeTab === 'general' ? '2px solid var(--green)' : '2px solid transparent', color: 'var(--text-primary)', cursor: 'pointer', fontWeight: 600 }}>
          General
        </button>
        <button 
          onClick={() => setActiveTab('api')}
          style={{ padding: '8px 16px', background: activeTab === 'api' ? 'var(--bg-card)' : 'transparent', border: 'none', borderBottom: activeTab === 'api' ? '2px solid var(--green)' : '2px solid transparent', color: 'var(--text-primary)', cursor: 'pointer', fontWeight: 600 }}>
          API Keys
        </button>
        <button 
          onClick={() => setActiveTab('lists')}
          style={{ padding: '8px 16px', background: activeTab === 'lists' ? 'var(--bg-card)' : 'transparent', border: 'none', borderBottom: activeTab === 'lists' ? '2px solid var(--green)' : '2px solid transparent', color: 'var(--text-primary)', cursor: 'pointer', fontWeight: 600 }}>
          Trading Lists
        </button>
        <button 
          onClick={() => setActiveTab('normalizations')}
          style={{ padding: '8px 16px', background: activeTab === 'normalizations' ? 'var(--bg-card)' : 'transparent', border: 'none', borderBottom: activeTab === 'normalizations' ? '2px solid var(--green)' : '2px solid transparent', color: 'var(--text-primary)', cursor: 'pointer', fontWeight: 600 }}>
          Asset Normalizations
        </button>
      </div>

      {activeTab === 'general' && (
        <div style={cardStyle}>
          <div style={labelStyle}>Large Movement Threshold</div>
          <div style={hintStyle}>
            Assets that have moved by this percentage or more in the last 25 hours will be temporarily pinned to the Dashboard ticker bar.
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 12 }}>
            <input
              type="range"
              min="0"
              max="100"
              step="1"
              value={threshold}
              onChange={e => handleThresholdChange(e.target.value)}
              style={{ flex: 1 }}
            />
            <input
              type="number"
              min="0"
              max="100"
              step="1"
              value={threshold}
              onChange={e => handleThresholdChange(e.target.value)}
              style={{ width: 60, padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', textAlign: 'center', fontSize: 14 }}
            />
            <span style={{ color: 'var(--text-muted)' }}>%</span>
          </div>
        </div>
      )}

      {activeTab === 'api' && (
        <>
          <div style={cardStyle}>
            <div style={labelStyle}>Kraken API Key</div>
            <input type="text" value={krakenApiKey} onChange={e => setKrakenApiKey(e.target.value)} style={inputStyle} placeholder="Your Kraken API key" />
            <div style={hintStyle}>Used to connect to Kraken exchange API</div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Kraken API Secret</div>
            <input type="password" value={krakenApiSecret} onChange={e => setKrakenApiSecret(e.target.value)} style={inputStyle} placeholder="Your Kraken API secret" />
            <div style={hintStyle}>Keep this secret secure</div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Pushover User Key</div>
            <input type="text" value={pushoverUserKey} onChange={e => setPushoverUserKey(e.target.value)} style={inputStyle} placeholder="Your Pushover user key" />
            <div style={hintStyle}>For push notifications via Pushover</div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Pushover API Token</div>
            <input type="password" value={pushoverApiToken} onChange={e => setPushoverApiToken(e.target.value)} style={inputStyle} placeholder="Your Pushover API token" />
            <div style={hintStyle}>Application token for Pushover</div>
          </div>

          <button onClick={handleSave} style={buttonStyle}>Save API Settings</button>
          {saveStatus && <span style={{ marginLeft: 12, color: saveStatus.includes('Error') ? 'var(--red)' : 'var(--green)' }}>{saveStatus}</span>}
        </>
      )}

      {activeTab === 'lists' && (
        <>
          <div style={cardStyle}>
            <div style={labelStyle}>Base Currencies</div>
            <input type="text" value={baseCurrencies} onChange={e => setBaseCurrencies(e.target.value)} style={inputStyle} placeholder="ZUSD" />
            <div style={hintStyle}>Comma-separated list of base currencies</div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Blacklist</div>
            <input type="text" value={blacklist} onChange={e => setBlacklist(e.target.value)} style={inputStyle} placeholder="TRUMP, MELANIA, MATIC" />
            <div style={hintStyle}>Assets to exclude from trading</div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Major Coins</div>
            <input type="text" value={majorCoin} onChange={e => setMajorCoin(e.target.value)} style={inputStyle} placeholder="XBT, ETH, SOL" />
            <div style={hintStyle}>Major cryptocurrency assets</div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Currencies</div>
            <input type="text" value={currency} onChange={e => setCurrency(e.target.value)} style={inputStyle} placeholder="GBP, EUR, USD, USDT" />
            <div style={hintStyle}>Fiat and stablecoin currencies</div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Bad Pairs</div>
            <input type="text" value={badPairs} onChange={e => setBadPairs(e.target.value)} style={inputStyle} placeholder="MATIC/USD, XBT/USD" />
            <div style={hintStyle}>Trading pairs to avoid</div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Default Pairs</div>
            <input type="text" value={defaultPairs} onChange={e => setDefaultPairs(e.target.value)} style={inputStyle} placeholder="SOL/USD, ETH/USD" />
            <div style={hintStyle}>Default trading pairs to display</div>
          </div>

          <button onClick={handleSave} style={buttonStyle}>Save Trading Lists</button>
          {saveStatus && <span style={{ marginLeft: 12, color: saveStatus.includes('Error') ? 'var(--red)' : 'var(--green)' }}>{saveStatus}</span>}
        </>
      )}

      {activeTab === 'normalizations' && (
        <>
          <div style={cardStyle}>
            <div style={labelStyle}>Asset Normalizations</div>
            <div style={hintStyle}>
              Define how Kraken asset names map to normalized names. Format: KRAKENNAME=NORMALIZED (one per line).
              Example: XXBT=XBT
            </div>
            <textarea 
              value={normalizations} 
              onChange={e => setNormalizations(e.target.value)} 
              style={textareaStyle}
              placeholder="XXBT=XBT&#10;ZUSD=USD&#10;ZGBP=GBP"
            />
          </div>

          <button onClick={handleSave} style={buttonStyle}>Save Normalizations</button>
          {saveStatus && <span style={{ marginLeft: 12, color: saveStatus.includes('Error') ? 'var(--red)' : 'var(--green)' }}>{saveStatus}</span>}
        </>
      )}
    </div>
  );
}
