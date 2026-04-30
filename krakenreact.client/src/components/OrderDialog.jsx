import { useState, useEffect, useRef } from 'react';
import api from '../api/apiClient';

const DEFAULT_PRICE_OFFSETS = [2, 5, 10, 15];
const DEFAULT_QTY_PERCENTAGES = [5, 10, 20, 25, 50, 75, 100];

// Cached symbol constraints so we only fetch once per session
let _symbolsCache = null;
async function fetchSymbolConstraints() {
  if (_symbolsCache) return _symbolsCache;
  const r = await api.get('/symbols');
  _symbolsCache = r.data || [];
  return _symbolsCache;
}

function findSymbolInfo(symbolsList, symbol) {
  if (!symbolsList || !symbol) return null;
  const noSlash = symbol.replace('/', '');
  return symbolsList.find(s =>
    s.websocketName === symbol ||
    s.websocketName?.replace('/', '') === symbol ||
    s.websocketName === noSlash ||
    s.websocketName?.replace('/', '') === noSlash
  ) || null;
}

export default function OrderDialog({ isOpen, onClose, editOrder, symbol: initialSymbol, symbols, balanceContext, priceOffsets, qtyPercentages }) {
  const [symbol, setSymbol] = useState('');
  const [side, setSide] = useState('Buy');
  const [price, setPrice] = useState('');
  const [quantity, setQuantity] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [riskUsd, setRiskUsd] = useState('');
  const [atrData, setAtrData] = useState(null);
  const [symbolsList, setSymbolsList] = useState(_symbolsCache || []);

  const pOffsets = priceOffsets?.length ? priceOffsets : DEFAULT_PRICE_OFFSETS;
  const qPcts = qtyPercentages?.length ? qtyPercentages : DEFAULT_QTY_PERCENTAGES;

  // Fetch ATR data and symbol constraints when dialog opens
  useEffect(() => {
    if (!isOpen) return;
    api.get('/balances/atr').then(r => {
      const map = {};
      (r.data || []).forEach(a => { map[a.asset] = a; });
      setAtrData(map);
    }).catch(() => {});
    fetchSymbolConstraints().then(setSymbolsList).catch(() => {});
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) return;
    setError('');
    setRiskUsd('');
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

  const symInfo = findSymbolInfo(symbolsList, symbol);
  const currentPrice = balanceContext?.price || 0;
  const available = balanceContext?.available || 0;
  const uncoveredQty = balanceContext?.uncoveredQty || 0;
  const usdAvailable = balanceContext?.usdAvailable || 0;
  const orderValue = price && quantity ? (Number(price) * Number(quantity)).toFixed(2) : '';

  // Derive ATR info for the current symbol's base asset
  const baseAsset = (() => {
    const s = symbol || '';
    const part = s.split('/')[0].split('USD')[0];
    return part.replace(/^X/, '') || '';
  })();
  const atrInfo = atrData ? (atrData[baseAsset] || null) : null;
  const atrPct = atrInfo?.atrPct || 0;

  const applyRiskSizing = () => {
    const risk = Number(riskUsd);
    if (!risk || risk <= 0 || atrPct <= 0) return;
    const usePrice = Number(price) || currentPrice;
    if (!usePrice) return;
    // qty = risk / (atrPct/100 * price) — 1 ATR move as the risk unit
    const qty = Number((risk / (atrPct / 100 * usePrice)).toPrecision(8));
    if (qty > 0) setQuantity(String(qty));
  };
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

  const roundToDecimals = (val, decimals) => {
    if (decimals == null || decimals < 0) return val;
    return Number(Number(val).toFixed(decimals));
  };

  const handlePriceBlur = () => {
    if (!price || !symInfo?.priceDecimals) return;
    const rounded = roundToDecimals(price, symInfo.priceDecimals);
    if (!isNaN(rounded) && rounded > 0) setPrice(String(rounded));
  };

  const handleQtyBlur = () => {
    if (!quantity || !symInfo?.lotDecimals) return;
    const rounded = roundToDecimals(quantity, symInfo.lotDecimals);
    if (!isNaN(rounded) && rounded > 0) setQuantity(String(rounded));
  };

  const validate = () => {
    if (!symbol) return 'Select a symbol';
    if (!price || Number(price) <= 0) return 'Enter a valid price';
    if (!quantity || Number(quantity) <= 0) return 'Enter a valid quantity';
    const qty = Number(quantity);
    const prc = Number(price);
    if (side === 'Sell' && available > 0 && qty > available * 1.001) {
      return `Cannot sell ${qty} — only ${available} available`;
    }
    if (symInfo) {
      if (symInfo.orderMin > 0 && qty < symInfo.orderMin) {
        return `Quantity ${qty} is below the minimum of ${symInfo.orderMin} for ${symbol}`;
      }
      if (symInfo.minValue > 0 && qty * prc < symInfo.minValue) {
        return `Order value $${(qty * prc).toFixed(2)} is below the minimum of $${symInfo.minValue} for ${symbol}`;
      }
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
            <input type="number" step="any" min="0" value={price} onChange={e => handlePriceChange(e.target.value)} onBlur={handlePriceBlur} style={inputStyle} placeholder={currentPrice ? `Current: ${currentPrice}` : ''} />
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
            <input type="number" step="any" min="0" value={quantity} onChange={e => handleQtyChange(e.target.value)} onBlur={handleQtyBlur} style={inputStyle} />
          </div>

          {/* ATR-based position sizing */}
          {!editOrder && side === 'Buy' && atrPct > 0 && (
            <div style={{ padding: '10px 12px', background: 'var(--bg-secondary, var(--bg-input))', borderRadius: 4, border: '1px solid var(--border)' }}>
              <div style={{ fontSize: 11, color: 'var(--text-secondary)', marginBottom: 6, fontWeight: 600 }}>
                ATR Position Sizing — 14-day ATR: {atrPct.toFixed(2)}% of price
              </div>
              <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <input
                  type="number" step="any" min="0"
                  placeholder="Risk $ amount"
                  value={riskUsd}
                  onChange={e => setRiskUsd(e.target.value)}
                  style={{ ...inputStyle, flex: 1 }}
                />
                <button onClick={applyRiskSizing} style={{ padding: '7px 12px', background: 'var(--bg-input)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', color: 'var(--text-secondary)', fontSize: 12, whiteSpace: 'nowrap' }}>
                  Apply
                </button>
              </div>
              <div style={{ fontSize: 10, color: 'var(--text-secondary)', marginTop: 4 }}>
                Qty = Risk $ ÷ (ATR% × Price). If you risk $100 with ATR 2%, qty ≈ 100 / (0.02 × price).
              </div>
            </div>
          )}

          {/* Order value */}
          {orderValue && (
            <div style={{ color: 'var(--text-secondary)', fontSize: 13 }}>
              Order Value: <strong style={{ color: 'var(--text-primary)' }}>${orderValue}</strong>
            </div>
          )}

          {/* Symbol constraints */}
          {symInfo && (
            <div style={{ padding: '6px 10px', background: 'var(--bg-secondary, var(--bg-input))', borderRadius: 4, border: '1px solid var(--border)', fontSize: 11, color: 'var(--text-muted)', display: 'flex', flexWrap: 'wrap', gap: '0 16px' }}>
              {symInfo.orderMin > 0 && <span>Min qty: <strong>{symInfo.orderMin}</strong></span>}
              {symInfo.minValue > 0 && <span>Min value: <strong>${symInfo.minValue}</strong></span>}
              {symInfo.priceDecimals != null && <span>Price decimals: <strong>{symInfo.priceDecimals}</strong></span>}
              {symInfo.lotDecimals != null && <span>Qty decimals: <strong>{symInfo.lotDecimals}</strong></span>}
              {symInfo.tickSize > 0 && <span>Tick: <strong>{symInfo.tickSize}</strong></span>}
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
