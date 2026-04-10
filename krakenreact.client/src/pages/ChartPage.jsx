import { useEffect, useRef } from 'react';
import { createChart, CandlestickSeries } from 'lightweight-charts';
import api from '../api/apiClient';
import { getConnection } from '../api/signalRService';
import { useTheme } from '../context/ThemeContext';

const CHART_THEMES = {
  light: {
    bg: '#f8f9fa', text: '#5e6673', grid: '#e8eaed', border: '#e0e3e8',
    up: '#0b8c50', down: '#c9304e',
  },
  dark: {
    bg: '#0b0e11', text: '#848e9c', grid: '#1e2329', border: '#2e3440',
    up: '#0ecb81', down: '#f6465d',
  },
};

export default function ChartPage({ symbol }) {
  const chartContainerRef = useRef();
  const chartRef = useRef(null);
  const seriesRef = useRef(null);
  const orderLinesRef = useRef([]);
  const { theme } = useTheme();

  useEffect(() => {
    const container = chartContainerRef.current;
    if (!container) return;

    let handler = null;
    let orderHandler = null;
    let disposed = false;
    const conn = getConnection();
    const colors = CHART_THEMES[theme] || CHART_THEMES.light;

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
              color: order.side === 'Buy' ? colors.up : colors.down,
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
        timeScale: { timeVisible: true, secondsVisible: false, borderColor: colors.border },
        rightPriceScale: { borderColor: colors.border },
      });
      chartRef.current = chart;

      const candleSeries = chart.addSeries(CandlestickSeries, {
        upColor: colors.up, downColor: colors.down,
        borderDownColor: colors.down, borderUpColor: colors.up,
        wickDownColor: colors.down, wickUpColor: colors.up,
      });
      seriesRef.current = candleSeries;

      const encodedSymbol = encodeURIComponent(symbol);
      api.get(`/prices/${encodedSymbol}/klines`).then(r => {
        if (disposed) return;
        const data = r.data
          .filter(k => k.openTime && k.open > 0)
          .map(k => ({
            time: Math.floor(new Date(k.openTime).getTime() / 1000),
            open: Number(k.open), high: Number(k.high), low: Number(k.low), close: Number(k.close),
          }))
          .sort((a, b) => a.time - b.time)
          .filter((v, i, arr) => i === 0 || v.time !== arr[i - 1].time);
        if (data.length) candleSeries.setData(data);

        updateOrderLines();
      }).catch(console.error);

      // Track the current day's OHLC so ticker updates merge into one daily candle
      const todayCandle = { time: 0, open: 0, high: 0, low: 0, close: 0 };

      handler = (data) => {
        if (data.symbol === symbol && data.closePrice) {
          try {
            // Floor to start of UTC day to match the daily kline interval
            const now = new Date();
            const dayTime = Math.floor(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()) / 1000);
            const close = Number(data.closePrice);
            const high = Number(data.highPrice || close);
            const low = Number(data.lowPrice || close);
            const open = Number(data.openPrice || close);

            if (todayCandle.time !== dayTime) {
              // New day — reset
              todayCandle.time = dayTime;
              todayCandle.open = open;
              todayCandle.high = high;
              todayCandle.low = low;
            } else {
              // Same day — extend high/low
              if (high > todayCandle.high) todayCandle.high = high;
              if (low < todayCandle.low) todayCandle.low = low;
            }
            todayCandle.close = close;

            candleSeries.update({
              time: todayCandle.time,
              open: todayCandle.open,
              high: todayCandle.high,
              low: todayCandle.low,
              close: todayCandle.close,
            });
          } catch { /* ignore update error */ }
        }
      };
      conn.on('TickerUpdate', handler);

      orderHandler = () => updateOrderLines();
      conn.on('OrderUpdate', orderHandler);
      conn.on('ExecutionUpdate', orderHandler);
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

    const frameId = requestAnimationFrame(tryCreate);
    window.addEventListener('resize', handleResize);

    return () => {
      disposed = true;
      cancelAnimationFrame(frameId);
      window.removeEventListener('resize', handleResize);
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
  }, [symbol, theme]);

  return (
    <div style={{ height: '100%', background: 'var(--bg-primary)', padding: 8, display: 'flex', flexDirection: 'column' }}>
      <div style={{ color: 'var(--yellow)', fontSize: 14, fontWeight: 600, marginBottom: 4, flexShrink: 0 }}>
        {symbol}
      </div>
      <div ref={chartContainerRef} style={{ flex: 1, minHeight: 0 }} />
    </div>
  );
}
