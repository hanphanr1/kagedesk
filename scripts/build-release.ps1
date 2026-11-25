[CmdletBinding()]
param(
    [string]$Version = "2.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot

try {
    Write-Host "[1/4] Running full test suite..." -ForegroundColor Cyan
    dotnet test KageDesk.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Test suite failed with exit code $LASTEXITCODE"
    }

    Write-Host "[2/4] Building Release binaries..." -ForegroundColor Cyan
    dotnet build src/KageDesk.App/KageDesk.App.csproj -c Release --no-incremental --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE"
    }

    $distDir = Join-Path $repoRoot "dist"
    if (Test-Path $distDir) {
        Remove-Item -Recurse -Force $distDir
    }
    New-Item -ItemType Directory -Path $distDir | Out-Null

    $zipName = "KageDesk-v$Version-win-x64.zip"
    $zipPath = Join-Path $distDir $zipName
    $stagingDir = Join-Path $distDir "staging"
    New-Item -ItemType Directory -Path $stagingDir | Out-Null

    Write-Host "[3/4] Staging portable release files..." -ForegroundColor Cyan
    $binDir = Join-Path $repoRoot "src/KageDesk.App/bin/Release/net8.0-windows"

    $includeFiles = @(
        "KageDesk.exe",
        "KageDesk.dll",
        "KageDesk.runtimeconfig.json",
        "KageDesk.deps.json",
        "KageDesk.Core.dll",
        "KageDesk.Rendering.dll"
    )

    foreach ($file in $includeFiles) {
        $sourcePath = Join-Path $binDir $file
        if (Test-Path $sourcePath) {
            Copy-Item $sourcePath -Destination $stagingDir
        } else {
            throw "Expected release file missing: $sourcePath"
        }
    }

    $themesSource = Join-Path $binDir "themes"
    if (Test-Path $themesSource) {
        Copy-Item -Recurse $themesSource -Destination (Join-Path $stagingDir "themes")
    } else {
        throw "Themes directory missing from build output: $themesSource"
    }

    Write-Host "[4/4] Creating ZIP package and SHA256 checksums..." -ForegroundColor Cyan
    Compress-Archive -Path "$stagingDir/*" -DestinationPath $zipPath -Force
    Remove-Item -Recurse -Force $stagingDir

    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $shaEntry = "$hash  $zipName"
    $shaPath = Join-Path $distDir "SHA256SUMS.txt"
    Set-Content -Path $shaPath -Value $shaEntry -Encoding utf8

    $zipInfo = Get-Item $zipPath
    Write-Host "Successfully packaged release:" -ForegroundColor Green
    Write-Host "  ZIP:    $zipPath ($([math]::Round($zipInfo.Length / 1MB, 2)) MB)"
    Write-Host "  SHA256: $hash"
}
finally {
    Pop-Location
}
