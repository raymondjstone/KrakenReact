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

### Trading & Orders
- **Live Dashboard** -- Pinned ticker cards, candlestick chart(s), live order book, open orders, and balances in a single view
- **Draggable / Resizable Layout** -- Dashboard panels can be rearranged and resized (react-grid-layout), with layout persisted to localStorage
- **Multi-Chart Support** -- Add or remove additional chart panels on the fly; each chart keeps its own interval selection
- **Live Order Book** -- Configurable-depth bid/ask panel streamed via SignalR, synced to the selected pair
- **Real-time Pricing** -- WebSocket V1 (public ticker) and V2 (private executions/balances) for live updates
- **Order Management** -- View, create, edit, and cancel limit orders directly from the UI
- **Symbol Constraint Validation** -- Order dialog enforces Kraken's per-symbol minimum quantity, minimum value, price decimal places, and quantity decimal places; constraints are displayed inline and inputs auto-round on blur

### Portfolio & Analytics
- **Portfolio Tracking** -- Balance overview with USD and GBP valuations, portfolio percentages, and order coverage (covered vs uncovered quantities for sell orders)
- **Profit/Loss** -- Proportional cost basis calculation from trade history with multi-currency support (GBP, EUR, USDT → USD conversion), net P/L and P/L percentage per asset
- **Analytics** -- Portfolio analytics and performance charts
- **Portfolio Rebalancing** -- Calculate drift from target allocations with buy/sell action plan; automated rebalance schedules with configurable cron, minimum drift threshold, alert-only or auto-execute modes

### Automation
- **Auto-Trade Engine** -- Rule-based order suggestions with weighted price analysis
- **DCA** -- Dollar-cost averaging rules engine: configurable symbols, amounts, and cron schedules, with per-rule last-run status and manual trigger
- **Stop-Loss** -- Automatic market sell when an asset price drops a configurable percentage below average cost basis (1–99.9%), checked every 5 minutes
- **Take-Profit** -- Automatic limit sell when an asset price rises a configurable percentage above average cost basis, checked every 5 minutes
- **Trailing Stop-Loss** -- Tracks the per-asset price high since monitoring began; triggers a market sell when price falls a configurable percentage from that peak
- **Profit Ladder** -- Sell a configured fraction of holdings when price rises above cost by a trigger percentage, with per-rule cooldown hours
- **Auto-Sell on Buy Fill** -- Automatically place a sell order at a configurable percentage above the fill price when a buy order executes
- **Price Alerts** -- Create, edit, and delete price threshold alerts; reset triggered alerts so they fire again; optional auto-order on trigger (places a limit buy or sell with a configurable offset % from the trigger price)
- **Auto-Cancel Stale Orders** -- Automatically cancel open orders older than a configurable number of days; separate toggles for buy and sell orders; runs daily at 09:00
- **Dry-Run Mode** -- Global setting that makes all scheduled jobs (DCA, Stop-Loss, Take-Profit, Trailing Stop, Profit Ladder, Auto-Sell, Price Alert auto-orders, Rebalance, Auto-Cancel) simulate their action and send a Pushover notification instead of placing any real orders

### Notifications
- **Pushover Notifications** -- Proximity alerts (0.1–20%) when open orders approach market price, drawdown alerts, staking reward alerts, and job execution notifications
- **Drawdown Alert** -- Computes 90-day peak-to-trough drawdown daily at 08:00 and sends a Pushover alert when the threshold is exceeded
- **Large Movement Alerts** -- Configurable threshold to temporarily surface assets with significant 24h price swings

### Data & History
- **Price History** -- Daily kline data stored in SQL Server, with automatic gap-filling from the Kraken API
- **Grouped Trades** -- Aggregated trade view by symbol with totals and averages
- **Ledger Browser** -- Full ledger history from Kraken (deposits, withdrawals, trades, staking)
- **Export to CSV** -- One-click CSV export on Trades, Grouped Trades, Orders, and Ledger pages
- **Delisted Asset Support** -- CSV-based fallback pricing for assets no longer tradeable on Kraken
- **Kraken Asset Normalisation** -- Handles Kraken's X-prefixed crypto names (XXBT, XXRP, XETH) and Z-prefixed fiat (ZUSD, ZGBP) transparently

### ML Predictions
- **ML Predictions** -- Machine-learning price direction forecasts with model quality metrics, Kelly sizing, regime detection, and confidence histograms (see [ML Predictions](#ml-predictions) section below)

### System & Settings
- **System Health** -- Live health check page showing database, WebSocket, pricing, and ML subsystem status
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

| Layer     | Technology                                              |
| --------- | ------------------------------------------------------- |
| Backend   | ASP.NET Core 10, Entity Framework Core, Serilog         |
| Frontend  | React 19, Vite, ag-grid Community, lightweight-charts   |
| Real-time | SignalR, Kraken WebSocket V1/V2                         |
| Database  | SQL Server                                              |
| Jobs      | Hangfire (recurring + fire-and-forget background jobs)  |
| Exchange  | Kraken.Net (CryptoExchange.Net)                         |

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

The application will automatically create and migrate all required tables on first run — no manual `dotnet ef` commands needed.

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

| Service / Job                 | Schedule         | Purpose                                                                                          |
| ----------------------------- | ---------------- | ------------------------------------------------------------------------------------------------ |
| `BackgroundTaskService`       | Continuous       | Startup data loading, periodic order/trade/balance refresh, kline priority loading, price alerts |
| `KrakenWebSocketV1Service`    | Continuous       | Public ticker feed — live prices for all ZUSD pairs                                              |
| `KrakenWebSocketV2Service`    | Continuous       | Private feed — execution reports and balance changes                                             |
| `DailyPriceRefreshJob`        | Configurable\*   | Fetches fresh OHLCV klines from Kraken for all tracked pairs (default 04:00)                     |
| `PredictionJob`               | Configurable\*   | Runs ML predictions for configured symbols (default 05:00)                                       |
| `StalePredictionRefreshJob`   | Every N minutes\*| Refreshes any prediction older than one candle interval (default every 15 min)                   |
| `PortfolioSnapshotJob`        | Daily 23:55      | Records total portfolio value to database for the history chart                                  |
| `StopLossTakeProfitJob`       | Every 5 min      | Checks all non-fiat balances against stop-loss, take-profit, and trailing stop thresholds        |
| `DrawdownAlertJob`            | Daily 08:00      | Computes 90-day drawdown and sends Pushover alert if threshold exceeded                          |
| `AutoCancelJob`               | Daily 09:00      | Cancels open orders older than the configured number of days (buy and/or sell, configurable)     |
| `DcaJob`                      | Per-rule cron    | Dollar-cost averaging — places a limit buy for each active DCA rule on its schedule              |
| `RebalanceJob`                | Per-schedule cron| Rebalance automation — sends alert or places orders when portfolio drift exceeds minimum          |

\* Configurable from Settings → Schedule in the UI.

## Configuration

All configuration is managed from the **Settings** tab:

### General
- Large movement threshold for temporary ticker pins
- Hide almost zero balances toggle (< 0.0001 units or < $0.01 value)
- Staking notification toggle
- Order proximity notification toggle with configurable threshold (0.1% – 20%)
- Theme preference (dark/light)
- Order dialog button presets (price offset % buttons and quantity % buttons)
- Auto-sell on buy fill — enable and set percentage above fill price
- Auto-add staking rewards to newest open sell order
- **Stop-Loss** — enable and set percentage below average cost (1–99.9%) for automatic market sells
- **Take-Profit** — enable and set percentage above average cost for automatic limit sells
- **Trailing Stop-Loss** — enable and set percentage drop from peak for automatic market sells
- **Drawdown Alert** — enable and set portfolio drawdown % that triggers a Pushover notification
- **Auto-Cancel Stale Orders** — enable, set age threshold in days, choose buy and/or sell orders
- **Dry-Run Jobs** — simulate all automated job actions via Pushover without placing real orders

### Schedule
- Daily price download time (default 04:00)
- Daily ML prediction job time (default 05:00)
- Stale prediction auto-refresh interval (default 15 minutes)

### API Keys
- Kraken API key and secret
- Pushover user key and app token

### Trading Lists
- Base currencies, blacklist, major coins, currency list, bad pairs, default pairs

### Asset Normalisations
- Custom Kraken name mappings (e.g. XXBT=BTC)

Pinned ticker pairs are also persisted to the database.

---

## Price Alerts

Alerts are managed from the **Price Alerts** tab:

- **Create** — set a symbol, direction (rises above / falls below), target price, and optional note
- **Edit** — modify any field on an existing alert
- **Reset** — clear the triggered state so the alert will fire again when price crosses the threshold
- **Auto-order** — optionally have the alert automatically place a limit buy or sell when it triggers, with a configurable quantity and price offset percentage from the trigger price
- Triggered alerts send a Pushover notification; if auto-order is enabled the order placement result is also notified

---

## Rebalance Schedules

The **Rebalance** tab includes an automated schedule manager:

- Define target allocations as `ASSET:PCT` pairs (e.g. `BTC:40,ETH:30,USD:30`)
- Set a cron schedule, minimum drift threshold, and optional note
- Choose **Alert Only** (sends a Pushover notification when any asset drifts beyond the threshold) or **Auto-Execute** (places limit orders to bring the portfolio back to target)
- Manually trigger any schedule with the **Run** button
- Each schedule registers as a Hangfire recurring job and is restored automatically on restart

---

## ML Predictions

The **Predictions** tab runs a machine-learning pipeline against Kraken OHLCV kline data and presents the results as per-symbol cards. This section explains every metric displayed and what it means in practice.

### How the model works

For each configured symbol the job:

1. Fetches the stored kline history (1-minute through 1-day candles, configurable).
2. Computes 23 technical features: RSI, MACD (signal and histogram), ATR (raw and as % of price), Bollinger Band width and %B, volume percentile, On-Balance Volume, ADX(14), Rate of Change(10), VWAP ratio, time-of-week seasonality indicator, and BTC market context (zeroed for XBT/USD itself to avoid circular reference).
3. Creates binary labels — did the close price rise over the next 1, 3, or 6 candles?
4. Trains two models on a strict **70/30 chronological split** (no data leakage): a **FastTree gradient-boosted decision tree** and a **logistic regression**.
5. Additionally runs **walk-forward cross-validation**: the data is split into rolling windows, each model is trained on an earlier window and tested on the following window, and the results are averaged. This gives a more realistic picture of out-of-sample performance.
6. The final prediction for each horizon is taken from whichever model scored highest on the test set.

---

### Per-card metrics explained

#### Direction and Confidence

| Display | Meaning |
|---|---|
| **↑ UP / ↓ DOWN** | The model's predicted direction for the next candle at the configured interval. |
| **Confidence %** | The model's probability score for that direction (0–100%). 50% = no edge, >55% starts to be meaningful, >65% is strong. This is the raw classifier output — it should not be interpreted as a reliable probability in isolation, especially for noisy short-term intervals. |
| **Probability bar** | Visual representation of the confidence score; green for UP, red for DOWN. |

#### Consensus

The model predicts three horizons simultaneously: **1 candle (1c), 3 candles (3c), and 6 candles (6c)**. The consensus badge summarises how many of the three agree:

- **3/3 → strong consensus** — all horizons point the same way. This is the most reliable signal.
- **2/3 → partial consensus** — majority agreement but with some horizon dissent.
- **1/3 or split** — the model is uncertain or seeing conflicting trends at different time scales.

Treat strong consensus signals more seriously than single-horizon predictions.

#### Actual Hit-Rate

> e.g. `57.3% actual hit-rate (44 checked)`

This is the **retrospective accuracy** of the model's past predictions for this symbol, computed by comparing old stored predictions to what the price actually did afterwards. It is the most honest measure of real-world model performance because it uses live predictions rather than back-tested data.

- **≥ 55%** (green) — the model has been beating random chance on this asset recently.
- **45–55%** (amber) — borderline; the model is near coin-flip territory.
- **< 45%** (red) — the model has been worse than random on this asset. Take predictions with extra scepticism.

The number in parentheses is how many past predictions have been evaluated. Small sample sizes make the hit-rate unreliable.

#### Market Regime

> e.g. `trending · ADX 28 · BB 4.2%`

The regime badge describes the **current market character** based on two indicators:

- **ADX (Average Directional Index, 14-period):** Measures trend strength, not direction. ADX < 20 = ranging/choppy, 20–40 = moderate trend, > 40 = strong trend.
- **BB width (Bollinger Band width as % of price):** Measures volatility. A narrow band means prices are compressed (often precedes a breakout); a wide band means high recent volatility.

Regime labels:
- `trending` — ADX ≥ 25. A directional model tends to perform better here.
- `ranging` — ADX < 20. Markets are oscillating; trend-following models are less reliable.
- `volatile` — BB width above the median. Price swings are elevated regardless of trend.
- `quiet` — BB width below median and ADX low. Low-information environment; low conviction.
- `breakout` — BB width has expanded sharply. A directional move may be underway.

#### Kelly Criterion

> e.g. `Kelly 4.2% · suggest $380`

The **Kelly criterion** is a formula from information theory that suggests the optimal fraction of your bankroll to risk on a bet given a known edge and win rate. It is computed from the model's historical win rate and average win/loss ratio.

**Half-Kelly** (displayed) is standard practice — most practitioners use half the full Kelly fraction to account for estimation error and reduce variance.

- **Kelly %** -- the suggested portfolio percentage to deploy on this signal.
- **Suggest $** -- the dollar amount implied by Kelly % applied to the total current portfolio value.

**Important caveats:** Kelly assumes the model's edge estimate is accurate, that the win/loss ratio is stable, and that position size is applied consistently across many trades. Crypto markets are far noisier than the assumptions require. Treat Kelly sizing as an upper bound, not a prescription.

#### Confidence Histogram

Clicking **Confidence histogram** shows a bar chart of **hit-rate by confidence decile** across all past predictions for this symbol. Each bar represents a 10-percentage-point confidence bucket (0–10%, 10–20%, …, 90–100%) and shows the fraction of predictions in that bucket that were correct.

What to look for:
- **Monotonically increasing** (bars get taller from left to right) — the model is well-calibrated; higher confidence genuinely correlates with higher accuracy. This is ideal.
- **Flat** — confidence scores don't predict accuracy; the model cannot distinguish easy from hard cases.
- **Inverted** — high-confidence predictions are less reliable than low-confidence ones. This can indicate overfitting.
- **Empty buckets** (greyed out) — the model rarely outputs predictions in that confidence range.

#### Per-Horizon Detail boxes (1c / 3c / 6c)

Each horizon box shows:

| Metric | Meaning |
|---|---|
| **Direction + confidence** | Predicted direction and probability for that specific horizon. |
| **FT acc** | FastTree model **test-set accuracy** on the 30% hold-out. Higher is better; > 55% is useful in crypto. |
| **AUC** | **Area Under the ROC Curve** for FastTree. 0.5 = random, 1.0 = perfect. A value > 0.55 suggests the model has genuine discriminative power. |
| **WF acc** | FastTree **walk-forward accuracy** — average accuracy across out-of-sample folds. This is more conservative than test-set accuracy and a better proxy for live performance. |
| **WF AUC** | Walk-forward AUC for FastTree. Same interpretation as AUC but averaged across folds. |
| **LR %** | Logistic regression test-set accuracy. |
| **WF LR** | Logistic regression walk-forward accuracy. |

Colour coding on the LR row: green ≥ 58%, amber ≥ 52%, red < 52%.

#### Benchmarks

| Metric | Meaning |
|---|---|
| **1c / 3c / 6c Buy & Hold** | What fraction of periods the price went up over each horizon, based on historical data. If the market rises 52% of the time, a model needs > 52% accuracy to be useful at all. A model that beats its benchmark is providing genuine value beyond simply expecting the market to go up. |
| **1c SMA crossover** | Accuracy of a simple moving-average crossover strategy over the same period and horizon. This is a naive technical benchmark — if the ML model cannot beat it, it offers little advantage. |

#### Summary Statistics

| Metric | Meaning |
|---|---|
| **WF folds** | Number of walk-forward validation windows used. More folds = more robust estimate of out-of-sample performance. |
| **Candles** | Training samples / test samples / total candles in the dataset. Larger datasets generally produce more reliable models; very small datasets (< 200 candles) produce unreliable results. |

#### Confidence Trend Sparkline

The small chart at the bottom of each card shows the model's **confidence score across the last 20 runs** for that symbol. Each dot is coloured green (UP prediction) or red (DOWN prediction). The dashed line shows the overall trend in confidence.

- Rising trend with consistent colour = model is growing more confident in a direction.
- Oscillating colours with flat confidence = model is uncertain, flipping direction frequently.
- Sustained high confidence in one direction = potentially stronger signal, but check actual hit-rate for calibration.

---

### Interpreting predictions responsibly

**Accuracy above ~55% in a noisy crypto market is genuinely useful — but nothing here is financial advice.** The benchmark columns exist specifically to show what chance alone looks like for each asset. A model that beats Buy & Hold accuracy and its SMA crossover benchmark has demonstrated some edge; one that does not has not.

Key limitations to keep in mind:

- Short intervals (1m, 5m, 15m) are dominated by noise. 1-hour and 1-day intervals tend to produce more stable models.
- A small candle history (< 300 candles) produces unreliable estimates for all metrics.
- Model performance varies over time as market regimes change. A model that worked well in a trending market may underperform in a ranging one and vice versa. The hit-rate and regime badge help track this.
- Walk-forward accuracy is always more conservative than test-set accuracy — if walk-forward is substantially lower, the model may be overfitting to the train period.
- Kelly sizing assumes your edge estimate is precise. In practice, use it as a rough ceiling, not an exact prescription.

---

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
