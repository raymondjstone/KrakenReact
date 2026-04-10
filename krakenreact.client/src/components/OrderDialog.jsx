import { useState, useEffect } from 'react';
import api from '../api/apiClient';

export default function OrderDialog({ isOpen, onClose, editOrder, symbol: initialSymbol, symbols }) {
  const [symbol, setSymbol] = useState('');
  const [side, setSide] = useState('Buy');
  const [price, setPrice] = useState('');
  const [quantity, setQuantity] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (editOrder) {
      setSymbol(editOrder.symbol);
      setSide(editOrder.side);
      setPrice(String(editOrder.price));
      setQuantity(String(editOrder.quantity));
    } else if (initialSymbol) {
      setSymbol(initialSymbol);
    }
    setError('');
  }, [editOrder, initialSymbol, isOpen]);

  if (!isOpen) return null;

  const orderValue = price && quantity ? (Number(price) * Number(quantity)).toFixed(2) : '';

  const handleSubmit = async () => {
    setError('');
    if (!symbol) { setError('Select a symbol'); return; }
    if (!price || Number(price) <= 0) { setError('Enter a valid price'); return; }
    if (!quantity || Number(quantity) <= 0) { setError('Enter a valid quantity'); return; }
    setSubmitting(true);
    try {
      if (editOrder) {
        await api.put(`/orders/${editOrder.id}`, { price: Number(price), quantity: Number(quantity) });
      } else {
        await api.post('/orders', { symbol: symbol.replace('/', ''), side, price: Number(price), quantity: Number(quantity) });
      }
      onClose(true);
    } catch (err) {
      setError(err.response?.data?.error || 'Failed to submit order');
    } finally {
      setSubmitting(false);
    }
  };

  const inputStyle = {
    width: '100%', padding: '8px 12px', background: 'var(--bg-input)', border: '1px solid var(--border)',
    color: 'var(--text-primary)', borderRadius: 4, boxSizing: 'border-box', fontSize: 13,
  };

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'var(--overlay-bg)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000 }}>
      <div style={{ background: 'var(--dialog-bg)', borderRadius: 8, padding: 24, width: 400, border: '1px solid var(--border)' }}>
        <h3 style={{ color: 'var(--text-primary)', marginTop: 0, marginBottom: 16, fontSize: 15 }}>{editOrder ? 'Edit Order' : 'Create Order'}</h3>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div>
            <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Symbol</label>
            {editOrder ? (
              <div style={{ ...inputStyle, display: 'flex', alignItems: 'center', background: 'var(--bg-secondary)' }}>{symbol}</div>
            ) : (
              <select value={symbol} onChange={e => setSymbol(e.target.value)} style={inputStyle}>
                <option value="">Select...</option>
                {(symbols || []).map(s => <option key={s} value={s}>{s}</option>)}
              </select>
            )}
          </div>
          <div style={{ display: 'flex', gap: 12 }}>
            <label style={{ color: side === 'Buy' ? 'var(--green)' : 'var(--text-secondary)', cursor: 'pointer', fontWeight: 500 }}>
              <input type="radio" value="Buy" checked={side === 'Buy'} onChange={() => setSide('Buy')} /> Buy
            </label>
            <label style={{ color: side === 'Sell' ? 'var(--red)' : 'var(--text-secondary)', cursor: 'pointer', fontWeight: 500 }}>
              <input type="radio" value="Sell" checked={side === 'Sell'} onChange={() => setSide('Sell')} /> Sell
            </label>
          </div>
          <div>
            <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Price</label>
            <input type="number" step="any" value={price} onChange={e => setPrice(e.target.value)} style={inputStyle} />
          </div>
          <div>
            <label style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Quantity</label>
            <input type="number" step="any" value={quantity} onChange={e => setQuantity(e.target.value)} style={inputStyle} />
          </div>
          {orderValue && <div style={{ color: 'var(--text-secondary)' }}>Order Value: <strong style={{ color: 'var(--text-primary)' }}>${orderValue}</strong></div>}
          {error && <div style={{ color: 'var(--red)', fontSize: 13 }}>{error}</div>}
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 8 }}>
            <button onClick={() => onClose(false)} className="btn btn-secondary">Cancel</button>
            <button onClick={handleSubmit} disabled={submitting} className="btn btn-primary" style={{ opacity: submitting ? 0.5 : 1 }}>
              {submitting ? 'Submitting...' : editOrder ? 'Amend' : 'Place Order'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
