// A setInterval that does nothing while the browser tab is hidden and catches up once when it becomes visible again, so a
// forgotten background tab doesn't keep polling the server (and, through it, Kraken). Returns a function that stops it.
export function setVisibleInterval(fn, ms) {
  const tick = () => { if (!document.hidden) fn(); };
  const timer = setInterval(tick, ms);
  const onVisible = () => { if (!document.hidden) fn(); };
  document.addEventListener('visibilitychange', onVisible);
  return () => {
    clearInterval(timer);
    document.removeEventListener('visibilitychange', onVisible);
  };
}
