import { useEffect, useState } from 'react';
import TabLayout from './components/TabLayout';
import api from './api/apiClient';
import { startConnection } from './api/signalRService';
import { ThemeProvider } from './context/ThemeContext';

export default function App() {
  const [totalValue, setTotalValue] = useState(0);
  const [usdGbpRate, setUsdGbpRate] = useState(0);

  useEffect(() => {
    const fetchBalances = () => {
      api.get('/balances').then(r => {
        const balances = r.data.balances || [];
        setTotalValue(balances.reduce((sum, b) => sum + b.latestValue, 0));
        if (r.data.usdGbpRate) setUsdGbpRate(r.data.usdGbpRate);
      }).catch(() => {});
    };

    fetchBalances();
    const interval = setInterval(fetchBalances, 30000);

    startConnection().then(conn => {
      conn.on('BalanceUpdate', (data, rate) => {
        const total = data.reduce((sum, b) => sum + b.latestValue, 0);
        setTotalValue(total);
        if (rate != null) setUsdGbpRate(rate);
      });
      conn.on('AppShutdown', () => {
        document.title = 'Shutting down...';
        setTimeout(() => window.close(), 1000);
      });
    });

    return () => clearInterval(interval);
  }, []);

  return (
    <ThemeProvider>
      <TabLayout totalValue={totalValue} usdGbpRate={usdGbpRate} />
    </ThemeProvider>
  );
}
