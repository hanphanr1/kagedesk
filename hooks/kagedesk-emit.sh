curl -s -X POST "http://127.0.0.1:${4:-4319}/api/event" -H "Content-Type: application/json" -d "{\"type\":\"${1:-tool_start}\",\"agentId\":\"${2:-external}\",\"message\":\"${3:-event}\"}"
