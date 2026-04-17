# KrakenReact

A real-time cryptocurrency trading dashboard built with ASP.NET Core and React, connected to the Kraken exchange via REST API and WebSocket feeds.

## Disclaimer

**USE AT YOUR OWN RISK.** KrakenReact is provided "as is", without warranty of any kind, express or implied, including but not limited to the warranties of merchantability, fitness for a particular purpose, accuracy, and non-infringement.

By downloading, installing, configuring, running, modifying, or otherwise using this software, **you acknowledge and agree that:**

- All use of this software is entirely at your own risk.
- This software places and manages **real orders** against your connected Kraken account using your API keys, and may result in **real financial loss**.
- Cryptocurrency markets are highly volatile. Prices, balances, profit/loss figures, order states, alerts, and any other information displayed may be delayed, incomplete, or incorrect.
- The author(s) make no representations about the accuracy, reliability, completeness, or timeliness of any data, calculation, signal, suggestion, notification, or automated behaviour produced by this software.
- **Nothing in this software constitutes financial, investment, tax, or trading advice.** Any "auto-trade", suggestion, or alert feature is experimental and must not be relied upon for trading decisions.
- You are solely responsible for your API key permissions, for reviewing every order before it is placed, for monitoring the software's behaviour, and for complying with the terms of service of Kraken and any applicable laws in your jurisdiction.
- **The author(s) and contributors shall not be liable for any direct, indirect, incidental, special, consequential, or exemplary damages** — including but not limited to loss of funds, lost profits, loss of data, trading losses, missed trades, erroneous orders, incorrect calculations, downtime, bugs, security incidents, or any other damages — arising out of or in any way connected with the use of, or inability to use, this software, even if advised of the possibility of such damages.

**If you do not agree to these terms, do not use this software.**

## Features

- **Live Dashboard** -- Pinned ticker cards, candlestick chart(s), live order book, open orders, and balances in a single view
- **Draggable / Resizable Layout** -- Dashboard panels can be rearranged and resized (react-grid-layout), with layout persisted to localStorage
- **Multi-Chart Support** -- Add or remove additional chart panels on the fly; each chart keeps its own interval selection
- **Live Order Book** -- Configurable-depth bid/ask panel streamed via SignalR, synced to the selected pair
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


## Docker Support

You can build and run KrakenReact using Docker for simplified deployment and consistent environments.

- See [DOCKER.md](DOCKER.md) for full instructions.

### Quick Start

1. **Build images:**
   ```bash
   docker build -t krakenreact-server -f KrakenReact.Server/Dockerfile .
   docker build -t krakenreact-client -f krakenreact.client/Dockerfile .
   ```
2. **Run containers:**
   ```bash
   docker run -d --name krakenreact-server -p 7247:7247 krakenreact-server
   docker run -d --name krakenreact-client -p 5173:5173 krakenreact-client
   ```
   Or use Docker Compose if available:
   ```bash
   docker-compose up --build
   ```
3. **Configuration:**
   - Mount your `appsettings.Local.json` or set environment variables for DB connection.
   - Set API keys and settings via the web UI after launch.

The `.dockerignore` file ensures fast, clean builds by excluding node_modules, bin/obj, test projects, etc.

---

## License

Private -- not for redistribution.

---

**Reminder:** use of this software is entirely at your own risk. The author(s) accept no liability whatsoever for any losses, errors, incorrect data, missed or erroneous orders, or any other damages arising from its use. See the [Disclaimer](#disclaimer) above.
