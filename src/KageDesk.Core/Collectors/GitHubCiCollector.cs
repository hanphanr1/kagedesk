using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using KageDesk.Core.Models;
using KageDesk.Core.State;

namespace KageDesk.Core.Collectors;

public class GitHubCiCollector
{
    private readonly HqStateAggregator _aggregator;
    private readonly Func<string[], TimeSpan, CancellationToken, Task<CliResult>> _commandRunner;

    public bool Enabled { get; set; }
    public List<string> Repositories { get; } = new();
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public GitHubCiCollector(
        HqStateAggregator aggregator,
        bool enabled = false,
        IEnumerable<string>? repositories = null,
        Func<string[], TimeSpan, CancellationToken, Task<CliResult>>? commandRunner = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        Enabled = enabled;
        if (repositories != null) Repositories.AddRange(repositories);
        _commandRunner = commandRunner ?? ((args, t, ct) => SafeProcessRunner.ExecuteAsync("gh", args, t, ct));
    }

    public async Task<IReadOnlyList<BuildInfo>> CollectOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled || Repositories.Count == 0)
            return Array.Empty<BuildInfo>();

        var allBuilds = new List<BuildInfo>();

        foreach (var repo in Repositories)
        {
            if (string.IsNullOrWhiteSpace(repo) || !SafeProcessRunner.IsValidGitHubRepo(repo)) continue;

            var args = new[]
            {
                "run", "list",
                "--json", "databaseId,status,conclusion,workflowName,headBranch,displayTitle,url,updatedAt",
                "--limit", "5",
                "-R", repo
            };

            var res = await _commandRunner(args, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                var builds = ParseRunList(res.StandardOutput, repo);
                allBuilds.AddRange(builds);
            }
        }

        if (allBuilds.Count > 0)
        {
            _aggregator.UpdateCiBuilds(allBuilds);
        }

        return allBuilds;
    }

    public static IReadOnlyList<BuildInfo> ParseRunList(string? json, string repo)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<BuildInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<BuildInfo>();

            var builds = new List<BuildInfo>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = el.TryGetProperty("databaseId", out var idProp) ? idProp.ToString() : Guid.NewGuid().ToString();
                var status = el.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "" : "";
                var conclusion = el.TryGetProperty("conclusion", out var concProp) ? concProp.GetString() : null;
                var workflowName = el.TryGetProperty("workflowName", out var wfProp) ? wfProp.GetString() ?? "CI" : "CI";
                var branch = el.TryGetProperty("headBranch", out var brProp) ? brProp.GetString() ?? "main" : "main";
                var title = el.TryGetProperty("displayTitle", out var tProp) ? tProp.GetString() ?? "" : "";
                var url = el.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;

                DateTimeOffset? updatedAt = null;
                if (el.TryGetProperty("updatedAt", out var upProp) && DateTimeOffset.TryParse(upProp.GetString(), out var dt))
                {
                    updatedAt = dt;
                }

                builds.Add(new BuildInfo(
                    Id: $"{repo}:{id}",
                    Repo: repo,
                    WorkflowName: workflowName,
                    Branch: branch,
                    Title: title,
                    Status: status,
                    Conclusion: conclusion,
                    Url: url,
                    UpdatedAt: updatedAt
                ));
            }

            return builds;
        }
        catch (JsonException)
        {
            return Array.Empty<BuildInfo>();
        }
    }

    private static async Task<CliResult> ExecuteGhProcessAsync(string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process == null) return new CliResult(-1, "", "Failed to start gh process");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var waitForExitTask = process.WaitForExitAsync(cts.Token);
            var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);

            if (completedTask != waitForExitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CliResult(-1, "", "gh command timed out", TimedOut: true);
            }

            await waitForExitTask.ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new CliResult(process.ExitCode, stdout, stderr);
        }
        catch (Win32Exception ex)
        {
            return new CliResult(-1, "", ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            return new CliResult(-1, "", "gh operation canceled");
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
