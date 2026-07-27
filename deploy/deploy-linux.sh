#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="/usr/share/uniflow"
SRC_DIR="$(cd "$(dirname "$0")/../src" && pwd)"
SERVICE_NAME="uniflow.service"
BINARY="UniFlow"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
log_info()  { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn()  { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

usage() {
    cat <<EOF
Usage: $0 [command] [options]

Commands:
    install          Install as systemd service (native)
    uninstall        Remove systemd service
    status           Check service status
    docker-build     Build Docker image
    docker-run       Run with docker-compose
    docker-stop      Stop docker-compose services
    docker-logs      View docker logs
    help             Show this help

Options:
    --tag NAME       Docker image tag (default: uniflow:latest)
EOF
}

if [ $# -eq 0 ]; then usage; exit 1; fi

CMD="${1:-help}"; shift 2>/dev/null || true
DOCKER_TAG="${DOCKER_TAG:-uniflow:latest}"
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

case "$CMD" in
    install)
        if [ "$EUID" -ne 0 ]; then log_error "Please run as root"; exit 1; fi
        id -u uniflow &>/dev/null || useradd -r -s /sbin/nologin -M uniflow
        mkdir -p "$INSTALL_DIR"
        log_info "Building $BINARY..."
        dotnet publish "$SRC_DIR/$BINARY/$BINARY.csproj" \
            --configuration Release \
            --runtime linux-x64 \
            --self-contained true \
            -p:PublishSingleFile=true \
            -p:EnableCompressionInSingleFile=true \
            -o "$INSTALL_DIR/$BINARY" 2>&1 | tail -3
        chown -R uniflow:uniflow "$INSTALL_DIR/$BINARY"
        log_info "Installing systemd unit..."
        cp "$(dirname "$0")/linux/uniflow.service" "/etc/systemd/system/$SERVICE_NAME"
        systemctl daemon-reload
        systemctl enable "$SERVICE_NAME"
        systemctl restart "$SERVICE_NAME"
        systemctl status "$SERVICE_NAME" --no-pager | head -10
        log_info "Done. Use 'journalctl -fu $SERVICE_NAME' to view logs."
        ;;
    uninstall)
        if [ "$EUID" -ne 0 ]; then log_error "Please run as root"; exit 1; fi
        systemctl stop "$SERVICE_NAME" 2>/dev/null || true
        systemctl disable "$SERVICE_NAME" 2>/dev/null || true
        rm -f "/etc/systemd/system/$SERVICE_NAME"
        rm -rf "$INSTALL_DIR/$BINARY"
        systemctl daemon-reload
        log_info "Uninstalled"
        ;;
    status)
        systemctl status "$SERVICE_NAME" --no-pager 2>/dev/null || \
            log_warn "Not installed. Try 'docker ps' for Docker deployment."
        ;;
    docker-build)
        log_info "Building Docker image: $DOCKER_TAG"
        docker build -t "$DOCKER_TAG" -f "$ROOT_DIR/Dockerfile" "$ROOT_DIR"
        log_info "Image built: $DOCKER_TAG"
        ;;
    docker-run)
        log_info "Starting UniFlow with docker-compose..."
        cd "$ROOT_DIR"
        docker compose up -d
        log_info "Running. Use 'docker compose logs -f' to watch."
        ;;
    docker-stop)
        cd "$ROOT_DIR"
        docker compose down
        log_info "Stopped"
        ;;
    docker-logs)
        cd "$ROOT_DIR"
        docker compose logs -f
        ;;
    help|*)
        usage
        ;;
esac