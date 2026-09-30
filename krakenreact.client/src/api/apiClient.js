import axios from 'axios';

const api = axios.create({
  baseURL: '/api',
  // Without a timeout a hung request (server busy, network dropped) leaves the UI waiting forever
  timeout: 30000,
});

/**
 * Shows a short error banner (rendered by App). Use it where a failed action would otherwise be swallowed — a
 * delete or save that silently did nothing looks exactly like one that worked.
 */
export function reportError(message) {
  window.dispatchEvent(new CustomEvent('app-error', { detail: message }));
}

/** Best human-readable reason from an axios error: the server's message if it sent one, else a fallback. */
export function errorMessage(err, fallback = 'Request failed') {
  const data = err?.response?.data;
  return (typeof data === 'string' && data) || data?.message || data?.error || (err?.code === 'ECONNABORTED' ? 'Request timed out' : fallback);
}

export default api;
