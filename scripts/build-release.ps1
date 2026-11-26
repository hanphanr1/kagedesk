[CmdletBinding()]
param(
    [string]$Version = "2.2.0"
)

$ErrorActionPreference = "Stop"

function Move-TempFileAtomically {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceTempPath,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    if (-not [System.IO.File]::Exists($SourceTempPath)) {
        throw "Source temp file does not exist: $SourceTempPath"
    }

    if ([System.IO.File]::Exists($DestinationPath)) {
        [System.IO.File]::Replace($SourceTempPath, $DestinationPath, [NullString]::Value, $true)
    }
    else {
        [System.IO.File]::Move($SourceTempPath, $DestinationPath)
    }
}

function Update-ChecksumManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ChecksumPath,

        [Parameter(Mandatory = $true)]
        [string]$ArtifactName,

        [Parameter(Mandatory = $true)]
        [string]$Sha256Hash
    )

    $normalizedHash = $Sha256Hash.Trim().ToLowerInvariant()
    $shaEntry = "$normalizedHash  $ArtifactName"

    $entries = [System.Collections.Generic.List[string]]::new()
    if ([System.IO.File]::Exists($ChecksumPath)) {
        $existingLines = [System.IO.File]::ReadAllLines($ChecksumPath, [System.Text.Encoding]::UTF8)
        foreach ($line in $existingLines) {
            $trimmed = $line.Trim()
            if ($trimmed.Length -gt 0) {
                $parts = $trimmed -split '\s+', 2
                if ($parts.Count -eq 2) {
                    if ($parts[1].Trim() -ne $ArtifactName) {
                        $entries.Add($trimmed)
                    }
                }
                elseif (-not $trimmed.EndsWith($ArtifactName)) {
                    $entries.Add($trimmed)
                }
            }
        }
    }

    $entries.Add($shaEntry)
    [string[]]$sortedEntries = $entries.ToArray()
    [System.Array]::Sort($sortedEntries, [System.StringComparer]::Ordinal)

    $tempPath = "$ChecksumPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    try {
        [System.IO.File]::WriteAllLines($tempPath, $sortedEntries, $utf8NoBom)
        Move-TempFileAtomically -SourceTempPath $tempPath -DestinationPath $ChecksumPath
    }
    finally {
        if ([System.IO.File]::Exists($tempPath)) {
            try {
                [System.IO.File]::Delete($tempPath)
            }
            catch {
                # Swallow cleanup failures on locked temp files so the original exception propagates
            }
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "Invalid version format: '$Version'. Must match SemVer pattern (e.g. 2.0.0 or 2.0.0-rc1)."
    }

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    Push-Location $repoRoot

    try {
        Write-Host "[1/4] Running full test suite..." -ForegroundColor Cyan
        dotnet test KageDesk.sln -c Release --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Test suite failed with exit code $LASTEXITCODE"
        }

        $distDir = Join-Path $repoRoot "dist"
        if (-not (Test-Path $distDir)) {
            New-Item -ItemType Directory -Path $distDir | Out-Null
        }

        $uniqueId = [Guid]::NewGuid().ToString("N")
        $stagingDir = Join-Path $distDir "staging-$uniqueId"
        $publishDir = Join-Path $stagingDir "publish"
        $packageDir = Join-Path $stagingDir "package"
        New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
        New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

        try {
            Write-Host "[2/4] Publishing Release binaries for win-x64 (v$Version)..." -ForegroundColor Cyan
            dotnet publish src/KageDesk.App/KageDesk.App.csproj -c Release -r win-x64 --self-contained false -p:Version=$Version -o $publishDir --nologo
            if ($LASTEXITCODE -ne 0) {
                throw "Publish failed with exit code $LASTEXITCODE"
            }

            Write-Host "[3/4] Staging package assets (binaries, themes, hooks, docs)..." -ForegroundColor Cyan
            # Copy published binaries and runtime files
            Copy-Item -Path "$publishDir/*" -Destination $packageDir -Recurse -Force

            # Copy themes from repo root
            $repoThemes = Join-Path $repoRoot "themes"
            if (Test-Path $repoThemes) {
                Copy-Item -Path $repoThemes -Destination $packageDir -Recurse -Force
            } else {
                throw "Themes directory missing from repo: $repoThemes"
            }

            # Copy hooks from repo root
            $repoHooks = Join-Path $repoRoot "hooks"
            if (Test-Path $repoHooks) {
                Copy-Item -Path $repoHooks -Destination $packageDir -Recurse -Force
            } else {
                throw "Hooks directory missing from repo: $repoHooks"
            }

            # Copy README and LICENSE
            $readme = Join-Path $repoRoot "README.md"
            if (Test-Path $readme) {
                Copy-Item -Path $readme -Destination $packageDir -Force
            }
            $license = Join-Path $repoRoot "LICENSE"
            if (Test-Path $license) {
                Copy-Item -Path $license -Destination $packageDir -Force
            }

            Write-Host "[4/4] Creating ZIP package, verifying entries, and calculating checksums..." -ForegroundColor Cyan
            $zipName = "KageDesk-v$Version-win-x64.zip"
            $zipPath = Join-Path $distDir $zipName
            if (Test-Path $zipPath) {
                Remove-Item -Force $zipPath
            }

            Compress-Archive -Path "$packageDir/*" -DestinationPath $zipPath -Force

            # Self-verify critical ZIP contents
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
            try {
                $entryNames = $zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
                $requiredEntries = @(
                    "KageDesk.exe",
                    "KageDesk.dll",
                    "KageDesk.runtimeconfig.json",
                    "KageDesk.deps.json",
                    "KageDesk.Core.dll",
                    "KageDesk.Rendering.dll",
                    "README.md",
                    "LICENSE",
                    "themes/shinobi-hq/theme.json",
                    "themes/cyber-bunker/theme.json",
                    "themes/cozy-lofi/theme.json",
                    "hooks/kagedesk-emit.ps1",
                    "hooks/kagedesk-emit.sh",
                    "hooks/claude-code-hooks.example.json",
                    "hooks/claude-code-hooks.windows.example.json"
                )

                $missingEntries = @()
                foreach ($req in $requiredEntries) {
                    if ($entryNames -notcontains $req) {
                        $missingEntries += $req
                    }
                }

                if ($missingEntries.Count -gt 0) {
                    throw "Release ZIP verification failed! Missing required entries: $($missingEntries -join ', ')"
                }

                Write-Host "Verified all $($requiredEntries.Count) critical entries in $zipName." -ForegroundColor Green
            }
            finally {
                $zip.Dispose()
            }

            $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $shaPath = Join-Path $distDir "SHA256SUMS.txt"
            Update-ChecksumManifest -ChecksumPath $shaPath -ArtifactName $zipName -Sha256Hash $hash

            $zipInfo = Get-Item $zipPath
            Write-Host "Successfully packaged and verified release:" -ForegroundColor Green
            Write-Host "  ZIP:    $zipPath ($([math]::Round($zipInfo.Length / 1MB, 2)) MB)"
            Write-Host "  SHA256: $hash"
        }
        finally {
            if (Test-Path $stagingDir) {
                Remove-Item -Recurse -Force $stagingDir -ErrorAction SilentlyContinue
            }
        }
    }
    finally {
        Pop-Location
    }
}
