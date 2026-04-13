# Docker Support for KrakenReact

You can build and run the KrakenReact stack using Docker for easier deployment and environment consistency.

## Prerequisites
- [Docker](https://www.docker.com/products/docker-desktop) installed
- (Optional) [Docker Compose](https://docs.docker.com/compose/) if using a multi-container setup

## Building and Running

### 1. Build the Docker Images

From the repository root:

```bash
docker build -t krakenreact-server -f KrakenReact.Server/Dockerfile .
docker build -t krakenreact-client -f krakenreact.client/Dockerfile .
```

### 2. Run the Containers

You can run the containers individually:

```bash
docker run -d --name krakenreact-server -p 7247:7247 krakenreact-server
# (Optional) If you want to run the client separately:
docker run -d --name krakenreact-client -p 5173:5173 krakenreact-client
```

Or use Docker Compose (if you have a `docker-compose.yml`):

```bash
docker-compose up --build
```

### 3. Environment Variables & Configuration
- The backend expects a valid SQL Server connection string. You can provide this via environment variables or by mounting your `appsettings.Local.json`.
- API keys and other settings can be set via the web UI after first launch.

### 4. .dockerignore
- The `.dockerignore` file ensures that build context does not include unnecessary files (node_modules, bin/obj, test projects, etc.) for faster and smaller builds.

### 5. Updating Images
- Rebuild the images after code changes:

```bash
docker build -t krakenreact-server -f KrakenReact.Server/Dockerfile .
docker build -t krakenreact-client -f krakenreact.client/Dockerfile .
```

## Notes
- Make sure your SQL Server instance is accessible from inside the container (network/firewall).
- For production, you may want to use Docker secrets or environment variables for sensitive settings.
- The client and server images can be deployed independently or together depending on your infrastructure.

---

For more details, see the Dockerfiles in `KrakenReact.Server/` and `krakenreact.client/`.
