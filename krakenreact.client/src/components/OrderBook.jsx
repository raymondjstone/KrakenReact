import { useState, useEffect, useRef, useCallback } from 'react';
import { getConnection } from '../api/signalRService';
import { formatPrice } from '../utils/formatters';

export default function OrderBook({ symbol }) {
  const [asks, setAsks] = useState([]);
  const [bids, setBids] = useState([]);
  const asksRef = useRef([]);
  const bidsRef = useRef([]);
  const lastPairRef = useRef(null);

  const applyUpdates = useCallback((current, updates) => {
    if (!updates) return current;
    const map = new Map(current.map(l => [l[0], l[1]]));
    for (const [price, volume] of updates) {
      if (volume === 0) map.delete(price);
      else map.set(price, volume);
    }
    return Array.from(map.entries()).map(([p, v]) => [p, v]);
  }, []);

  useEffect(() => {
    if (!symbol) return;

    const conn = getConnection();

    // Tell backend which pair we want
    conn.invoke('SubscribeBook', symbol).catch(() => {});
    lastPairRef.current = symbol;

    const snapshotHandler = (data) => {
      if (data.pair !== symbol) return;
      const sortedAsks = (data.asks || []).sort((a, b) => a[0] - b[0]);
      const sortedBids = (data.bids || []).sort((a, b) => b[0] - a[0]);
      asksRef.current = sortedAsks;
      bidsRef.current = sortedBids;
      setAsks(sortedAsks);
      setBids(sortedBids);
    };

    const updateHandler = (data) => {
      if (data.pair !== symbol) return;
      if (data.asks) {
        asksRef.current = applyUpdates(asksRef.current, data.asks).sort((a, b) => a[0] - b[0]).slice(0, 10);
        setAsks([...asksRef.current]);
      }
      if (data.bids) {
        bidsRef.current = applyUpdates(bidsRef.current, data.bids).sort((a, b) => b[0] - a[0]).slice(0, 10);
        setBids([...bidsRef.current]);
      }
    };

    conn.on('BookSnapshot', snapshotHandler);
    conn.on('BookUpdate', updateHandler);

    return () => {
      conn.off('BookSnapshot', snapshotHandler);
      conn.off('BookUpdate', updateHandler);
    };
  }, [symbol, applyUpdates]);

  const maxQty = Math.max(
    ...asks.map(a => a[1] || 0),
    ...bids.map(b => b[1] || 0),
    0.0001
  );

  // Show asks in reverse so lowest ask is at bottom (nearest to spread)
  const displayAsks = asks.slice(0, 10).reverse();
  const displayBids = bids.slice(0, 10);

  const spread = asks.length > 0 && bids.length > 0 ? asks[0][0] - bids[0][0] : null;

  return (
    <div className="orderbook">
      <div className="orderbook-header">Order Book</div>
      <div className="orderbook-labels">
        <span>Price</span>
        <span>Qty</span>
      </div>
      <div className="orderbook-asks">
        {displayAsks.map(([price, qty], i) => (
          <div key={i} className="orderbook-row ask">
            <div className="orderbook-bar ask-bar" style={{ width: `${(qty / maxQty) * 100}%` }} />
            <span className="orderbook-price">{formatPrice(price)}</span>
            <span className="orderbook-qty">{formatQty(qty)}</span>
          </div>
        ))}
      </div>
      {spread != null && (
        <div className="orderbook-spread">
          Spread: {formatPrice(spread)}
        </div>
      )}
      <div className="orderbook-bids">
        {displayBids.map(([price, qty], i) => (
          <div key={i} className="orderbook-row bid">
            <div className="orderbook-bar bid-bar" style={{ width: `${(qty / maxQty) * 100}%` }} />
            <span className="orderbook-price">{formatPrice(price)}</span>
            <span className="orderbook-qty">{formatQty(qty)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

function formatQty(value) {
  if (value == null) return '';
  const num = Number(value);
  if (num >= 1000) return num.toFixed(1);
  if (num >= 1) return num.toFixed(3);
  return num.toFixed(6);
}
