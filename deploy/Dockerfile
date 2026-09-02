# === Build Stage ===
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY code/src/UniFlow/UniFlow.csproj UniFlow/
RUN dotnet restore UniFlow/UniFlow.csproj

COPY code/src/UniFlow/ UniFlow/
COPY code/src/UniFlow.sln .
RUN dotnet publish UniFlow/UniFlow.csproj \
    --configuration Release \
    --runtime linux-musl-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o /app

COPY deploy/appsettings.docker.json /app/appsettings.default.json
COPY deploy/docker/entrypoint.sh /app/entrypoint.sh

# === Runtime Stage ===
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S -g 1001 uniflow && \
    adduser -S -u 1001 -G uniflow uniflow && \
    mkdir -p /app/logs /app/DisposeFile /app/config && \
    chown -R uniflow:uniflow /app

RUN apk add --no-cache curl

ENV \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5100

COPY --from=build /app .
COPY code/src/UniFlow/wwwroot /app/wwwroot

RUN chmod +x /app/entrypoint.sh && chown -R uniflow:uniflow /app

USER uniflow

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -qO- http://localhost:5100/api/health || exit 1

EXPOSE 5100

ENTRYPOINT ["/bin/sh", "/app/entrypoint.sh"]