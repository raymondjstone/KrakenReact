import { useState, useEffect, useRef } from 'react';
import { createChart, CandlestickSeries } from 'lightweight-charts';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { useTheme } from '../context/ThemeContext';

const CHART_THEMES = {
  light: {
    bg: '#f8f9fa', text: '#5e6673', grid: '#e8eaed', border: '#e0e3e8',
        up: '#0b8c50', down: '#c9304e', buyorder: '#0b6c50', sellorder: '#a9304e' 
  },
  dark: {
    bg: '#0b0e11', text: '#848e9c', grid: '#1e2329', border: '#2e3440',
      up: '#0ecb81', down: '#f6465d', buyorder: '#0e6b81', sellorder: '#d6465d',

  },
};

const INTERVALS = [
  { key: '1', label: '1m' },
  { key: '5', label: '5m' },
  { key: '15', label: '15m' },
  { key: '30', label: '30m' },
  { key: '60', label: '1H' },
  { key: '240', label: '4H' },
  { key: '1D', label: '1D' },
  { key: '1W', label: '1W' },
];

// Intervals where candles represent less than a day — show time on the axis
const INTRADAY = new Set(['1', '5', '15', '30', '60', '240']);

export default function ChartPage({ symbol, displaySymbol, chartId }) {
  const chartContainerRef = useRef();
  const chartRef = useRef(null);
  const seriesRef = useRef(null);
  const orderLinesRef = useRef([]);
  const resizeHandlerRef = useRef(null);
  const resizeObserverRef = useRef(null);
  const { theme } = useTheme();
  const intervalKey = chartId ? `kraken_chart_interval_${chartId}` : 'kraken_chart_interval';
  const [interval, setInterval_] = useState(() =>
    localStorage.getItem(intervalKey) || localStorage.getItem('kraken_chart_interval') || '1D'
  );
  const [loading, setLoading] = useState(true);
  const [noData, setNoData] = useState(false);

  const changeInterval = (iv) => {
    setInterval_(iv);
    localStorage.setItem(intervalKey, iv);
  };

  useEffect(() => {
    const container = chartContainerRef.current;
    if (!container) return;

    let handler = null;
    let orderHandler = null;
    let disposed = false;
    const conn = getConnection();
    const colors = CHART_THEMES[theme] || CHART_THEMES.light;
    const isIntraday = INTRADAY.has(interval);

    if (chartRef.current) {
      chartRef.current.remove();
      chartRef.current = null;
      seriesRef.current = null;
      orderLinesRef.current = [];
    }

    function updateOrderLines() {
      if (disposed || !seriesRef.current) return;
      orderLinesRef.current.forEach(line => {
        try { seriesRef.current.removePriceLine(line); } catch { /* line already removed */ }
      });
      orderLinesRef.current = [];

      api.get('/orders').then(r => {
        if (disposed || !seriesRef.current) return;
        const symbolNoSlash = symbol.replace('/', '');
        const openOrders = r.data.filter(o =>
          o.symbol === symbolNoSlash &&
          (o.status === 'Open' || o.status === 'New' || o.status === 'PendingNew')
        );
        openOrders.forEach(order => {
          try {
            const line = seriesRef.current.createPriceLine({
              price: Number(order.price),
              color: order.side === 'Buy' ? colors.buyorder : colors.sellorder,
              lineWidth: 1,
              lineStyle: 2,
              axisLabelVisible: true,
              title: `${order.side} ${order.quantity}`,
            });
            orderLinesRef.current.push(line);
          } catch { /* ignore draw error */ }
        });
      }).catch(() => {});
    }

    setLoading(true);
    setNoData(false);


    function tryCreate() {
      if (disposed || chartRef.current) return;
      const w = container.clientWidth;
      const h = container.clientHeight;
      if (w < 10 || h < 10) return;

      const chart = createChart(container, {
        width: w,
        height: h,
        layout: { background: { color: colors.bg }, textColor: colors.text },
        grid: { vertLines: { color: colors.grid }, horzLines: { color: colors.grid } },
        crosshair: { mode: 0 },
        timeScale: { timeVisible: isIntraday, secondsVisible: false, borderColor: colors.border },
        rightPriceScale: { borderColor: colors.border },
      });
      chartRef.current = chart;

      const candleSeries = chart.addSeries(CandlestickSeries, {
        upColor: colors.up, downColor: colors.down,
        borderDownColor: colors.down, borderUpColor: colors.up,
        wickDownColor: colors.down, wickUpColor: colors.up,
        lastValueMark: {
          visible: true,
          color: '#0000ff', // custom color for the last value line
          text: 'Last',
        },
      });
      seriesRef.current = candleSeries;

      // Double rAF to ensure flexbox/layout is complete before resizing
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          if (chartRef.current && container) {
            chartRef.current.applyOptions({ width: container.clientWidth, height: container.clientHeight });
          }
        });
      });

      const encodedSymbol = encodeURIComponent(symbol);
      api.get(`/prices/${encodedSymbol}/klines?interval=${interval}`).then(r => {
        console.log(`[Chart] ${symbol} interval=${interval}: ${r.data.length} raw klines, disposed=${disposed}`);
        if (r.data.length > 0) console.log('[Chart] sample:', r.data[0]);
        if (disposed) return;
        const data = r.data
          .filter(k => k.openTime && k.open > 0)
          .map(k => ({
            time: Math.floor(new Date(k.openTime).getTime() / 1000),
            open: Number(k.open), high: Number(k.high), low: Number(k.low), close: Number(k.close),
          }))
          .sort((a, b) => a.time - b.time)
          .filter((v, i, arr) => i === 0 || v.time !== arr[i - 1].time);
        console.log(`[Chart] ${symbol}: ${data.length} after filter`);
        if (data.length) {
          candleSeries.setData(data);
          if (!disposed) { setLoading(false); setNoData(false); }
        } else {
          if (!disposed) { setLoading(false); setNoData(true); }
        }

        // Double rAF after data load to ensure chart fills container
        requestAnimationFrame(() => {
          requestAnimationFrame(() => {
            if (chartRef.current && container) {
              chartRef.current.applyOptions({ width: container.clientWidth, height: container.clientHeight });
            }
          });
        });

        updateOrderLines();
      }).catch((err) => {
        console.error(err);
        if (!disposed) { setLoading(false); setNoData(true); }
      });

      // For intraday charts, merge live ticker updates into the current candle
      const liveCandle = { time: 0, open: 0, high: 0, low: 0, close: 0 };

      handler = (data) => {
        if (data.symbol !== symbol || !data.closePrice) return;
        try {
          const close = Number(data.closePrice);
          const high = Number(data.highPrice || close);
          const low = Number(data.lowPrice || close);
          const open = Number(data.openPrice || close);

          // Compute the bucket start time for the current interval
          const now = new Date();
          let bucketTime;
          if (interval === '1D' || interval === '1W') {
            bucketTime = Math.floor(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()) / 1000);
          } else {
            const mins = parseInt(interval, 10) || 60;
            const secs = mins * 60;
            bucketTime = Math.floor(now.getTime() / 1000 / secs) * secs;
          }

          if (liveCandle.time !== bucketTime) {
            liveCandle.time = bucketTime;
            liveCandle.open = open;
            liveCandle.high = high;
            liveCandle.low = low;
          } else {
            if (high > liveCandle.high) liveCandle.high = high;
            if (low < liveCandle.low) liveCandle.low = low;
          }
          liveCandle.close = close;

          candleSeries.update({
            time: liveCandle.time,
            open: liveCandle.open,
            high: liveCandle.high,
            low: liveCandle.low,
            close: liveCandle.close,
          });
        } catch { /* ignore update error */ }
      };
      conn.on('TickerUpdate', handler);

      orderHandler = () => updateOrderLines();
      conn.on('OrderUpdate', orderHandler);
      conn.on('ExecutionUpdate', orderHandler);
    }

    // Remove previous resize handler if any (from prior effect run)
    if (resizeHandlerRef.current) {
      window.removeEventListener('resize', resizeHandlerRef.current);
    }

    // ResizeObserver for robust container resize detection
    if (resizeObserverRef.current) {
      resizeObserverRef.current.disconnect();
      resizeObserverRef.current = null;
    }

    const handleResize = () => {
      if (!chartRef.current) {
        tryCreate();
      } else if (container) {
        const w = container.clientWidth;
        const h = container.clientHeight;
        if (w > 10 && h > 10) {
          chartRef.current.applyOptions({ width: w, height: h });
        }
      }
    };
    resizeHandlerRef.current = handleResize;

    // Observe container size changes
    if (container) {
      resizeObserverRef.current = new window.ResizeObserver(() => {
        if (chartRef.current && container) {
          const w = container.clientWidth;
          const h = container.clientHeight;
          if (w > 10 && h > 10) {
            chartRef.current.applyOptions({ width: w, height: h });
          }
        }
      });
      resizeObserverRef.current.observe(container);
    }

    const frameId = requestAnimationFrame(tryCreate);
    window.addEventListener('resize', handleResize);

    return () => {
      disposed = true;
      cancelAnimationFrame(frameId);
      window.removeEventListener('resize', handleResize);
      resizeHandlerRef.current = null;
      if (resizeObserverRef.current) {
        resizeObserverRef.current.disconnect();
        resizeObserverRef.current = null;
      }
      if (handler) conn.off('TickerUpdate', handler);
      if (orderHandler) {
        conn.off('OrderUpdate', orderHandler);
        conn.off('ExecutionUpdate', orderHandler);
      }
      if (chartRef.current) {
        chartRef.current.remove();
        chartRef.current = null;
        seriesRef.current = null;
        orderLinesRef.current = [];
      }
    };
  }, [symbol, theme, interval]);

  const btnBase = {
    border: 'none', cursor: 'pointer', padding: '3px 8px', borderRadius: 3,
    fontSize: 11, fontWeight: 600, transition: 'all 0.15s',
  };

  return (
    <div style={{ height: '100%', background: 'var(--bg-primary)', padding: 8, display: 'flex', flexDirection: 'column' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4, flexShrink: 0 }}>
        <span style={{ color: 'var(--yellow)', fontSize: 14, fontWeight: 600 }}>
          {displaySymbol || symbol}
        </span>
        <div style={{ display: 'flex', gap: 2, marginLeft: 12 }}>
          {INTERVALS.map(iv => (
            <button
              key={iv.key}
              onClick={() => changeInterval(iv.key)}
              style={{
                ...btnBase,
                background: interval === iv.key ? 'var(--yellow)' : 'var(--bg-input)',
                color: interval === iv.key ? '#0b0e11' : 'var(--text-secondary)',
              }}
            >
              {iv.label}
            </button>
          ))}
        </div>
      </div>
      <div style={{ flex: 1, minHeight: 0, position: 'relative' }}>
        <div ref={chartContainerRef} style={{ width: '100%', height: '100%' }} />
        {loading && (
          <div style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--bg-primary)', zIndex: 2 }}>
            <div style={{ color: 'var(--text-muted)', fontSize: 13 }}>Loading chart data...</div>
          </div>
        )}
        {!loading && noData && (
          <div style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--bg-primary)', zIndex: 2 }}>
            <div style={{ color: 'var(--text-muted)', fontSize: 13 }}>No data available for {displaySymbol || symbol}</div>
          </div>
        )}
      </div>
    </div>
  );
}
