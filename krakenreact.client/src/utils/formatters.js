export function formatNumber(value, decimals = 2) {
  if (value == null || value === '') return '';
  const num = Number(value);
  if (isNaN(num)) return '';
  return num.toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
}

export function formatPrice(value) {
  if (value == null) return '';
  const num = Number(value);
  if (isNaN(num)) return '';
  if (Math.abs(num) < 0.01) return num.toFixed(8);
  if (Math.abs(num) < 1) return num.toFixed(6);
  if (Math.abs(num) < 100) return num.toFixed(4);
  return num.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function formatDate(value) {
  if (!value) return '';
  const d = new Date(value);
  return d.toLocaleDateString() + ' ' + d.toLocaleTimeString();
}

export function colorForValue(value) {
  if (value == null) return undefined;
  const num = Number(value);
  if (num > 0) return 'var(--green)';
  if (num < 0) return 'var(--red)';
  return undefined;
}
