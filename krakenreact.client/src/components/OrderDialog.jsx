import { useState, useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import api from '../api/apiClient';

const DEFAULT_PRICE_OFFSETS = [2, 5, 10, 15];
const DEFAULT_QTY_PERCENTAGES = [5, 10, 20, 25, 50, 75, 100];

async function fetchTemplates() {
  const r = await api.get('/ordertemplates');
  return r.data || [];
}

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
  return symbolsList.find((s) =>
    s.websocketName === symbol ||
    s.websocketName?.replace('/', '') === symbol ||
    s.websocketName === noSlash ||
    s.websocketName?.replace('/', '') === noSlash
  ) || null;
}

export default function OrderDialog({ isOpen, onClose, editOrder, symbol: initialSymbol, symbols, balanceContext, priceOffsets, qtyPercentages }) {
  const dialogRef = useRef(null);
  const dragData = useRef({ dragging: false, offsetX: 0, offsetY: 0 });
  const [symbol, setSymbol] = useState('');
  const [side, setSide] = useState('Buy');
  const [price, setPrice] = useState('');
  const [quantity, setQuantity] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [riskUsd, setRiskUsd] = useState('');
  const [atrData, setAtrData] = useState(null);
  const [symbolsList, setSymbolsList] = useState(_symbolsCache || []);
  const [templates, setTemplates] = useState([]);
  const [showSaveTemplate, setShowSaveTemplate] = useState(false);
  const [templateName, setTemplateName] = useState('');
  const [bracketEnabled, setBracketEnabled] = useState(false);
  const [bracketStopPct, setBracketStopPct] = useState('2');
  const [bracketTpPct, setBracketTpPct] = useState('5');

  const pOffsets = priceOffsets?.length ? priceOffsets : DEFAULT_PRICE_OFFSETS;
  const qPcts = qtyPercentages?.length ? qtyPercentages : DEFAULT_QTY_PERCENTAGES;

  useEffect(() => {
    if (!isOpen) return;
    api.get('/balances/atr').then((r) => {
      const map = {};
      (r.data || []).forEach((a) => { map[a.asset] = a; });
      setAtrData(map);
    }).catch(() => { /* ignore */ });
    fetchSymbolConstraints().then(setSymbolsList).catch(() => { /* ignore */ });
    fetchTemplates().then(setTemplates).catch(() => { /* ignore */ });
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

  useEffect(() => {
    const onMouseMove = (e) => {
      if (!dragData.current.dragging || !dialogRef.current) return;
      dialogRef.current.style.left = e.clientX - dragData.current.offsetX + 'px';
      dialogRef.current.style.top = e.clientY - dragData.current.offsetY + 'px';
    };
    const onMouseUp = () => { dragData.current.dragging = false; };
    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
    return () => {
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
    };
  }, []);

  if (!isOpen) return null;

  const symInfo = findSymbolInfo(symbolsList, symbol);
  const currentPrice = balanceContext?.price || 0;
  const available = balanceContext?.available || 0;
  const uncoveredQty = balanceContext?.uncoveredQty || 0;
  const usdAvailable = balanceContext?.usdAvailable || 0;
  const orderValue = price && quantity ? (Number(price) * Number(quantity)).toFixed(2) : '';

  const baseAsset = (() => {
    const s = symbol || '';
    const part = s.split('/')[0].split('USD')[0];
    return part.replace(/^X/, '') || '';
  })();
  const atrInfo = atrData ? (atrData[baseAsset] || null) : null;
  const atrPct = atrInfo?.atrPct || 0;

  const roundToDecimals = (val, decimals) => {
    if (decimals == null || decimals < 0) return val;
    return Number(Number(val).toFixed(decimals));
  };

  const applyRoundedPrice = (raw) => {
    const decimals = symInfo?.priceDecimals;
    const rounded = decimals != null ? roundToDecimals(raw, decimals) : Number(raw.toPrecision(8));
    setPrice(String(rounded));
  };

  const applyPriceOffset = (pct) => {
    if (!currentPrice) return;
    const factor = side === 'Buy' ? (1 - pct / 100) : (1 + pct / 100);
    applyRoundedPrice(currentPrice * factor);
  };

  const setCurrentPrice = () => { if (currentPrice) applyRoundedPrice(currentPrice); };
  const enteredPrice = Number(price) || 0;

  const applyQtyPercentage = (pct) => {
    if (side === 'Buy') {
      const usePrice = enteredPrice > 0 ? enteredPrice : currentPrice;
      if (usePrice <= 0 || usdAvailable <= 0) return;
      const usdToSpend = pct >= 100 ? usdAvailable : usdAvailable * pct / 100;
      const qty = Number((usdToSpend / usePrice).toPrecision(8));
      if (qty > 0) setQuantity(String(qty));
    } else {
      const base = uncoveredQty > 0.0001 ? uncoveredQty : available;
      if (base <= 0) return;
      const qty = pct >= 100 ? base : Number((base * pct / 100).toPrecision(8));
      if (qty > 0) setQuantity(String(qty));
    }
  };

  const applyRiskSizing = () => {
    const risk = Number(riskUsd);
    if (!risk || risk <= 0 || atrPct <= 0) return;
    const usePrice = Number(price) || currentPrice;
    if (!usePrice) return;
    const qty = Number((risk / (atrPct / 100 * usePrice)).toPrecision(8));
    if (qty > 0) setQuantity(String(qty));
  };

  const handleQtyChange = (val) => {
    if (val === '' || val === undefined) { setQuantity(''); return; }
    const num = Number(val);
    if (!isNaN(num) && num >= 0) setQuantity(val);
  };

  const handlePriceChange = (val) => {
    if (val === '' || val === undefined) { setPrice(''); return; }
    const num = Number(val);
    if (!isNaN(num) && num >= 0) setPrice(val);
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
    if (side === 'Sell' && available > 0 && qty > available * 1.001)
      return 'Cannot sell ' + qty + ' - only ' + available + ' available';
    if (symInfo) {
      if (symInfo.orderMin > 0 && qty < symInfo.orderMin)
        return 'Quantity ' + qty + ' is below the minimum of ' + symInfo.orderMin + ' for ' + symbol;
      if (symInfo.minValue > 0 && qty * prc < symInfo.minValue)
        return 'Order value $' + (qty * prc).toFixed(2) + ' is below the minimum of $' + symInfo.minValue + ' for ' + symbol;
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
        await api.put('/orders/' + editOrder.id, { price: Number(price), quantity: Number(quantity) });
      } else {
        const payload = { symbol: symbol.replace('/', ''), side, price: Number(price), quantity: Number(quantity) };
        if (bracketEnabled && side === 'Buy') {
          if (Number(bracketStopPct) > 0) payload.bracketStopPct = Number(bracketStopPct);
          if (Number(bracketTpPct) > 0) payload.bracketTakeProfitPct = Number(bracketTpPct);
        }
        await api.post('/orders', payload);
      }
      onClose(true);
    } catch (submitErr) {
      if (submitErr.response?.status === 504 || submitErr.response?.status === 502) {
        setError('Server timed out - the Kraken API did not respond in time. Please try again.');
      } else {
        const data = submitErr.response?.data;
        const msg = (typeof data === 'string' && !data.trim().startsWith('<'))
          ? data
          : (data?.error ?? 'Failed to submit order');
        setError(typeof msg === 'string' ? msg : JSON.stringify(msg));
      }
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
    cursor: 'pointer', background: 'var(--bg-input)', color: 'var(--text-secondary)', transition: 'all 0.15s',
  };

  return createPortal(
    <div style={{ position: 'fixed', inset: 0, background: 'var(--overlay-bg)', zIndex: 9999 }}>
      <div
        ref={dialogRef}
        style={{
          position: 'absolute', left: '50%', top: '50%', transform: 'translate(-50%, -50%)',
          background: 'var(--dialog-bg)', borderRadius: 8, width: 440, maxWidth: '95vw',
          maxHeight: '90vh', border: '1px solid var(--border)', boxShadow: '0 4px 32px rgba(0,0,0,0.18)',
          display: 'flex', flexDirection: 'column', overflow: 'hidden',
        }}
      >
        <div
          style={{
            cursor: 'move', userSelect: 'none', padding: '12px 16px', flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'space-between',
            background: 'var(--dialog-bg)', borderBottom: '1px solid var(--border)',
            borderTopLeftRadius: 8, borderTopRightRadius: 8,
          }}
          onMouseDown={(e) => {
            const dialog = dialogRef.current;
            if (!dialog) return;
            dragData.current.dragging = true;
            const rect = dialog.getBoundingClientRect();
            dragData.current.offsetX = e.clientX - rect.left;
            dragData.current.offsetY = e.clientY - rect.top;
            dialog.style.transform = '';
          }}
        >
          <h3 style={{ color: 'var(--text-primary)', margin: 0, fontSize: 15 }}>
            {editOrder ? 'Edit Order' : 'Create Order'}
          </h3>
          <button onClick={() => onClose(false)} style={{ background: 'none', border: 'none', color: 'var(--text-muted)', fontSize: 20, cursor: 'pointer', lineHeight: 1 }}>x</button>
        </div>

        <div style={{ overflowY: 'auto', flex: 1, padding: '16px 24px' }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>

            <div>
              <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Symbol</label>
              {editOrder || balanceContext ? (
                <div style={{ ...inputStyle, display: 'flex', alignItems: 'center', background: 'var(--bg-secondary)' }}>{symbol}</div>
              ) : (
                <select value={symbol} onChange={(e) => setSymbol(e.target.value)} style={inputStyle}>
                  <option value=''>Select...</option>
                  {(symbols || []).map((s) => <option key={s} value={s}>{s}</option>)}
                </select>
              )}
            </div>

            {!editOrder && templates.length > 0 && (
              <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
                <span style={{ fontSize: 11, color: 'var(--text-muted)', whiteSpace: 'nowrap' }}>Template:</span>
                <select defaultValue='' onChange={(e) => {
                  const t = templates.find((tmpl) => String(tmpl.id) === e.target.value);
                  if (!t) return;
                  if (t.symbol) setSymbol(t.symbol);
                  if (t.side) setSide(t.side);
                  if (t.priceOffsetPct != null && currentPrice > 0) {
                    const factor = (t.side || side) === 'Buy' ? (1 - t.priceOffsetPct / 100) : (1 + t.priceOffsetPct / 100);
                    setPrice(String(Number((currentPrice * factor).toPrecision(8))));
                  }
                  if (t.quantity != null) setQuantity(String(t.quantity));
                  e.target.value = '';
                }} style={{ ...inputStyle, flex: 1, padding: '4px 8px', fontSize: 12 }}>
                  <option value=''>Load template...</option>
                  {templates.map((t) => <option key={t.id} value={t.id}>{t.name} ({t.symbol || 'any'} {t.side || ''})</option>)}
                </select>
              </div>
            )}

            {!editOrder && (
              <div>
                <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Side</label>
                <div style={{ display: 'flex', gap: 8, marginTop: 4 }}>
                  <button onClick={() => setSide('Buy')} style={{ flex: 1, padding: '6px 0', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13, background: side === 'Buy' ? 'var(--green)' : 'var(--bg-input)', color: side === 'Buy' ? '#fff' : 'var(--text-secondary)' }}>Buy</button>
                  <button onClick={() => setSide('Sell')} style={{ flex: 1, padding: '6px 0', border: 'none', borderRadius: 4, cursor: 'pointer', fontWeight: 600, fontSize: 13, background: side === 'Sell' ? 'var(--red)' : 'var(--bg-input)', color: side === 'Sell' ? '#fff' : 'var(--text-secondary)' }}>Sell</button>
                </div>
              </div>
            )}

            <div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
                <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Price</label>
                {currentPrice > 0 && !editOrder && (
                  <div style={{ display: 'flex', gap: 3, marginLeft: 'auto', flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                    <button style={smallBtn} onClick={setCurrentPrice}>Current</button>
                    {pOffsets.map((pct) => (
                      <button key={pct} style={smallBtn} onClick={() => applyPriceOffset(pct)}>
                        {side === 'Buy' ? '-' : '+'}{pct}%
                      </button>
                    ))}
                  </div>
                )}
              </div>
              <input type='number' step='any' min='0' value={price} onChange={(e) => handlePriceChange(e.target.value)} onBlur={handlePriceBlur} style={inputStyle} placeholder={currentPrice ? 'Current: ' + currentPrice : ''} />
            </div>

            <div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
                <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Quantity</label>
                {!editOrder && ((side === 'Sell' && available > 0) || (side === 'Buy' && usdAvailable > 0)) && (
                  <div style={{ display: 'flex', gap: 3, marginLeft: 'auto', flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                    {side === 'Sell' && uncoveredQty > 0.0001 && (
                      <button style={smallBtn} onClick={() => setQuantity(String(uncoveredQty))}>Uncov</button>
                    )}
                    {qPcts.map((pct) => (
                      <button key={pct} style={smallBtn} onClick={() => applyQtyPercentage(pct)}>{pct}%</button>
                    ))}
                  </div>
                )}
              </div>
              <input type='number' step='any' min='0' value={quantity} onChange={(e) => handleQtyChange(e.target.value)} onBlur={handleQtyBlur} style={inputStyle} />
            </div>

            {!editOrder && side === 'Buy' && atrPct > 0 && (
              <div style={{ padding: '10px 12px', background: 'var(--bg-secondary, var(--bg-input))', borderRadius: 4, border: '1px solid var(--border)' }}>
                <div style={{ fontSize: 11, color: 'var(--text-secondary)', marginBottom: 6, fontWeight: 600 }}>ATR Position Sizing - 14-day ATR: {atrPct.toFixed(2)}% of price</div>
                <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                  <input type='number' step='any' min='0' placeholder='Risk $ amount' value={riskUsd} onChange={(e) => setRiskUsd(e.target.value)} style={{ ...inputStyle, flex: 1 }} />
                  <button onClick={applyRiskSizing} style={{ padding: '7px 12px', background: 'var(--bg-input)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', color: 'var(--text-secondary)', fontSize: 12, whiteSpace: 'nowrap' }}>Apply</button>
                </div>
                <div style={{ fontSize: 10, color: 'var(--text-secondary)', marginTop: 4 }}>Qty = Risk $ / (ATR% x Price).</div>
              </div>
            )}

            {orderValue && (
              <div style={{ color: 'var(--text-secondary)', fontSize: 13 }}>
                Order Value: <strong style={{ color: 'var(--text-primary)' }}></strong>
              </div>
            )}

            {symInfo && (
              <div style={{ padding: '6px 10px', background: 'var(--bg-secondary, var(--bg-input))', borderRadius: 4, border: '1px solid var(--border)', fontSize: 11, color: 'var(--text-muted)', display: 'flex', flexWrap: 'wrap', gap: '0 16px' }}>
                {symInfo.orderMin > 0 && <span>Min qty: <strong>{symInfo.orderMin}</strong></span>}
                {symInfo.minValue > 0 && <span>Min value: <strong></strong></span>}
                {symInfo.priceDecimals != null && <span>Price decimals: <strong>{symInfo.priceDecimals}</strong></span>}
                {symInfo.lotDecimals != null && <span>Qty decimals: <strong>{symInfo.lotDecimals}</strong></span>}
                {symInfo.tickSize > 0 && <span>Tick: <strong>{symInfo.tickSize}</strong></span>}
              </div>
            )}

            {error && <div style={{ color: 'var(--red)', fontSize: 13, whiteSpace: 'pre-wrap' }}>{error}</div>}

            {!editOrder && side === 'Buy' && (
              <div style={{ padding: '10px 12px', background: 'var(--bg-secondary, var(--bg-input))', borderRadius: 4, border: '1px solid var(--border)' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12, color: 'var(--text-secondary)', cursor: 'pointer', marginBottom: bracketEnabled ? 10 : 0 }}>
                  <input type='checkbox' checked={bracketEnabled} onChange={(e) => setBracketEnabled(e.target.checked)} />
                  Attach bracket (OCO stop-loss + take-profit)
                </label>
                {bracketEnabled && (
                  <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap' }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                      <span style={{ fontSize: 11, color: 'var(--red)', whiteSpace: 'nowrap' }}>Stop-loss %:</span>
                      <input type='number' step='0.1' min='0.1' max='50' value={bracketStopPct} onChange={(e) => setBracketStopPct(e.target.value)} style={{ ...inputStyle, width: 70 }} />
                      {price && Number(bracketStopPct) > 0 && <span style={{ fontSize: 10, color: 'var(--text-muted)' }}>@ </span>}
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                      <span style={{ fontSize: 11, color: 'var(--green)', whiteSpace: 'nowrap' }}>Take-profit %:</span>
                      <input type='number' step='0.1' min='0.1' max='200' value={bracketTpPct} onChange={(e) => setBracketTpPct(e.target.value)} style={{ ...inputStyle, width: 70 }} />
                      {price && Number(bracketTpPct) > 0 && <span style={{ fontSize: 10, color: 'var(--text-muted)' }}>@ </span>}
                    </div>
                    <span style={{ fontSize: 10, color: 'var(--text-muted)' }}>SL &amp; TP placed after entry fills. First fill cancels the other.</span>
                  </div>
                )}
              </div>
            )}

            {!editOrder && (
              <div>
                {showSaveTemplate ? (
                  <div style={{ display: 'flex', gap: 8, alignItems: 'center', padding: '8px 10px', background: 'var(--bg-secondary, var(--bg-input))', borderRadius: 4, border: '1px solid var(--border)' }}>
                    <input value={templateName} onChange={(e) => setTemplateName(e.target.value)} placeholder='Template name' style={{ ...inputStyle, flex: 1, padding: '4px 8px', fontSize: 12 }} />
                    <button
                      onClick={async () => {
                        if (!templateName.trim()) return;
                        try {
                          await api.post('/ordertemplates', {
                            name: templateName.trim(), symbol: symbol || null, side: side || null,
                            priceOffsetPct: currentPrice > 0 && price ? (Math.abs(Number(price) / currentPrice - 1) * 100) : null,
                            quantity: quantity ? Number(quantity) : null, note: '',
                          });
                          const updated = await fetchTemplates();
                          setTemplates(updated);
                          setShowSaveTemplate(false);
                          setTemplateName('');
                        } catch (saveErr) { console.error('Failed to save template', saveErr); }
                      }}
                      style={{ padding: '4px 12px', background: 'var(--green)', color: 'white', border: 'none', borderRadius: 4, cursor: 'pointer', fontSize: 12, whiteSpace: 'nowrap' }}
                    >Save</button>
                    <button onClick={() => setShowSaveTemplate(false)} style={{ padding: '4px 8px', background: 'transparent', color: 'var(--text-muted)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 12 }}>x</button>
                  </div>
                ) : (
                  <button onClick={() => setShowSaveTemplate(true)} style={{ padding: '3px 10px', background: 'transparent', color: 'var(--text-muted)', border: '1px solid var(--border)', borderRadius: 4, cursor: 'pointer', fontSize: 11 }}>Save as Template</button>
                )}
              </div>
            )}

          </div>
        </div>

        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', padding: '12px 24px', borderTop: '1px solid var(--border)', flexShrink: 0, background: 'var(--dialog-bg)' }}>
          <button onClick={() => onClose(false)} className='btn btn-secondary'>Cancel</button>
          <button onClick={handleSubmit} disabled={submitting} className='btn btn-primary' style={{ opacity: submitting ? 0.5 : 1 }}>
            {submitting ? 'Submitting...' : editOrder ? 'Amend' : side + ' Order'}
          </button>
        </div>

      </div>
    </div>,
    document.body
  );
}
