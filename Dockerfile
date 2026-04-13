# Stage 1: Build React frontend
FROM node:22-alpine AS frontend
WORKDIR /app/client
COPY krakenreact.client/package.json krakenreact.client/package-lock.json ./
RUN npm ci
COPY krakenreact.client/ ./
RUN npm run build
# Verify the build produced output
RUN ls -la /app/client/dist/ && test -f /app/client/dist/index.html

# Stage 2: Build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY KrakenReact.Server/KrakenReact.Server.csproj KrakenReact.Server/
RUN dotnet restore KrakenReact.Server/KrakenReact.Server.csproj
COPY KrakenReact.Server/ KrakenReact.Server/
RUN dotnet publish KrakenReact.Server/KrakenReact.Server.csproj -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Copy published app
WORKDIR /app
COPY --from=backend /app/publish .

# Copy frontend build into wwwroot next to the DLL
COPY --from=frontend /app/client/dist /app/wwwroot

# Verify wwwroot exists and show full app directory
RUN echo "=== /app contents ===" && ls -la /app/ && echo "=== /app/wwwroot contents ===" && ls -la /app/wwwroot/ && test -f /app/wwwroot/index.html

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=4567
ENV ASPNETCORE_URLS=
EXPOSE 4567

ENTRYPOINT ["dotnet", "KrakenReact.Server.dll"]
