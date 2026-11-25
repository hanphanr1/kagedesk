using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace KageDesk.App.Tests;

public class BuildReleaseScriptTests
{
    private static string FindBuildReleaseScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "scripts", "build-release.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate scripts/build-release.ps1 from test output directory.");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunWindowsPowerShell(string command)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start powershell.exe");
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(30_000), "Windows PowerShell process timed out.");
        return (proc.ExitCode, stdout, stderr);
    }

    [Fact]
    public void UpdateChecksumManifest_InitialCreationAndSequentialUpsert_WritesUtf8NoBomAndPreservesPriorEntries()
    {
        string scriptPath = FindBuildReleaseScript();
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-sha-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string checksumPath = Path.Combine(tempDir, "SHA256SUMS.txt");
            string escapedScript = scriptPath.Replace("'", "''");
            string escapedChecksum = checksumPath.Replace("'", "''");

            string ps = $@"
$ErrorActionPreference = 'Stop'
. '{escapedScript}'
Update-ChecksumManifest -ChecksumPath '{escapedChecksum}' -ArtifactName 'KageDesk-v1.9.0-win-x64.zip' -Sha256Hash 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
Update-ChecksumManifest -ChecksumPath '{escapedChecksum}' -ArtifactName 'KageDesk-v2.0.0-win-x64.zip' -Sha256Hash 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'
Update-ChecksumManifest -ChecksumPath '{escapedChecksum}' -ArtifactName 'KageDesk-v2.0.0-win-x64.zip' -Sha256Hash 'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC'
";

            var (exitCode, stdout, stderr) = RunWindowsPowerShell(ps);
            Assert.True(exitCode == 0, $"PowerShell failed (exit {exitCode}). StdOut: {stdout}\nStdErr: {stderr}");
            Assert.True(File.Exists(checksumPath));

            byte[] rawBytes = File.ReadAllBytes(checksumPath);
            // Must be UTF-8 without BOM
            Assert.False(
                rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF,
                "SHA256SUMS.txt must be written as UTF-8 without BOM.");

            string[] lines = File.ReadAllLines(checksumPath, Encoding.UTF8);
            Assert.Equal(2, lines.Length);
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  KageDesk-v1.9.0-win-x64.zip", lines[0]);
            Assert.Equal("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc  KageDesk-v2.0.0-win-x64.zip", lines[1]);

            // No stray temp files left behind
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void MoveTempFileAtomically_WhenSourceLockedWithoutDeleteSharing_ThrowsAndPreservesExistingManifest()
    {
        string scriptPath = FindBuildReleaseScript();
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-sha-lock-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string checksumPath = Path.Combine(tempDir, "SHA256SUMS.txt");
            string lockedTempPath = $"{checksumPath}.{Guid.NewGuid():N}.tmp";
            const string originalContent = "1111111111111111111111111111111111111111111111111111111111111111  KageDesk-v1.9.0-win-x64.zip\r\n";

            File.WriteAllText(checksumPath, originalContent, new UTF8Encoding(false));
            File.WriteAllText(lockedTempPath, "2222222222222222222222222222222222222222222222222222222222222222  KageDesk-v2.0.0-win-x64.zip\r\n", new UTF8Encoding(false));

            string escapedScript = scriptPath.Replace("'", "''");
            string escapedChecksum = checksumPath.Replace("'", "''");
            string escapedTemp = lockedTempPath.Replace("'", "''");

            string ps = $@"
$ErrorActionPreference = 'Stop'
. '{escapedScript}'
$lockStream = [System.IO.File]::Open('{escapedTemp}', [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
$threwIoException = $false
try {{
    try {{
        Move-TempFileAtomically -SourceTempPath '{escapedTemp}' -DestinationPath '{escapedChecksum}'
    }}
    catch {{
        $ex = $_.Exception
        $inner = if ($ex.InnerException) {{ $ex.InnerException }} else {{ $ex }}
        Write-Output ""EXCEPTION_TYPE: $($inner.GetType().FullName)""
        Write-Output ""EXPECTED_ERROR: $($inner.Message)""
        if ($inner -is [System.IO.IOException]) {{
            $threwIoException = $true
        }}
        else {{
            throw ""Expected System.IO.IOException on locked temp file, got $($inner.GetType().FullName): $($inner.Message)""
        }}
    }}
}}
finally {{
    $lockStream.Dispose()
}}
if (-not $threwIoException) {{
    throw 'Expected Move-TempFileAtomically to throw System.IO.IOException when temp source is locked without delete sharing.'
}}
";

            var (exitCode, stdout, stderr) = RunWindowsPowerShell(ps);
            Assert.True(exitCode == 0, $"PowerShell failed (exit {exitCode}). StdOut: {stdout}\nStdErr: {stderr}");
            Assert.Contains("EXCEPTION_TYPE: System.IO.IOException", stdout);
            Assert.Contains("EXPECTED_ERROR:", stdout);

            // Existing SHA256SUMS.txt MUST remain completely intact with its original content!
            Assert.True(File.Exists(checksumPath), "Existing SHA256SUMS.txt must not be deleted on failed replacement.");
            string afterContent = File.ReadAllText(checksumPath, new UTF8Encoding(false));
            Assert.Equal(originalContent, afterContent);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
