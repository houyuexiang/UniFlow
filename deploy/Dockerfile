# === Build Stage ===
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY code/src/UniFlow/UniFlow.csproj UniFlow/
RUN dotnet restore UniFlow/UniFlow.csproj

COPY code/src/UniFlow/ UniFlow/
COPY code/src/UniFlow.sln .
COPY deploy/appsettings.docker.json /app/appsettings.default.json
COPY deploy/docker/entrypoint.sh /app/entrypoint.sh

RUN chmod +x /app/entrypoint.sh && chown -R uniflow:uniflow /app

USER uniflow

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -qO- http://localhost:5100/api/health || exit 1

EXPOSE 5100

ENTRYPOINT ["/bin/sh", "/app/entrypoint.sh"]