#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0-alpine"
CMD="${1:-build}"

VERSION="1.0.2"
DATE=$(date +%Y%m%d)

case "$CMD" in
  build)
    echo "=== Building UniFlow with .NET 10 SDK ==="
    docker run --rm -v "$ROOT:/project" -w /project/code/src "$SDK_IMAGE" \
      dotnet build UniFlow.sln --configuration Release
    ;;
  test)
    echo "=== Running Tests with .NET 10 SDK ==="
    docker run --rm -v "$ROOT:/project" -w /project/code/src "$SDK_IMAGE" \
      dotnet test ../tests/UniFlow.Tests/UniFlow.Tests.csproj --configuration Release
    ;;
  publish)
    echo "=== Publishing UniFlow ==="
    docker run --rm -v "$ROOT:/project" -w /project/code/src "$SDK_IMAGE" \
      dotnet publish UniFlow/UniFlow.csproj \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained false \
        -o /project/release/publish
    echo "Published to $ROOT/release/publish"
    ;;
  docker)
    echo "=== Building Docker image ==="
    docker build -t "uniflow:${VERSION}-${DATE}" \
      -t uniflow:latest \
      -f "$ROOT/deploy/Dockerfile" "$ROOT"
    echo "Tagged: uniflow:${VERSION}-${DATE}, uniflow:latest"
    ;;
  *)
    echo "Usage: $0 {build|test|publish|docker}"
    exit 1
    ;;
esac