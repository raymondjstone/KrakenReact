import axios from 'axios';

// Sent on every request. The server refuses state-changing API calls without it, which is what stops another website
// from driving the API through this browser (a cross-site page can't add a custom header without a CORS pre-flight,
// and the server only approves that for this app's own origins).
export const CLIENT_HEADERS = { 'X-Requested-With': 'KrakenReact' };

const api = axios.create({
  baseURL: '/api',
  headers: CLIENT_HEADERS,
  // Without a timeout a hung request (server busy, network dropped) leaves the UI waiting forever
  timeout: 30000,
});

// Placing an order can legitimately take longer than an ordinary request: the server waits up to its Kraken timeout (60 s by
// default) and then looks the order up again (a further 9 s). Giving up sooner leaves the user not knowing whether the order
// exists. Pass this as the request timeout on calls that place or change orders.
export const ORDER_TIMEOUT_MS = 100000;

/** True when the request got no answer at all (timed out, or the connection dropped): the server may still have acted on it. */
export function hasNoResponse(err) {
  return !!err && !err.response && (err.code === 'ECONNABORTED' || err.code === 'ERR_NETWORK' || err.code === 'ETIMEDOUT');
}

export const NO_RESPONSE_ORDER_MESSAGE =
  'No response from the server - the order may or may not have been placed. Check Open Orders before trying again.';

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
