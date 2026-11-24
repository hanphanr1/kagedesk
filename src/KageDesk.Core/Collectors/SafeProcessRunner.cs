using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KageDesk.Core.Collectors;

public static class ExecutableResolver
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void ClearCache() => Cache.Clear();

    public static string? Resolve(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (Cache.TryGetValue(command, out var cached))
        {
            return cached;
        }

        string? resolved = ResolveInternal(command);
        Cache[command] = resolved;
        return resolved;
    }

    private static string? ResolveInternal(string command)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;

        char separator = OperatingSystem.IsWindows() ? ';' : ':';
        string[] dirs = pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var extensions = GetExecutableExtensions();

        foreach (var rawDir in dirs)
        {
            // Security: Never search current directory or relative paths
            if (rawDir is "." or ".." || !Path.IsPathRooted(rawDir))
            {
                continue;
            }

            if (!Directory.Exists(rawDir)) continue;

            // If command already has an extension that exists
            if (Path.HasExtension(command))
            {
                string full = Path.Combine(rawDir, command);
                if (File.Exists(full))
                {
                    return Path.GetFullPath(full);
                }
            }
            else
            {
                // Check all extensions
                foreach (var ext in extensions)
                {
                    string candidate = Path.Combine(rawDir, command + ext);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        return null;
    }

    private static string[] GetExecutableExtensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new[] { "" };
        }

        string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return new[] { ".exe", ".cmd", ".bat" };
        }

        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public static class SafeProcessRunner
{
    private static readonly Regex GitHubRepoRegex = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    public static bool IsValidGitHubRepo(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return false;
        return GitHubRepoRegex.IsMatch(repo);
    }

    public static async Task<CliResult> ExecuteAsync(
        string executableName,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string? fullPath = ExecutableResolver.Resolve(executableName);
        if (fullPath == null)
        {
            return new CliResult(-1, "", $"Executable '{executableName}' not found in PATH");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(timeout);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process == null)
            {
                return new CliResult(-1, "", $"Failed to start process: {executableName}");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(deadlineCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(deadlineCts.Token);
            var exitTask = process.WaitForExitAsync(deadlineCts.Token);

            await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);

            return new CliResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process?.Kill(entireProcessTree: true); } catch { }

            if (cancellationToken.IsCancellationRequested)
            {
                return new CliResult(-1, "", "Operation canceled");
            }
            return new CliResult(-1, "", $"Command '{executableName}' timed out", TimedOut: true);
        }
        catch (Win32Exception ex)
        {
            return new CliResult(-1, "", ex.Message);
        }
        catch (Exception ex)
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            return new CliResult(-1, "", ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }
}
