import { useEffect, useState } from 'react';
import TabLayout from './components/TabLayout';
import api from './api/apiClient';
import { startConnection, getConnection } from './api/signalRService';
import { ThemeProvider } from './context/ThemeContext';

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
    const interval = setInterval(fetchBalances, 30000);

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
      clearInterval(interval);
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

  return (
    <ThemeProvider>
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
