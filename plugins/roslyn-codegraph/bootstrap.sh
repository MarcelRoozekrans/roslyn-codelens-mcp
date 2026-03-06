#!/usr/bin/env bash
set -euo pipefail

TOOL_NAME="roslyn-codegraph-mcp"

if ! command -v "$TOOL_NAME" &>/dev/null; then
    echo "[roslyn-codegraph] Installing $TOOL_NAME dotnet global tool..." >&2
    dotnet tool install -g "$TOOL_NAME" >&2
fi

exec "$TOOL_NAME" "$@"
