import { useEffect, useState } from 'react';
import TabLayout from './components/TabLayout';
import api from './api/apiClient';
import { startConnection, getConnection, getConnectionState, subscribeConnectionState } from './api/signalRService';
import { ThemeProvider } from './context/ThemeContext';
import { setVisibleInterval } from './utils/visibleInterval';

export default function App() {
  const [totalValue, setTotalValue] = useState(0);
  const [totalValueGbp, setTotalValueGbp] = useState(0);

  const updateFromBalances = (balances) => {
    setTotalValue(balances.reduce((sum, b) => sum + (b.latestValue || 0), 0));
    setTotalValueGbp(balances.reduce((sum, b) => sum + (b.latestValueGbp || 0), 0));
  };

  useEffect(() => {
    let disposed = false;
    const fetchBalances = () => {
      if (disposed) return;
      api.get('/balances').then(r => {
        if (!disposed) updateFromBalances(r.data.balances || []);
      }).catch(() => {});
    };

    fetchBalances();
    const interval = setVisibleInterval(fetchBalances, 300000);

    const conn = getConnection();
    const balanceHandler = (data) => { if (!disposed) updateFromBalances(data); };
    const shutdownHandler = () => {
      document.title = 'Shutting down...';
      setTimeout(() => window.close(), 1000);
    };
    conn.on('BalanceUpdate', balanceHandler);
    conn.on('AppShutdown', shutdownHandler);
    startConnection();

    return () => {
      disposed = true;
      interval();
      conn.off('BalanceUpdate', balanceHandler);
      conn.off('AppShutdown', shutdownHandler);
    };
  }, []);

  // Banner for failures reported through reportError()
  const [errorText, setErrorText] = useState('');
  useEffect(() => {
    let timer;
    const handler = (e) => {
      setErrorText(String(e.detail || 'Something went wrong'));
      clearTimeout(timer);
      timer = setTimeout(() => setErrorText(''), 6000);
    };
    window.addEventListener('app-error', handler);
    return () => { window.removeEventListener('app-error', handler); clearTimeout(timer); };
  }, []);

  // Live-feed status: shown only once the connection has been down for a moment, so a brief blip or the initial connect is quiet
  const [feedDown, setFeedDown] = useState(false);
  useEffect(() => {
    let timer;
    const apply = (state) => {
      clearTimeout(timer);
      if (state === 'reconnecting' || state === 'disconnected') timer = setTimeout(() => setFeedDown(true), 3000);
      else setFeedDown(false);
    };
    apply(getConnectionState());
    const unsubscribe = subscribeConnectionState(apply);
    return () => { unsubscribe(); clearTimeout(timer); };
  }, []);

  return (
    <ThemeProvider>
      {feedDown && (
        <div
          role="status"
          style={{
            position: 'fixed', top: 0, left: 0, right: 0, zIndex: 10000, padding: '6px 12px', textAlign: 'center',
            background: 'var(--orange, #f59e0b)', color: '#000', fontSize: 13,
          }}
        >
          Live updates are disconnected - reconnecting. Prices and orders shown may be out of date.
        </div>
      )}
      <TabLayout totalValue={totalValue} totalValueGbp={totalValueGbp} />
      {errorText && (
        <div
          role="alert"
          onClick={() => setErrorText('')}
          style={{
            position: 'fixed', bottom: 16, left: '50%', transform: 'translateX(-50%)', zIndex: 10000,
            maxWidth: '80vw', padding: '10px 16px', borderRadius: 6, cursor: 'pointer',
            background: 'var(--red, #ef4444)', color: '#fff', fontSize: 13, boxShadow: '0 4px 14px rgba(0,0,0,.35)',
          }}
        >
          {errorText}
        </div>
      )}
    </ThemeProvider>
  );
}
