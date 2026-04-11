import { formatPrice } from '../utils/formatters';

export default function TickerCard({ data, selected, onClick, onRemove, tempPinned }) {
  const change = data.closePriceMovement ?? 0;
  const isPositive = change >= 0;

  return (
    <div
      className={`ticker-card${selected ? ' selected' : ''}${tempPinned ? ' temp-pinned' : ''}`}
      onClick={() => onClick(data.symbol)}
    >
      <div className="ticker-symbol">
        {data.displaySymbol || data.symbol}
        {onRemove && (
          <button
            className="ticker-remove"
            onClick={(e) => { e.stopPropagation(); onRemove(data.symbol); }}
            title="Remove from ticker bar"
          >x</button>
        )}
      </div>
      <div className="ticker-price">{formatPrice(data.closePrice)}</div>
      <div className={`ticker-change ${isPositive ? 'positive' : 'negative'}`}>
        {isPositive ? '+' : ''}{change.toFixed(2)}%
      </div>
      <div className="ticker-bidask">
        <span className="bid">{formatPrice(data.bestBid)}</span>
        <span className="ask">{formatPrice(data.bestAsk)}</span>
      </div>
    </div>
  );
}
