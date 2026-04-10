# KrakenReact

A real-time cryptocurrency trading dashboard built with ASP.NET Core and React, connected to the Kraken exchange via REST API and WebSocket feeds.

## Features

- **Live Dashboard** -- Pinned ticker cards, candlestick chart, open orders, and balances in a single view
- **Real-time Pricing** -- WebSocket V1 (public ticker) and V2 (private executions/balances) for live updates
- **Order Management** -- View, create, edit, and cancel limit orders directly from the UI
- **Portfolio Tracking** -- Balance overview with USD and GBP valuations, portfolio percentages, and order coverage (covered vs uncovered quantities)
- **Profit/Loss** -- Cost basis calculation from trade history with net P/L per asset
- **Price History** -- Daily kline data stored in SQL Server, with automatic gap-filling from the Kraken API
- **Large Movement Alerts** -- Configurable threshold to temporarily surface assets with significant 24h price swings
- **Auto-Trade Engine** -- Rule-based order suggestions with weighted price analysis
- **Grouped Trades** -- Aggregated trade view by symbol with totals and averages
- **Ledger Browser** -- Full ledger history from Kraken (deposits, withdrawals, trades, staking)
- **Delisted Asset Support** -- CSV-based fallback pricing for assets no longer tradeable on Kraken
- **Kraken Asset Normalisation** -- Handles Kraken's X-prefixed crypto names (XXBT, XXRP, XETH) and Z-prefixed fiat (ZUSD, ZGBP) transparently
- **Pushover Notifications** -- Alerts when open orders approach the current market price
- **Dark/Light Theme** -- Toggle between dark and light mode
- **Configurable Settings** -- API keys, trading lists, blacklists, and asset normalisations managed from the Settings page and persisted in the database
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

- **General** -- Large movement threshold for temporary ticker pins
- **API Keys** -- Kraken API key/secret, Pushover credentials
- **Trading Lists** -- Base currencies, blacklist, major coins, currency list, bad pairs, default pairs
- **Asset Normalisations** -- Custom Kraken name mappings (e.g. XXBT=XBT)

Pinned ticker pairs are also persisted to the database.

## License

Private -- not for redistribution.
