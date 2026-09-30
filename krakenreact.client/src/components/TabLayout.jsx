import { lazy, Suspense, useState, useEffect, useCallback } from 'react';
import Dashboard from './Dashboard';
import AlertCentre from './AlertCentre';
import SettingsPage, { loadSettings, saveSettings } from '../pages/SettingsPage';
import api, { reportError, errorMessage } from '../api/apiClient';

// Every other page loads on first visit, so the initial bundle only carries the dashboard, alert centre and
// settings (the charting and grid libraries used by the rest were all downloaded up front).
const InfoPage = lazy(() => import('../pages/InfoPage'));
const BalancesPage = lazy(() => import('../pages/BalancesPage'));
const AutoTradePage = lazy(() => import('../pages/AutoTradePage'));
const GroupedTradesPage = lazy(() => import('../pages/GroupedTradesPage'));
const TradesPage = lazy(() => import('../pages/TradesPage'));
const PairedTradesPage = lazy(() => import('../pages/PairedTradesPage'));
const OrdersPage = lazy(() => import('../pages/OrdersPage'));
const LedgerPage = lazy(() => import('../pages/LedgerPage'));
const ChartPage = lazy(() => import('../pages/ChartPage'));
const DelistedPairsPage = lazy(() => import('../pages/DelistedPairsPage'));
const PredictionPage = lazy(() => import('../pages/PredictionPage'));
const MarketAnalysisPage = lazy(() => import('../pages/MarketAnalysisPage'));
const TaxReportPage = lazy(() => import('../pages/TaxReportPage'));
const PriceAlertsPage = lazy(() => import('../pages/PriceAlertsPage'));
const AnalyticsPage = lazy(() => import('../pages/AnalyticsPage'));
const DcaPage = lazy(() => import('../pages/DcaPage'));
const HealthPage = lazy(() => import('../pages/HealthPage'));
const StakingPage = lazy(() => import('../pages/StakingPage'));
const RebalancePage = lazy(() => import('../pages/RebalancePage'));
const FundingRatesPage = lazy(() => import('../pages/FundingRatesPage'));
const ProfitLadderPage = lazy(() => import('../pages/ProfitLadderPage'));
const MicroTradePage = lazy(() => import('../pages/MicroTradePage'));
const RealizedPnLPage = lazy(() => import('../pages/RealizedPnLPage'));
const ScheduledOrdersPage = lazy(() => import('../pages/ScheduledOrdersPage'));
const AutoRepricePage = lazy(() => import('../pages/AutoRepricePage'));
import { getConnection } from '../api/signalRService';
import { useTheme } from '../context/ThemeContext';

const DEFAULT_CONFIG = {
  showTickers: true,
  showChart: true,
  showWatchlist: true,
  showOrders: true,
};

const fixedTabs = [
  { id: 'dashboard', label: 'Dashboard' },
  { id: 'info', label: 'Prices' },
  { id: 'balances', label: 'Balances' },
  { id: 'autotrade', label: 'AutoTrade' },
  { id: 'grouptrades', label: 'Grouped Trades' },
  { id: 'trades', label: 'Trades' },
  { id: 'pairedtrades', label: 'Buy/Sell Pairs' },
  { id: 'orders', label: 'Orders' },
  { id: 'ledger', label: 'Ledger' },
  { id: 'delisted', label: 'Delisted Pairs' },
  { id: 'predictions', label: 'Predictions' },
  { id: 'pricealerts', label: 'Price Alerts' },
  { id: 'analytics', label: 'Analytics' },
  { id: 'marketanalysis', label: 'Market Analysis' },
  { id: 'tax', label: 'Tax Report' },
  { id: 'dca', label: 'DCA' },
  { id: 'profitladder', label: 'Profit Ladder' },
  { id: 'microtrade', label: 'Micro Trading' },
  { id: 'staking', label: 'Staking' },
  { id: 'rebalance', label: 'Rebalance' },
  { id: 'funding', label: 'Funding Rates' },
  { id: 'scheduledorders', label: 'Sched. Orders' },
  { id: 'autoreprice', label: 'Auto Reprice' },
  { id: 'realizedpnl', label: 'Realized P&L' },
  { id: 'health', label: 'Health' },
];

export default function TabLayout({ totalValue, totalValueGbp }) {
  const [activeTab, setActiveTab] = useState('dashboard');
  const [chartTabs, setChartTabs] = useState([]);
  const [showConfig, setShowConfig] = useState(false);
  const [statusText, setStatusText] = useState('');
  const [pinnedSymbols, setPinnedSymbols] = useState([]);
  const [appSettings, setAppSettings] = useState(loadSettings);
  const [serverSettings, setServerSettings] = useState(null);
  const { toggleTheme, isDark } = useTheme();

  // Load server settings and pinned pairs from database on mount
  const loadServerSettings = useCallback(() => {
    api.get('/settings').then(r => setServerSettings(r.data))
      .catch(err => console.error('Failed to load server settings:', err));
  }, []);

  useEffect(() => {
    loadServerSettings();
    api.get('/settings/pinned-pairs')
      .then(r => setPinnedSymbols(r.data))
      .catch(() => setPinnedSymbols(['XBT/USD', 'ETH/USD', 'SOL/USD']));
  }, []);

  const savePinned = (list) => {
    setPinnedSymbols(list);
    api.put('/settings/pinned-pairs', list).catch(e => reportError(errorMessage(e, 'Could not save pinned pairs')));
  };
  const pinSymbol = (symbol) => {
    if (!pinnedSymbols.includes(symbol)) savePinned([...pinnedSymbols, symbol]);
  };
  const unpinSymbol = (symbol) => {
    savePinned(pinnedSymbols.filter(s => s !== symbol));
  };
  const handleSettingsChange = (updated) => {
    setAppSettings(updated);
    saveSettings(updated);
  };
  const pinnedSet = new Set(pinnedSymbols);
  const [config, setConfig] = useState(() => {
    try {
      const saved = localStorage.getItem('kraken_dashboard_config');
      return saved ? { ...DEFAULT_CONFIG, ...JSON.parse(saved) } : DEFAULT_CONFIG;
    } catch {
      return DEFAULT_CONFIG;
    }
  });

  const updateConfig = (newConfig) => {
    setConfig(newConfig);
    localStorage.setItem('kraken_dashboard_config', JSON.stringify(newConfig));
  };

  useEffect(() => {
    const conn = getConnection();
    const handler = (msg) => setStatusText(msg);
    conn.on('StatusUpdate', handler);
    return () => conn.off('StatusUpdate', handler);
  }, []);

  const openChart = (symbol) => {
    if (!chartTabs.find(t => t.id === symbol)) {
      // Build a display-friendly label from the raw symbol using normalizations if available
      const norms = serverSettings?.assetNormalizations || {};
      const parts = symbol.split('/');
      const displayBase = norms[parts[0]] || parts[0];
      const displayCcy = parts[1] ? (norms[parts[1]] || parts[1]) : '';
      const label = displayCcy ? `${displayBase}/${displayCcy}` : displayBase;
      setChartTabs(prev => [...prev, { id: symbol, label }]);
    }
    setActiveTab(symbol);
  };

  const closeChart = (symbol, e) => {
    e.stopPropagation();
    setChartTabs(prev => prev.filter(t => t.id !== symbol));
    if (activeTab === symbol) setActiveTab('dashboard');
  };

  const handleShutdown = async () => {
    if (!window.confirm('Are you sure you want to shutdown the application?')) return;

    try {
      setStatusText('Shutting down...');
      await fetch('/api/shutdown', { method: 'POST' });
    } catch (err) {
      console.error('Shutdown error:', err);
      setStatusText('Shutdown error - server may already be stopped');
    }
  };

  useEffect(() => {
    // Double rAF ensures the flexbox layout has fully resolved before triggering resize
    requestAnimationFrame(() => {
      requestAnimationFrame(() => window.dispatchEvent(new Event('resize')));
    });
  }, [activeTab]);

  const allTabs = [...fixedTabs, ...chartTabs, { id: 'settings', label: 'Settings' }];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh' }}>
      <header className="app-header">
        <div className="app-logo">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2" />
          </svg>
          Kraken
        </div>

        <div className="nav-tabs">
          {allTabs.map(tab => (
            <button
              key={tab.id}
              className={`nav-tab${activeTab === tab.id ? ' active' : ''}`}
              onClick={() => setActiveTab(tab.id)}
            >
              {tab.label}
              {chartTabs.find(ct => ct.id === tab.id) && (
                <span className="close-btn" onClick={(e) => closeChart(tab.id, e)}>x</span>
              )}
            </button>
          ))}
        </div>

        <div className="header-right">
          <a
            href="https://buymeacoffee.com/raymondjstone"
            target="_blank"
            rel="noopener noreferrer"
            className="bmc-link"
            title="Support the developer — Buy me a coffee"
          >
            <img
              src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png"
              alt="Buy me a coffee"
              height="28"
            />
          </a>
          {statusText && (
            <div className="status-text" style={{ color: 'var(--text-muted)', fontSize: 11, marginRight: 12, maxWidth: 300, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {statusText}
            </div>
          )}
          <AlertCentre />
          <button className="settings-btn" onClick={toggleTheme} title="Toggle light/dark mode">
            {isDark ? '\u2600' : '\u263E'}
          </button>
          <button className="settings-btn" onClick={handleShutdown} title="Shutdown application" style={{ color: '#ef4444' }}>
            {'\u23FB'}
          </button>
          {activeTab === 'dashboard' && (
            <button className="settings-btn" onClick={() => setShowConfig(true)}>Layout</button>
          )}
          {totalValue > 0 && (
            <div className="portfolio-value">
              ${formatNum(totalValue)}
              {totalValueGbp > 0 && (
                <span style={{ color: 'var(--text-muted)', fontSize: '0.85em', marginLeft: 6 }}>
                  ({'\u00A3'}{formatNum(totalValueGbp)})
                </span>
              )}
            </div>
          )}
        </div>
      </header>

      <div style={{ flex: 1, overflow: 'hidden', position: 'relative' }}>
        <div style={{ position: 'absolute', inset: 0, display: activeTab === 'dashboard' ? 'block' : 'none' }}>
          <Dashboard config={config} pinnedSymbols={pinnedSymbols} pinnedSet={pinnedSet} onPin={pinSymbol} onUnpin={unpinSymbol} largeMovementThreshold={appSettings.largeMovementThreshold} hideAlmostZeroBalances={serverSettings?.hideAlmostZeroBalances} orderPriceOffsets={serverSettings?.orderPriceOffsets} orderQtyPercentages={serverSettings?.orderQtyPercentages} orderBookDepth={serverSettings?.orderBookDepth} />
        </div>
        <Suspense fallback={<div style={{ padding: 24, color: 'var(--text-muted)' }}>Loading…</div>}>
        {activeTab === 'info' && <InfoPage onSymbolClick={openChart} pinnedSet={pinnedSet} onPin={pinSymbol} onUnpin={unpinSymbol} />}
        {activeTab === 'balances' && <BalancesPage hideAlmostZeroBalances={serverSettings?.hideAlmostZeroBalances} />}
        {activeTab === 'autotrade' && <AutoTradePage />}
        {activeTab === 'grouptrades' && <GroupedTradesPage />}
        {activeTab === 'trades' && <TradesPage />}
        {activeTab === 'pairedtrades' && <PairedTradesPage />}
        {activeTab === 'orders' && <OrdersPage />}
        {activeTab === 'ledger' && <LedgerPage />}
        {activeTab === 'delisted' && <DelistedPairsPage />}
        {chartTabs.find(t => t.id === activeTab) && <ChartPage symbol={activeTab} displaySymbol={chartTabs.find(t => t.id === activeTab)?.label} />}
        {activeTab === 'predictions' && <PredictionPage onSymbolClick={openChart} />}
        {activeTab === 'pricealerts' && <PriceAlertsPage />}
        {activeTab === 'analytics' && <AnalyticsPage />}
        {activeTab === 'marketanalysis' && <MarketAnalysisPage />}
        {activeTab === 'tax' && <TaxReportPage />}
        {activeTab === 'dca' && <DcaPage />}
        {activeTab === 'profitladder' && <ProfitLadderPage />}
        {activeTab === 'microtrade' && <MicroTradePage />}
        {activeTab === 'staking' && <StakingPage />}
        {activeTab === 'rebalance' && <RebalancePage />}
        {activeTab === 'funding' && <FundingRatesPage />}
        {activeTab === 'scheduledorders' && <ScheduledOrdersPage />}
        {activeTab === 'autoreprice' && <AutoRepricePage />}
        {activeTab === 'realizedpnl' && <RealizedPnLPage />}
        {activeTab === 'health' && <HealthPage />}
        </Suspense>
        {activeTab === 'settings' && <SettingsPage settings={appSettings} onSettingsChange={handleSettingsChange} serverSettings={serverSettings} onServerSettingsRefresh={loadServerSettings} />}
      </div>

      {showConfig && (
        <div className="config-overlay" onClick={() => setShowConfig(false)}>
          <div className="config-panel" onClick={e => e.stopPropagation()}>
            <h3>Dashboard Layout</h3>
            {[
              ['showTickers', 'Ticker Cards'],
              ['showChart', 'Price Chart'],
              ['showWatchlist', 'Watchlist'],
              ['showOrders', 'Orders & Balances'],
            ].map(([key, label]) => (
              <div key={key} className="config-row">
                <label>{label}</label>
                <label className="toggle">
                  <input
                    type="checkbox"
                    checked={config[key]}
                    onChange={e => updateConfig({ ...config, [key]: e.target.checked })}
                  />
                  <span className="toggle-slider" />
                </label>
              </div>
            ))}
            <div style={{ marginTop: 16, textAlign: 'right' }}>
              <button className="btn btn-primary" onClick={() => setShowConfig(false)}>Done</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function formatNum(value) {
  return Number(value).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
