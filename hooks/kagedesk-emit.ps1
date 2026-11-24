param(
    [string]$Type = "tool_start",
    [string]$State = "",
    [string]$AgentId = "external",
    [string]$Name = "",
    [string]$Role = "",
    [string]$Message = "event",
    [string]$Repo = "",
    [string]$BuildStatus = "",
    [int]$Port = 4319,
    [string]$Token = ""
)

if ($Port -lt 1 -or $Port -gt 65535) {
    Write-Error "Invalid port: $Port (must be between 1 and 65535)"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    $tokenPath = Join-Path $env:APPDATA "KageDesk\bridge.token"
    if (Test-Path $tokenPath) {
        $Token = (Get-Content $tokenPath -Raw).Trim()
    }
}

$payload = [ordered]@{}
if (![string]::IsNullOrWhiteSpace($Type)) { $payload["type"] = $Type }
if (![string]::IsNullOrWhiteSpace($State)) { $payload["state"] = $State }
if (![string]::IsNullOrWhiteSpace($AgentId)) { $payload["agentId"] = $AgentId }
if (![string]::IsNullOrWhiteSpace($Name)) { $payload["name"] = $Name }
if (![string]::IsNullOrWhiteSpace($Role)) { $payload["role"] = $Role }
if (![string]::IsNullOrWhiteSpace($Message)) { $payload["message"] = $Message }
if (![string]::IsNullOrWhiteSpace($Repo)) { $payload["repo"] = $Repo }
if (![string]::IsNullOrWhiteSpace($BuildStatus)) { $payload["buildStatus"] = $BuildStatus }

$json = $payload | ConvertTo-Json -Compress
$headers = @{}
if (![string]::IsNullOrWhiteSpace($Token)) {
    $headers["X-KageDesk-Token"] = $Token
}

try {
    $response = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/event" -Method Post -ContentType "application/json" -Headers $headers -Body $json -TimeoutSec 3
    exit 0
}
catch {
    Write-Error "Failed to emit KageDesk event: $_"
    exit 1
}
