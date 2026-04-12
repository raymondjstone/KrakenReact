# KrakenReact

A real-time cryptocurrency trading dashboard built with ASP.NET Core and React, connected to the Kraken exchange via REST API and WebSocket feeds.

## Features

- **Live Dashboard** -- Pinned ticker cards, candlestick chart, open orders, and balances in a single view
- **Real-time Pricing** -- WebSocket V1 (public ticker) and V2 (private executions/balances) for live updates
- **Order Management** -- View, create, edit, and cancel limit orders directly from the UI
- **Portfolio Tracking** -- Balance overview with USD and GBP valuations, portfolio percentages, and order coverage (covered vs uncovered quantities for sell orders)
- **Profit/Loss** -- Proportional cost basis calculation from trade history with multi-currency support (GBP, EUR, USDT → USD conversion), net P/L and P/L percentage per asset
- **Price History** -- Daily kline data stored in SQL Server, with automatic gap-filling from the Kraken API
- **Large Movement Alerts** -- Configurable threshold to temporarily surface assets with significant 24h price swings
- **Auto-Trade Engine** -- Rule-based order suggestions with weighted price analysis
- **Grouped Trades** -- Aggregated trade view by symbol with totals and averages
- **Ledger Browser** -- Full ledger history from Kraken (deposits, withdrawals, trades, staking)
- **Delisted Asset Support** -- CSV-based fallback pricing for assets no longer tradeable on Kraken
- **Kraken Asset Normalisation** -- Handles Kraken's X-prefixed crypto names (XXBT, XXRP, XETH) and Z-prefixed fiat (ZUSD, ZGBP) transparently with improved price lookups
- **Pushover Notifications** -- Configurable proximity alerts (0.1-20%) when open orders approach the current market price, with optional staking reward notifications
- **Dark/Light Theme** -- Defaults to dark mode with theme preference persisted to database
- **Smart Balance Filtering** -- Optional "Hide Almost Zero Balances" setting (filters balances with < 0.0001 units OR < $0.01 value)
- **Real-time Order Updates** -- Order amendments automatically recalculate distance, value, and balance covered/uncovered amounts with SignalR broadcasts
- **Configurable Settings** -- API keys, trading lists, blacklists, asset normalisations, notification preferences, and UI settings managed from the Settings page and persisted in the database
- **Graceful Shutdown** -- Server shutdown button that notifies connected clients via SignalR

## Architecture

```
KrakenReact.Server/          ASP.NET Core backend (.NET 10)
  Controllers/               REST API endpoints
  Services/                  Background services, Kraken API, WebSocket handlers
  Hubs/                      SignalR hub for real-time push
  Data/                      EF Core DbContext and data access
  Models/                    Entity models
  DTOs/                      Data transfer objects

krakenreact.client/          React frontend (Vite)
  src/components/            Dashboard, Watchlist, TickerCard, OrderDialog, etc.
  src/pages/                 Tab pages (Balances, Orders, Trades, Settings, etc.)
  src/api/                   Axios client and SignalR connection
```

### Key Technologies

| Layer    | Technology                                      |
| -------- | ----------------------------------------------- |
| Backend  | ASP.NET Core 10, Entity Framework Core, Serilog |
| Frontend | React 19, Vite, ag-grid Community, lightweight-charts |
| Real-time | SignalR, Kraken WebSocket V1/V2                |
| Database | SQL Server                                      |
| Exchange | Kraken.Net (CryptoExchange.Net)                 |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (v18+)
- SQL Server instance (local or remote)
- A Kraken account with API keys (read + trade permissions)

## Getting Started

### 1. Clone and install dependencies

```bash
git clone <repo-url>
cd KrakenReact/KrakenReact

# Frontend dependencies
cd krakenreact.client
npm install
cd ..
```

### 2. Configure the database connection

Create `KrakenReact.Server/appsettings.Local.json` (this file is gitignored):

```json
{
  "ConnectionStrings": {
    "EFDB": "Server=YOUR_SERVER;Database=Kraken;User Id=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=True;Max Pool Size=100;"
  }
}
```

The application will automatically create missing tables on first run.

### 3. Configure Kraken API keys

API keys can be set in one of two ways:

- **Via the Settings page** in the UI (stored in the database `AppSettings` table)
- **Via the legacy `EFAppCreds` table** (auto-migrated to AppSettings on startup)

### 4. Run the application

```bash
cd KrakenReact.Server
dotnet run
```

This starts both the ASP.NET backend (https://localhost:7247) and the Vite dev server (http://localhost:5173) automatically. Open http://localhost:5173 in your browser.

## Background Services

| Service                  | Purpose                                                              |
| ------------------------ | -------------------------------------------------------------------- |
| `BackgroundTaskService`  | Orchestrates startup data loading, kline refresh (daily at 04:00), periodic order/trade/balance refresh |
| `KrakenWebSocketV1Service` | Public ticker feed -- live prices for all ZUSD pairs              |
| `KrakenWebSocketV2Service` | Private feed -- execution reports and balance changes             |

## Configuration

All configuration is managed from the **Settings** tab:

- **General** 
  - Large movement threshold for temporary ticker pins
  - Hide almost zero balances toggle (< 0.0001 units or < $0.01 value)
  - Staking notification toggle
  - Order proximity notification toggle with configurable threshold (0.1% - 20%)
  - Theme preference (dark/light)
- **API Keys** -- Kraken API key/secret, Pushover credentials
- **Trading Lists** -- Base currencies, blacklist, major coins, currency list, bad pairs, default pairs
- **Asset Normalisations** -- Custom Kraken name mappings (e.g. XXBT=XBT)

Pinned ticker pairs are also persisted to the database.

## Recent Improvements

### Profit/Loss Tracking
- **Proportional Cost Basis** -- When selling assets, the cost basis is proportionally adjusted for remaining holdings
- **Multi-Currency Support** -- Trades in GBP, EUR, and USDT are automatically converted to USD for accurate P/L calculations
- **Fiat Exclusion** -- Fiat currencies (USD, GBP, EUR, etc.) correctly show blank P/L fields instead of zero

### Order Management Enhancements
- **Real-time Recalculation** -- When editing an order, all derived fields (Latest Price, Distance, Distance %, Order Value) are automatically recalculated
- **Balance Coverage Updates** -- Covered/uncovered quantities for balances update immediately when orders are created, edited, or cancelled
- **Asset Matching** -- Improved matching logic handles Bitcoin (XBT/BTC) and other aliased assets correctly across orders and balances
- **SignalR Broadcasting** -- Order and balance updates are pushed to all connected clients in real-time

### Price Normalization
- **Enhanced Lookups** -- `LatestPrice()` now iterates through all matching symbols to handle XBT/BTC variations
- **Symbol Resolution** -- `ResolveSymbolKey()` method maps normalized names (BTC/USD) to Kraken WebSocket keys (XBT/USD)
- **Duplicate Prevention** -- WebSocket V2 balance handler prevents XBT/BTC duplication

### Settings Persistence
- **Database-First** -- All settings including theme, notifications, and filters are persisted to the database
- **Automatic Migration** -- Legacy settings are migrated to the new AppSettings table on startup
- **Runtime Updates** -- Settings changes reload into TradingStateService without restart
- **Dark Mode Default** -- Application defaults to dark theme on first launch

### UI/UX Improvements
- **Hide Almost Zero Balances** -- Smart filter based on both quantity (< 0.0001) and USD value (< $0.01)
- **Configurable Notifications** -- Order proximity alerts with adjustable threshold (0.1-20%) and on/off toggle
- **Theme Persistence** -- Theme preference survives page refreshes and syncs across browser tabs
- **Settings Debouncing** -- Settings auto-save after 1 second of inactivity to prevent excessive writes

## License

Private -- not for redistribution.
