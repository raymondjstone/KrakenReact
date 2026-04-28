import { useState, useEffect, useRef, useCallback } from 'react';
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

export default function SettingsPage({ settings, onSettingsChange, serverSettings, onServerSettingsRefresh }) {
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

  // Boolean settings
  const [stakingNotifications, setStakingNotifications] = useState(false);
  const [hideAlmostZeroBalances, setHideAlmostZeroBalances] = useState(false);
  const [orderProximityNotifications, setOrderProximityNotifications] = useState(true);
  const [orderProximityThreshold, setOrderProximityThreshold] = useState(2.0);

  // Order dialog button configs
  const [orderPriceOffsets, setOrderPriceOffsets] = useState('2, 5, 10, 15');
  const [orderQtyPercentages, setOrderQtyPercentages] = useState('5, 10, 20, 25, 50, 75, 100');

  // Auto-sell
  const [autoSellOnBuyFill, setAutoSellOnBuyFill] = useState(false);
  const [autoSellPercentage, setAutoSellPercentage] = useState(10);

  // Auto-add staking to order
  const [autoAddStakingToOrder, setAutoAddStakingToOrder] = useState(false);

  // Order book
  const [orderBookDepth, setOrderBookDepth] = useState(25);

  // Schedule
  const [priceDownloadTime, setPriceDownloadTime] = useState('04:00');
  const [predictionJobTime, setPredictionJobTime] = useState('05:00');
  const [predictionAutoRefreshInterval, setPredictionAutoRefreshInterval] = useState(15);
  const [scheduleInfo, setScheduleInfo] = useState(null);
  const [triggerStatus, setTriggerStatus] = useState('');
  const triggerTimerRef = useRef(null);

  // Asset Normalizations
  const [normalizations, setNormalizations] = useState('');

  const [saveStatus, setSaveStatus] = useState('');
  const [loaded, setLoaded] = useState(false);
  const saveTimerRef = useRef(null);

  // Populate local state from serverSettings prop (loaded once in parent, survives tab switches)
  useEffect(() => {
    if (!serverSettings) return;
    const data = serverSettings;
    setKrakenApiKey(data.krakenApiKey || '');
    setKrakenApiSecret(data.krakenApiSecret || '');
    setPushoverUserKey(data.pushoverUserKey || '');
    setPushoverApiToken(data.pushoverApiToken || '');
    setStakingNotifications(!!data.stakingNotifications);
    setHideAlmostZeroBalances(!!data.hideAlmostZeroBalances);
    setOrderProximityNotifications(data.orderProximityNotifications !== false);
    setOrderProximityThreshold(data.orderProximityThreshold ?? 2.0);
    setBaseCurrencies((data.baseCurrencies || []).join(', '));
    setBlacklist((data.blacklist || []).join(', '));
    setMajorCoin((data.majorCoin || []).join(', '));
    setCurrency((data.currency || []).join(', '));
    setBadPairs((data.badPairs || []).join(', '));
    setDefaultPairs((data.defaultPairs || []).join(', '));
    if (data.assetNormalizations) {
      setNormalizations(Object.entries(data.assetNormalizations).map(([k, v]) => `${k}=${v}`).join('\n'));
    }
    if (data.orderPriceOffsets?.length) setOrderPriceOffsets(data.orderPriceOffsets.join(', '));
    if (data.orderQtyPercentages?.length) setOrderQtyPercentages(data.orderQtyPercentages.join(', '));
    setAutoSellOnBuyFill(!!data.autoSellOnBuyFill);
    setAutoSellPercentage(data.autoSellPercentage ?? 10);
    setAutoAddStakingToOrder(!!data.autoAddStakingToOrder);
    setOrderBookDepth(data.orderBookDepth || 25);
    setPriceDownloadTime(data.priceDownloadTime || '04:00');
    setPredictionJobTime(data.predictionJobTime || '05:00');
    setPredictionAutoRefreshInterval(data.predictionAutoRefreshIntervalMinutes ?? 15);
    setLoaded(true);
  }, [serverSettings]);

  useEffect(() => {
    return () => {
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
      if (triggerTimerRef.current) clearTimeout(triggerTimerRef.current);
    };
  }, []);

  const fetchScheduleInfo = useCallback(() => {
    api.get('/schedule/price-download')
      .then(r => setScheduleInfo(r.data))
      .catch(() => setScheduleInfo(null));
  }, []);

  useEffect(() => {
    if (activeTab === 'schedule') fetchScheduleInfo();
  }, [activeTab, fetchScheduleInfo]);

  const handleThresholdChange = (val) => {
    const num = Math.max(0, Math.min(100, Number(val) || 0));
    setThreshold(num);
    const updated = { ...settings, largeMovementThreshold: num };
    onSettingsChange(updated);
  };

  const handleSave = () => {
    if (!loaded) return; // Don't save until server settings have been loaded
    setSaveStatus('Saving...');

    const payload = {
      krakenApiKey: krakenApiKey || undefined,
      krakenApiSecret: krakenApiSecret || undefined,
      pushoverUserKey: pushoverUserKey || undefined,
      pushoverApiToken: pushoverApiToken || undefined,
      stakingNotifications: stakingNotifications,
      hideAlmostZeroBalances: hideAlmostZeroBalances,
      orderProximityNotifications: orderProximityNotifications,
      orderProximityThreshold: orderProximityThreshold,
      baseCurrencies: baseCurrencies.split(',').map(s => s.trim()).filter(Boolean),
      blacklist: blacklist.split(',').map(s => s.trim()).filter(Boolean),
      majorCoin: majorCoin.split(',').map(s => s.trim()).filter(Boolean),
      currency: currency.split(',').map(s => s.trim()).filter(Boolean),
      badPairs: badPairs.split(',').map(s => s.trim()).filter(Boolean),
      defaultPairs: defaultPairs.split(',').map(s => s.trim()).filter(Boolean),
    };

    // Parse order dialog button configs
    payload.orderPriceOffsets = orderPriceOffsets.split(',').map(s => parseFloat(s.trim())).filter(v => v > 0 && !isNaN(v));
    payload.orderQtyPercentages = orderQtyPercentages.split(',').map(s => parseFloat(s.trim())).filter(v => v > 0 && v <= 100 && !isNaN(v));

    // Auto-sell settings
    payload.autoSellOnBuyFill = autoSellOnBuyFill;
    payload.autoSellPercentage = autoSellPercentage;
    payload.autoAddStakingToOrder = autoAddStakingToOrder;
    payload.orderBookDepth = orderBookDepth;
    payload.priceDownloadTime = priceDownloadTime;
    payload.predictionJobTime = predictionJobTime;
    payload.predictionAutoRefreshIntervalMinutes = Math.max(5, Number(predictionAutoRefreshInterval) || 15);

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
        onServerSettingsRefresh(); // Refresh parent cache (updates masked API keys etc.)
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

  const handleTriggerNow = () => {
    setTriggerStatus('Running...');
    api.post('/schedule/price-download/trigger')
      .then(() => {
        setTriggerStatus('Job triggered! Check Hangfire dashboard for progress.');
        if (triggerTimerRef.current) clearTimeout(triggerTimerRef.current);
        triggerTimerRef.current = setTimeout(() => {
          setTriggerStatus('');
          fetchScheduleInfo();
        }, 5000);
      })
      .catch(() => {
        setTriggerStatus('Error triggering job');
        if (triggerTimerRef.current) clearTimeout(triggerTimerRef.current);
        triggerTimerRef.current = setTimeout(() => setTriggerStatus(''), 3000);
      });
  };

  const cardStyle = { background: 'var(--bg-card)', border: '1px solid var(--border)', borderRadius: 8, padding: 20, marginBottom: 20 };
  const labelStyle = { marginBottom: 8, fontWeight: 600, color: 'var(--text-primary)' };
  const inputStyle = { width: '100%', padding: '8px 12px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 14 };
  const textareaStyle = { ...inputStyle, minHeight: 500, fontFamily: 'monospace', resize: 'vertical' };
  const hintStyle = { color: 'var(--text-muted)', fontSize: 13, marginTop: 4 };
  const buttonStyle = { padding: '10px 20px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600 };

  return (
    <div style={{ padding: 24, maxWidth: 900, height: '100%', overflow: 'auto' }}>
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
        <button
          onClick={() => setActiveTab('schedule')}
          style={{ padding: '8px 16px', background: activeTab === 'schedule' ? 'var(--bg-card)' : 'transparent', border: 'none', borderBottom: activeTab === 'schedule' ? '2px solid var(--green)' : '2px solid transparent', color: 'var(--text-primary)', cursor: 'pointer', fontWeight: 600 }}>
          Schedule
        </button>
      </div>

      {activeTab === 'general' && (
        <>
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

          <div style={cardStyle}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div>
                <div style={labelStyle}>Staking Reward Notifications</div>
                <div style={hintStyle}>
                  Send a Pushover notification when a new staking reward payment is received. Requires Pushover API keys to be configured.
                </div>
              </div>
              <label className="toggle" style={{ flexShrink: 0, marginLeft: 16 }}>
                <input
                  type="checkbox"
                  checked={stakingNotifications}
                  onChange={e => setStakingNotifications(e.target.checked)}
                />
                <span className="toggle-slider" />
              </label>
            </div>
          </div>

          <div style={cardStyle}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div>
                <div style={labelStyle}>Auto-Add Staking Rewards to Open Order</div>
                <div style={hintStyle}>
                  When a staking reward is received, automatically add the reward quantity to the newest open sell order for that asset. The order is amended after a 2-minute delay to allow balances to settle.
                </div>
              </div>
              <label className="toggle" style={{ flexShrink: 0, marginLeft: 16 }}>
                <input
                  type="checkbox"
                  checked={autoAddStakingToOrder}
                  onChange={e => setAutoAddStakingToOrder(e.target.checked)}
                />
                <span className="toggle-slider" />
              </label>
            </div>
          </div>

          <div style={cardStyle}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div>
                <div style={labelStyle}>Hide Almost Zero Balances</div>
                <div style={hintStyle}>
                  Hide balance rows with less than 0.0001 units or less than $0.01 value from the Dashboard and Balances grids. These balances still count towards portfolio totals.
                </div>
              </div>
              <label className="toggle" style={{ flexShrink: 0, marginLeft: 16 }}>
                <input
                  type="checkbox"
                  checked={hideAlmostZeroBalances}
                  onChange={e => setHideAlmostZeroBalances(e.target.checked)}
                />
                <span className="toggle-slider" />
              </label>
            </div>
          </div>

          <div style={cardStyle}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div>
                <div style={labelStyle}>Order Proximity Notifications</div>
                <div style={hintStyle}>
                  Send a Pushover notification when the current price is within the configured percentage of an open order. Requires Pushover API keys to be configured.
                </div>
              </div>
              <label className="toggle" style={{ flexShrink: 0, marginLeft: 16 }}>
                <input
                  type="checkbox"
                  checked={orderProximityNotifications}
                  onChange={e => setOrderProximityNotifications(e.target.checked)}
                />
                <span className="toggle-slider" />
              </label>
            </div>
            {orderProximityNotifications && (
              <div style={{ marginTop: 16 }}>
                <div style={labelStyle}>Proximity Threshold</div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 8 }}>
                  <input
                    type="range"
                    min="0.1"
                    max="20.0"
                    step="0.1"
                    value={orderProximityThreshold}
                    onChange={e => setOrderProximityThreshold(parseFloat(e.target.value))}
                    style={{ flex: 1 }}
                  />
                  <input
                    type="number"
                    min="0.1"
                    max="20.0"
                    step="0.1"
                    value={orderProximityThreshold}
                    onChange={e => {
                      const v = parseFloat(e.target.value);
                      if (!isNaN(v)) setOrderProximityThreshold(Math.min(20.0, Math.max(0.1, v)));
                    }}
                    style={{ width: 70, padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', textAlign: 'center', fontSize: 14 }}
                  />
                  <span style={{ color: 'var(--text-muted)' }}>%</span>
                </div>
                <div style={hintStyle}>Alert when price is within this percentage of an open order (0.1% � 20.0%)</div>
              </div>
            )}
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Order Dialog — Price Offset Buttons</div>
            <div style={hintStyle}>
              Comma-separated percentage values for the price helper buttons on the order dialog. Buy orders lower the price, sell orders raise it.
            </div>
            <input
              type="text"
              value={orderPriceOffsets}
              onChange={e => setOrderPriceOffsets(e.target.value)}
              style={{ ...inputStyle, marginTop: 8 }}
              placeholder="2, 5, 10, 15"
            />
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Order Dialog — Quantity Percentage Buttons</div>
            <div style={hintStyle}>
              Comma-separated percentage values for the sell quantity helper buttons on the order dialog. Each button sets the quantity to that percentage of the available (or uncovered) balance.
            </div>
            <input
              type="text"
              value={orderQtyPercentages}
              onChange={e => setOrderQtyPercentages(e.target.value)}
              style={{ ...inputStyle, marginTop: 8 }}
              placeholder="5, 10, 20, 25, 50, 75, 100"
            />
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Order Book Depth</div>
            <div style={hintStyle}>
              Number of price levels shown on each side of the order book. Higher values show more of the market but use more bandwidth. Requires page refresh to take effect.
            </div>
            <select
              value={orderBookDepth}
              onChange={e => setOrderBookDepth(Number(e.target.value))}
              style={{ ...inputStyle, marginTop: 8, width: 'auto', minWidth: 120 }}
            >
              {[10, 25, 100, 500, 1000].map(d => (
                <option key={d} value={d}>{d} levels</option>
              ))}
            </select>
          </div>

          <div style={cardStyle}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
              <div>
                <div style={labelStyle}>Auto-Sell on Buy Fill</div>
                <div style={hintStyle}>
                  When enabled, automatically creates a sell order when a buy order fills completely. The sell price is set at the configured percentage above the buy price.
                </div>
              </div>
              <label className="toggle" style={{ flexShrink: 0, marginLeft: 16 }}>
                <input
                  type="checkbox"
                  checked={autoSellOnBuyFill}
                  onChange={e => setAutoSellOnBuyFill(e.target.checked)}
                />
                <span className="toggle-slider" />
              </label>
            </div>
            {autoSellOnBuyFill && (
              <div style={{ marginTop: 16 }}>
                <div style={labelStyle}>Sell Price Markup</div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 8 }}>
                  <input
                    type="range"
                    min="1"
                    max="500"
                    step="1"
                    value={autoSellPercentage}
                    onChange={e => setAutoSellPercentage(parseFloat(e.target.value))}
                    style={{ flex: 1 }}
                  />
                  <input
                    type="number"
                    min="1"
                    max="500"
                    step="0.1"
                    value={autoSellPercentage}
                    onChange={e => {
                      const v = parseFloat(e.target.value);
                      if (!isNaN(v)) setAutoSellPercentage(Math.min(500, Math.max(1, v)));
                    }}
                    style={{ width: 70, padding: '4px 8px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', textAlign: 'center', fontSize: 14 }}
                  />
                  <span style={{ color: 'var(--text-muted)' }}>%</span>
                </div>
                <div style={hintStyle}>Sell order will be placed at this percentage above the buy price (1% - 500%)</div>
              </div>
            )}
          </div>

          <button onClick={handleSave} style={buttonStyle}>Save General Settings</button>
          {saveStatus && <span style={{ marginLeft: 12, color: saveStatus.includes('Error') ? 'var(--red)' : 'var(--green)' }}>{saveStatus}</span>}
        </>
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

      {activeTab === 'schedule' && (
        <>
          <div style={cardStyle}>
            <div style={labelStyle}>Daily Price Download Time</div>
            <div style={hintStyle}>
              The app refreshes all coin prices once per day by fetching fresh OHLC data from Kraken. Choose when this runs — off-peak hours (e.g. 4am) are recommended.
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 12 }}>
              <input
                type="time"
                value={priceDownloadTime}
                onChange={e => setPriceDownloadTime(e.target.value)}
                style={{ padding: '8px 12px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 16, width: 140 }}
              />
            </div>
            <div style={{ display: 'flex', gap: 8, marginTop: 12, flexWrap: 'wrap' }}>
              {[['00:00', 'Midnight'], ['02:00', '2 AM'], ['04:00', '4 AM'], ['06:00', '6 AM'], ['08:00', '8 AM'], ['12:00', 'Noon']].map(([t, label]) => (
                <button
                  key={t}
                  onClick={() => setPriceDownloadTime(t)}
                  style={{
                    padding: '4px 12px', border: '1px solid var(--border)', borderRadius: 4,
                    background: priceDownloadTime === t ? 'var(--green)' : 'var(--bg-primary)',
                    color: priceDownloadTime === t ? 'white' : 'var(--text-primary)',
                    cursor: 'pointer', fontSize: 13
                  }}>
                  {label}
                </button>
              ))}
            </div>
            <div style={{ ...hintStyle, marginTop: 8 }}>
              Cron: <code style={{ background: 'var(--bg-primary)', padding: '1px 6px', borderRadius: 3, fontSize: 12 }}>
                {(() => { const [hh, mm] = priceDownloadTime.split(':'); return `${parseInt(mm || 0)} ${parseInt(hh || 4)} * * *`; })()}
              </code>
            </div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Daily ML Prediction Job Time</div>
            <div style={hintStyle}>
              When the prediction model training and inference job runs each day. Choose a time after the price download completes.
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 12 }}>
              <input
                type="time"
                value={predictionJobTime}
                onChange={e => setPredictionJobTime(e.target.value)}
                style={{ padding: '8px 12px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 16, width: 140 }}
              />
            </div>
            <div style={{ display: 'flex', gap: 8, marginTop: 12, flexWrap: 'wrap' }}>
              {[['01:00', '1 AM'], ['03:00', '3 AM'], ['05:00', '5 AM'], ['06:00', '6 AM'], ['08:00', '8 AM'], ['12:00', 'Noon']].map(([t, label]) => (
                <button
                  key={t}
                  onClick={() => setPredictionJobTime(t)}
                  style={{
                    padding: '4px 12px', border: '1px solid var(--border)', borderRadius: 4,
                    background: predictionJobTime === t ? 'var(--green)' : 'var(--bg-primary)',
                    color: predictionJobTime === t ? 'white' : 'var(--text-primary)',
                    cursor: 'pointer', fontSize: 13
                  }}>
                  {label}
                </button>
              ))}
            </div>
            <div style={{ ...hintStyle, marginTop: 8 }}>
              Cron: <code style={{ background: 'var(--bg-primary)', padding: '1px 6px', borderRadius: 3, fontSize: 12 }}>
                {(() => { const [hh, mm] = predictionJobTime.split(':'); return `${parseInt(mm || 0)} ${parseInt(hh || 5)} * * *`; })()}
              </code>
            </div>
          </div>

          <div style={cardStyle}>
            <div style={labelStyle}>Auto-Refresh Stale Predictions</div>
            <div style={hintStyle}>
              Runs at 1 minute past the hour and repeats at this interval. Any prediction card older than {' '}
              <strong>30 minutes</strong> will be automatically re-generated.
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 12 }}>
              <input
                type="number"
                min={5}
                max={60}
                value={predictionAutoRefreshInterval}
                onChange={e => setPredictionAutoRefreshInterval(Math.max(5, Math.min(60, Number(e.target.value) || 15)))}
                style={{ padding: '8px 12px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', fontSize: 16, width: 80 }}
              />
              <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>minutes</span>
            </div>
            <div style={{ display: 'flex', gap: 8, marginTop: 12, flexWrap: 'wrap' }}>
              {[5, 10, 15, 20, 30].map(v => (
                <button
                  key={v}
                  onClick={() => setPredictionAutoRefreshInterval(v)}
                  style={{
                    padding: '4px 12px', border: '1px solid var(--border)', borderRadius: 4,
                    background: predictionAutoRefreshInterval === v ? 'var(--green)' : 'var(--bg-primary)',
                    color: predictionAutoRefreshInterval === v ? 'white' : 'var(--text-primary)',
                    cursor: 'pointer', fontSize: 13
                  }}>
                  {v} min
                </button>
              ))}
            </div>
            <div style={{ ...hintStyle, marginTop: 8 }}>
              Cron: <code style={{ background: 'var(--bg-primary)', padding: '1px 6px', borderRadius: 3, fontSize: 12 }}>
                {(() => {
                  const n = Math.max(1, predictionAutoRefreshInterval);
                  const mins = [];
                  for (let m = 1; m < 60; m += n) mins.push(m);
                  return `${mins.join(',')} * * * *`;
                })()}
              </code>
            </div>
          </div>

          {scheduleInfo && (
            <div style={cardStyle}>
              <div style={labelStyle}>Current Schedule Status</div>
              <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 16px', marginTop: 8 }}>
                <span style={{ color: 'var(--text-muted)', fontSize: 13 }}>Next run</span>
                <span style={{ fontSize: 13 }}>{scheduleInfo.nextExecution ? new Date(scheduleInfo.nextExecution).toLocaleString() : '—'}</span>
                <span style={{ color: 'var(--text-muted)', fontSize: 13 }}>Last run</span>
                <span style={{ fontSize: 13 }}>{scheduleInfo.lastExecution ? new Date(scheduleInfo.lastExecution).toLocaleString() : 'Never'}</span>
                <span style={{ color: 'var(--text-muted)', fontSize: 13 }}>Last result</span>
                <span style={{ fontSize: 13, color: scheduleInfo.lastJobState === 'Succeeded' ? 'var(--green)' : scheduleInfo.lastJobState ? 'var(--red)' : 'var(--text-muted)' }}>
                  {scheduleInfo.lastJobState || 'No runs yet'}
                </span>
              </div>
              <button
                onClick={fetchScheduleInfo}
                style={{ marginTop: 12, padding: '4px 12px', border: '1px solid var(--border)', borderRadius: 4, background: 'var(--bg-primary)', color: 'var(--text-primary)', cursor: 'pointer', fontSize: 13 }}>
                Refresh Status
              </button>
            </div>
          )}

          <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
            <button onClick={handleSave} style={buttonStyle}>Save Schedule</button>
            <button
              onClick={handleTriggerNow}
              style={{ ...buttonStyle, background: 'var(--blue, #3b82f6)' }}>
              Run Now
            </button>
            <a
              href="/hangfire"
              target="_blank"
              rel="noopener noreferrer"
              style={{ ...buttonStyle, background: 'var(--bg-card)', color: 'var(--text-primary)', border: '1px solid var(--border)', textDecoration: 'none', display: 'inline-block' }}>
              Hangfire Dashboard ↗
            </a>
          </div>
          {saveStatus && <span style={{ marginLeft: 0, marginTop: 8, display: 'block', color: saveStatus.includes('Error') ? 'var(--red)' : 'var(--green)' }}>{saveStatus}</span>}
          {triggerStatus && <span style={{ marginTop: 8, display: 'block', color: triggerStatus.includes('Error') ? 'var(--red)' : 'var(--green)' }}>{triggerStatus}</span>}
        </>
      )}
    </div>
  );
}
