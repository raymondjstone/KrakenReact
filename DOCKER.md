# Docker Support for KrakenReact

One image serves both the API and the built React frontend (a multi-stage build: Node builds the client, the .NET SDK
builds the server, and the ASP.NET runtime image runs the result). It listens on **port 4567**.

## Prerequisites
- [Docker](https://www.docker.com/products/docker-desktop) with Compose
- A reachable SQL Server database

## Running with Compose

1. Create a `.env` file next to `docker-compose.yml`:

   ```
   EFDB_CONNECTION_STRING=Server=YOURHOST;Database=Kraken;User Id=YOURUSER;Password=YOURPASSWORD;TrustServerCertificate=True;Max Pool Size=100;
   ```

   Compose refuses to start if this is missing.

2. Build and start:

   ```bash
   docker compose up --build -d
   ```

3. Open <http://localhost:4567>. Kraken and Pushover keys are entered in the app's Settings page.

## What the compose file sets up
- **Restart policy** `unless-stopped`, so the trading jobs and websocket feeds resume after a crash or reboot.
- **Health check**: a TCP connect to port 4567 every 30s (the runtime image has no curl).
- **30s stop grace period**, so an in-flight order placement can finish and be recorded before shutdown.
- The container runs as the unprivileged `app` user, not root.

## Running the image directly

```bash
docker build -t krakenreact .
docker run -d --name krakenreact -p 4567:4567 \
  -e "ConnectionStrings__EFDB=Server=...;Database=Kraken;..." \
  --restart unless-stopped krakenreact
```

## Notes
- The database must be reachable from inside the container (network/firewall). The schema is created and migrated
  automatically on start.
- **There is no login.** Anyone who can reach port 4567 can place and cancel orders and read settings, and the Hangfire
  dashboard at `/hangfire` is writable. Keep the port on a trusted network or behind a reverse proxy that authenticates.
- Prefer Docker secrets or an env file kept out of version control for the connection string.
- `.dockerignore` keeps `node_modules`, `bin/obj` and test projects out of the build context.
