// Browser-side preferences that don't need the server (currently the large-movement highlight threshold).
// Kept out of SettingsPage so that file exports only the component, which React fast refresh needs.

const SETTINGS_KEY = 'kraken_app_settings';

const defaultSettings = {
  largeMovementThreshold: 5,
};

export function loadSettings() {
  try {
    const stored = localStorage.getItem(SETTINGS_KEY);
    if (stored) return { ...defaultSettings, ...JSON.parse(stored) };
  } catch { /* storage unavailable or corrupt — use the defaults */ }
  return { ...defaultSettings };
}

export function saveSettings(settings) {
  // Storage can be full, blocked (private windows) or unavailable; losing a preference must not break the page
  try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings)); } catch { /* ignore */ }
}
