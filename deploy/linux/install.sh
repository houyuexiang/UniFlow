#!/usr/bin/env bash
# UniFlow Install Script
set -euo pipefail

INSTALL_DIR="/usr/share/uniflow"
SERVICE_NAME="uniflow.service"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
log_info()  { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn()  { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

if [ "$EUID" -ne 0 ]; then log_error "Please run as root: sudo $0"; exit 1; fi

# Create system user
id -u uniflow &>/dev/null || {
    useradd -r -s /sbin/nologin -M uniflow
    log_info "Created system user uniflow"
}

# Install files
mkdir -p "$INSTALL_DIR"
cp -f "$SCRIPT_DIR/UniFlow"          "$INSTALL_DIR/"
cp -f "$SCRIPT_DIR/libe_sqlite3.so"  "$INSTALL_DIR/"
cp -rf "$SCRIPT_DIR/wwwroot"         "$INSTALL_DIR/"
if [ ! -f "$INSTALL_DIR/appsettings.json" ]; then
    cp -f "$SCRIPT_DIR/appsettings.json" "$INSTALL_DIR/"
    log_info "Installed default config file"
else
    log_warn "appsettings.json already exists, keeping existing config"
fi

# Permissions
chown -R uniflow:uniflow "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/UniFlow"

# Install systemd service
cat > "/etc/systemd/system/$SERVICE_NAME" <<EOF
[Unit]
Description=UniFlow - Laboratory Automation Workflow Service (.NET 10)
After=network.target
Wants=network.target

[Service]
Type=simple
ExecStart=$INSTALL_DIR/UniFlow
WorkingDirectory=$INSTALL_DIR
Restart=on-failure
RestartSec=10
User=uniflow
Group=uniflow
Environment=ASPNETCORE_ENVIRONMENT=Production
StandardOutput=journal
StandardError=journal
NoNewPrivileges=true
PrivateDevices=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl restart "$SERVICE_NAME"

# Optional: enable auto-start on boot
if [ "${1:-}" = "--enable" ]; then
    systemctl enable "$SERVICE_NAME"
    log_info "Auto-start enabled"
else
    log_warn "Auto-start not enabled (to enable: systemctl enable $SERVICE_NAME)"
fi

# Wait for startup and check health
log_info "Waiting for service to start..."
for i in $(seq 1 10); do
    sleep 2
    if curl -sf http://localhost:5100/api/health >/dev/null 2>&1; then
        break
    fi
done

if curl -sf http://localhost:5100/api/health >/dev/null 2>&1; then
    log_info "UniFlow started successfully: http://localhost:5100"
    log_info "Health check: http://localhost:5100/api/health"
    log_info "View logs: journalctl -fu $SERVICE_NAME"
else
    log_warn "Service started but health check failed. Check logs: journalctl -u $SERVICE_NAME"
fi

echo
echo "========================================"
echo " UniFlow Installation Complete"
echo "========================================"
echo " Install Dir: $INSTALL_DIR"
echo " Config File: $INSTALL_DIR/appsettings.json"
echo " Web Admin:   http://<this-IP>:5100"
echo ""
echo " Common Commands:"
echo "   systemctl start uniflow      Start"
echo "   systemctl stop uniflow       Stop"
echo "   systemctl restart uniflow    Restart"
echo "   journalctl -fu uniflow       View logs"
echo "========================================"