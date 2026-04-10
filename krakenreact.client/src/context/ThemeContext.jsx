import { createContext, useContext, useState, useEffect, useMemo } from 'react';
import { themeAlpine, colorSchemeDark, colorSchemeLight } from 'ag-grid-community';

const ThemeContext = createContext();

const lightGridTheme = themeAlpine.withPart(colorSchemeLight).withParams({
  backgroundColor: '#ffffff',
  headerBackgroundColor: '#f5f6f8',
  oddRowBackgroundColor: '#fafbfc',
  rowHoverColor: '#f0f1f3',
  borderColor: '#e0e3e8',
  headerForegroundColor: '#5e6673',
  foregroundColor: '#1e2329',
  fontSize: 12,
  selectedRowBackgroundColor: 'rgba(184, 134, 11, 0.08)',
});

const darkGridTheme = themeAlpine.withPart(colorSchemeDark).withParams({
  backgroundColor: '#1e2329',
  headerBackgroundColor: '#161a1e',
  oddRowBackgroundColor: '#1a1e24',
  rowHoverColor: '#2b3139',
  borderColor: '#2e3440',
  headerForegroundColor: '#848e9c',
  foregroundColor: '#eaecef',
  fontSize: 12,
  selectedRowBackgroundColor: 'rgba(240, 185, 11, 0.08)',
});

// eslint-disable-next-line react-refresh/only-export-components
export function useTheme() {
  return useContext(ThemeContext);
}

export function ThemeProvider({ children }) {
  const [theme, setTheme] = useState(() => localStorage.getItem('kraken_theme') || 'light');

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('kraken_theme', theme);
  }, [theme]);

  const toggleTheme = () => setTheme(prev => prev === 'dark' ? 'light' : 'dark');
  const isDark = theme === 'dark';
  const gridTheme = useMemo(() => isDark ? darkGridTheme : lightGridTheme, [isDark]);

  return (
    <ThemeContext.Provider value={{ theme, toggleTheme, isDark, gridTheme }}>
      {children}
    </ThemeContext.Provider>
  );
}
