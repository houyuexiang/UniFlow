# === Build Stage ===
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY src/UniFlow/UniFlow.csproj UniFlow/
RUN dotnet restore UniFlow/UniFlow.csproj

COPY src/UniFlow/ UniFlow/
COPY src/UniFlow.sln .
RUN dotnet publish UniFlow/UniFlow.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained false \
    -o /app

# === Runtime Stage ===
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S -g 1001 uniflow && \
    adduser -S -u 1001 -G uniflow uniflow && \
    mkdir -p /app/logs /app/DisposeFile /app/config && \
    chown -R uniflow:uniflow /app

RUN apk add --no-cache icu-data-full curl

ENV \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5100

COPY --from=build /app .
COPY appsettings.docker.json /app/appsettings.default.json
COPY deploy/docker/entrypoint.sh /app/entrypoint.sh

RUN chmod +x /app/entrypoint.sh && chown -R uniflow:uniflow /app

USER uniflow

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -qO- http://localhost:5100/api/health || exit 1

EXPOSE 5100

ENTRYPOINT ["/bin/sh", "/app/entrypoint.sh"]