#!/usr/bin/env bash
set -euo pipefail

TYPE="${1:-tool_start}"
AGENT_ID="${2:-external}"
MESSAGE="${3:-event}"
PORT="${4:-4319}"
TOKEN="${5:-}"

# Validate numeric port in 1..65535
if ! [[ "$PORT" =~ ^[0-9]+$ ]] || [ "$PORT" -lt 1 ] || [ "$PORT" -gt 65535 ]; then
    echo "Error: Invalid port: $PORT (must be between 1 and 65535)" >&2
    exit 1
fi

# Locate token if not provided
if [ -z "$TOKEN" ]; then
    if [ -n "${APPDATA:-}" ] && [ -f "$APPDATA/KageDesk/bridge.token" ]; then
        TOKEN=$(sed '1s/^\xef\xbb\xbf//' "$APPDATA/KageDesk/bridge.token" | tr -d '\r\n')
    elif [ -f "$HOME/.config/kagedesk/bridge.token" ]; then
        TOKEN=$(sed '1s/^\xef\xbb\xbf//' "$HOME/.config/kagedesk/bridge.token" | tr -d '\r\n')
    fi
fi

# Construct JSON payload safely without unescaped interpolation
if command -v jq >/dev/null 2>&1; then
    JSON_BODY=$(jq -n \
        --arg type "$TYPE" \
        --arg agentId "$AGENT_ID" \
        --arg message "$MESSAGE" \
        '{type: $type, agentId: $agentId, message: $message}')
elif command -v python3 >/dev/null 2>&1; then
    JSON_BODY=$(python3 -c '
import json, sys
data = {"type": sys.argv[1], "agentId": sys.argv[2], "message": sys.argv[3]}
print(json.dumps(data))
' "$TYPE" "$AGENT_ID" "$MESSAGE")
else
    echo "Error: jq or python3 is required for safe JSON encoding" >&2
    exit 1
fi

# Prepare curl arguments
CURL_ARGS=(
    --silent
    --noproxy 127.0.0.1
    --max-time 3
    --fail-with-body
    -X POST "http://127.0.0.1:$PORT/api/event"
    -H "Content-Type: application/json"
)

if [ -n "$TOKEN" ]; then
    CURL_ARGS+=(-H "X-KageDesk-Token: $TOKEN")
fi

curl "${CURL_ARGS[@]}" -d "$JSON_BODY"
