#!/bin/sh
set -e

# Initialize config from default if volume is empty
if [ ! -f /app/config/appsettings.json ]; then
    echo "[entrypoint] Initializing default config..."
    cp /app/appsettings.default.json /app/config/appsettings.json
fi

# Ensure appsettings.json symlink points to the config volume
ln -sf /app/config/appsettings.json /app/appsettings.json

echo "[entrypoint] Starting UniFlow..."
exec /app/UniFlow