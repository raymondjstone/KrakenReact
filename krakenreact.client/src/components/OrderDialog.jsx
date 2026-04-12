import { useState, useEffect } from 'react';
import api from '../api/apiClient';

const DEFAULT_PRICE_OFFSETS = [2, 5, 10, 15];
const DEFAULT_QTY_PERCENTAGES = [5, 10, 20, 25, 50, 75, 100];

export default function OrderDialog({ isOpen, onClose, editOrder, symbol: initialSymbol, symbols, balanceContext, priceOffsets, qtyPercentages }) {
  const [symbol, setSymbol] = useState('');
  const [side, setSide] = useState('Buy');
  const [price, setPrice] = useState('');
  const [quantity, setQuantity] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const pOffsets = priceOffsets?.length ? priceOffsets : DEFAULT_PRICE_OFFSETS;
  const qPcts = qtyPercentages?.length ? qtyPercentages : DEFAULT_QTY_PERCENTAGES;

  useEffect(() => {
    if (!isOpen) return;
    setError('');
    if (editOrder) {
      setSymbol(editOrder.symbol);
      setSide(editOrder.side);
      setPrice(String(editOrder.price));
      setQuantity(String(editOrder.quantity));
    } else if (balanceContext) {
      setSymbol(balanceContext.symbol || '');
      setSide(balanceContext.uncoveredQty > 0.0001 ? 'Sell' : 'Buy');
      setPrice(balanceContext.price ? String(balanceContext.price) : '');
      setQuantity('');
    } else if (initialSymbol) {
      setSymbol(initialSymbol);
      setSide('Buy');
      setPrice('');
      setQuantity('');
    } else {
      setSymbol('');
      setSide('Buy');
      setPrice('');
      setQuantity('');
    }
  }, [editOrder, initialSymbol, balanceContext, isOpen]);

  if (!isOpen) return null;

  const currentPrice = balanceContext?.price || 0;
  const available = balanceContext?.available || 0;
  const uncoveredQty = balanceContext?.uncoveredQty || 0;
  const usdAvailable = balanceContext?.usdAvailable || 0;
  const orderValue = price && quantity ? (Number(price) * Number(quantity)).toFixed(2) : '';
  const enteredPrice = Number(price) || 0;

  const applyPriceOffset = (pct) => {
    if (!currentPrice) return;
    const factor = side === 'Buy' ? (1 - pct / 100) : (1 + pct / 100);
    setPrice(String(Number((currentPrice * factor).toPrecision(8))));
  };

  const setCurrentPrice = () => {
    if (currentPrice) setPrice(String(currentPrice));
  };

  const applyQtyPercentage = (pct) => {
    if (side === 'Buy') {
      // Buy: percentage of USD balance, converted to units at the entered price
      const usePrice = enteredPrice > 0 ? enteredPrice : currentPrice;
      if (usePrice <= 0 || usdAvailable <= 0) return;
      const usdToSpend = pct >= 100 ? usdAvailable : usdAvailable * pct / 100;
      const qty = Number((usdToSpend / usePrice).toPrecision(8));
      if (qty > 0) setQuantity(String(qty));
    } else {
      // Sell: percentage of held balance (uncovered if significant, otherwise available)
      const base = uncoveredQty > 0.0001 ? uncoveredQty : available;
      if (base <= 0) return;
      const qty = pct >= 100 ? base : Number((base * pct / 100).toPrecision(8));
      if (qty > 0) setQuantity(String(qty));
    }
  };

  const handleQtyChange = (val) => {
    // Allow empty for clearing, otherwise only non-negative
    if (val === '' || val === undefined) { setQuantity(''); return; }
    const num = Number(val);
    if (!isNaN(num) && num >= 0) setQuantity(val);
  };

  const handlePriceChange = (val) => {
    if (val === '' || val === undefined) { setPrice(''); return; }
    const num = Number(val);
    if (!isNaN(num) && num >= 0) setPrice(val);
  };

  const validate = () => {
    if (!symbol) return 'Select a symbol';
    if (!price || Number(price) <= 0) return 'Enter a valid price';
    if (!quantity || Number(quantity) <= 0) return 'Enter a valid quantity';
    const qty = Number(quantity);
    if (side === 'Sell' && available > 0 && qty > available * 1.001) {
      return `Cannot sell ${qty} — only ${available} available`;
    }
    return null;
  };

  const handleSubmit = async () => {
    const err = validate();
    if (err) { setError(err); return; }
    setError('');
    setSubmitting(true);
    try {
      if (editOrder) {
        await api.put(`/orders/${editOrder.id}`, { price: Number(price), quantity: Number(quantity) });
      } else {
        await api.post('/orders', { symbol: symbol.replace('/', ''), side, price: Number(price), quantity: Number(quantity) });
      }
      onClose(true);
    } catch (err) {
      const msg = err.response?.data?.error || err.response?.data || 'Failed to submit order';
      setError(typeof msg === 'string' ? msg : JSON.stringify(msg));
    } finally {
      setSubmitting(false);
    }
  };

  const inputStyle = {
    width: '100%', padding: '8px 12px', background: 'var(--bg-input)', border: '1px solid var(--border)',
    color: 'var(--text-primary)', borderRadius: 4, boxSizing: 'border-box', fontSize: 13,
  };
  const smallBtn = {
    padding: '2px 6px', fontSize: 10, fontWeight: 600, border: 'none', borderRadius: 3,
    cursor: 'pointer', background: 'var(--bg-input)', color: 'var(--text-secondary)',
    transition: 'all 0.15s',
  };

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'var(--overlay-bg)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000 }}>
      <div style={{ background: 'var(--dialog-bg)', borderRadius: 8, padding: 24, width: 440, border: '1px solid var(--border)' }}>
        <h3 style={{ color: 'var(--text-primary)', marginTop: 0, marginBottom: 16, fontSize: 15 }}>
          {editOrder ? 'Edit Order' : 'Create Order'}
        </h3>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {/* Symbol */}
          <div>
            <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Symbol</label>
            {editOrder || balanceContext ? (
              <div style={{ ...inputStyle, display: 'flex', alignItems: 'center', background: 'var(--bg-secondary)' }}>{symbol}</div>
            ) : (
              <select value={symbol} onChange={e => setSymbol(e.target.value)} style={inputStyle}>
                <option value="">Select...</option>
                {(symbols || []).map(s => <option key={s} value={s}>{s}</option>)}
              </select>
            )}
          </div>

          {/* Side */}
          {!editOrder && (
            <div>
              <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Side</label>
              <div style={{ display: 'flex', gap: 8, marginTop: 4 }}>
                <button
                  onClick={() => setSide('Buy')}
                  style={{ flex: 1, padding: '6px 0', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13,
                    background: side === 'Buy' ? 'var(--green)' : 'var(--bg-input)', color: side === 'Buy' ? '#fff' : 'var(--text-secondary)' }}
                >Buy</button>
                <button
                  onClick={() => setSide('Sell')}
                  style={{ flex: 1, padding: '6px 0', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13,
                    background: side === 'Sell' ? 'var(--red)' : 'var(--bg-input)', color: side === 'Sell' ? '#fff' : 'var(--text-secondary)' }}
                >Sell</button>
              </div>
            </div>
          )}

          {/* Price */}
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
              <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Price</label>
              {currentPrice > 0 && !editOrder && (
                <div style={{ display: 'flex', gap: 3, marginLeft: 'auto', flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                  <button style={smallBtn} onClick={setCurrentPrice} title="Use current price">Current</button>
                  {pOffsets.map(pct => (
                    <button key={pct} style={smallBtn} onClick={() => applyPriceOffset(pct)} title={`${side === 'Buy' ? '-' : '+'}${pct}% from current`}>
                      {side === 'Buy' ? '-' : '+'}{pct}%
                    </button>
                  ))}
                </div>
              )}
            </div>
            <input type="number" step="any" min="0" value={price} onChange={e => handlePriceChange(e.target.value)} style={inputStyle} placeholder={currentPrice ? `Current: ${currentPrice}` : ''} />
          </div>

          {/* Quantity */}
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
              <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Quantity</label>
              {!editOrder && ((side === 'Sell' && available > 0) || (side === 'Buy' && usdAvailable > 0)) && (
                <div style={{ display: 'flex', gap: 3, marginLeft: 'auto', flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                  {side === 'Sell' && uncoveredQty > 0.0001 && (
                    <button style={smallBtn} onClick={() => setQuantity(String(uncoveredQty))} title="Uncovered balance">Uncov</button>
                  )}
                  {qPcts.map(pct => (
                    <button key={pct} style={smallBtn} onClick={() => applyQtyPercentage(pct)}
                      title={side === 'Buy'
                        ? `${pct}% of $${usdAvailable.toFixed(2)} USD at ${enteredPrice > 0 ? enteredPrice : currentPrice}`
                        : `${pct}% of ${uncoveredQty > 0.0001 ? 'uncovered' : 'available'} balance`}>
                      {pct}%
                    </button>
                  ))}
                </div>
              )}
            </div>
            <input type="number" step="any" min="0" value={quantity} onChange={e => handleQtyChange(e.target.value)} style={inputStyle} />
          </div>

          {/* Order value */}
          {orderValue && (
            <div style={{ color: 'var(--text-secondary)', fontSize: 13 }}>
              Order Value: <strong style={{ color: 'var(--text-primary)' }}>${orderValue}</strong>
            </div>
          )}

          {/* Error */}
          {error && <div style={{ color: 'var(--red)', fontSize: 13, whiteSpace: 'pre-wrap' }}>{error}</div>}

          {/* Buttons */}
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 8 }}>
            <button onClick={() => onClose(false)} className="btn btn-secondary">Cancel</button>
            <button onClick={handleSubmit} disabled={submitting} className="btn btn-primary" style={{ opacity: submitting ? 0.5 : 1 }}>
              {submitting ? 'Submitting...' : editOrder ? 'Amend' : `${side} Order`}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
