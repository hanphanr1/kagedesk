using System.ComponentModel;
using System.Diagnostics;
using KageDesk.Core.Models;
using KageDesk.Core.State;

namespace KageDesk.Core.Collectors;

public record CliResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false);

public class OrcaCliCollector
{
    private readonly HqStateAggregator _aggregator;
    private readonly Func<DateTimeOffset> _timeProvider;
    private readonly Func<string[], TimeSpan, CancellationToken, Task<CliResult>> _commandRunner;

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(4);
    public bool IsPaused { get; set; }

    public OrcaCliCollector(
        HqStateAggregator aggregator,
        Func<DateTimeOffset>? timeProvider = null,
        Func<string[], TimeSpan, CancellationToken, Task<CliResult>>? commandRunner = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow);
        _commandRunner = commandRunner ?? ((args, t, ct) => SafeProcessRunner.ExecuteAsync("orca", args, t, ct));
    }

    public async Task<bool> CollectOnceAsync(CancellationToken cancellationToken = default)
    {
        if (IsPaused)
            return false;

        var now = _timeProvider();

        // 1. orca bot list --json
        var botListTask = _commandRunner(new[] { "bot", "list", "--json" }, CommandTimeout, cancellationToken);

        // 2. orca worktree ps --since 48h --limit 25 --agent-wait --json
        var worktreeTask = _commandRunner(new[] { "worktree", "ps", "--since", "48h", "--limit", "25", "--agent-wait", "--json" }, CommandTimeout, cancellationToken);

        // 3. orca bot item list --status needs-you,in-progress,waiting --limit 20 --json
        var itemsTask = _commandRunner(new[] { "bot", "item", "list", "--status", "needs-you,in-progress,waiting", "--limit", "20", "--json" }, CommandTimeout, cancellationToken);

        // 4. orca account list
        var accountsTask = _commandRunner(new[] { "account", "list" }, CommandTimeout, cancellationToken);

        await Task.WhenAll(botListTask, worktreeTask, itemsTask, accountsTask).ConfigureAwait(false);

        var wtResult = await worktreeTask.ConfigureAwait(false);
        var itemsResult = await itemsTask.ConfigureAwait(false);
        var accResult = await accountsTask.ConfigureAwait(false);

        // Parse worktree ps
        var agents = wtResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(wtResult.StandardOutput)
            ? OrcaSnapshotParser.ParseWorktreePs(wtResult.StandardOutput, now)
            : Array.Empty<AgentSnapshot>();

        // Parse bot items
        var items = itemsResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(itemsResult.StandardOutput)
            ? OrcaSnapshotParser.ParseBotItems(itemsResult.StandardOutput)
            : Array.Empty<BotItemInfo>();

        // Merge items into agents
        var mergedAgents = OrcaSnapshotParser.MergeSnapshots(agents, items);

        // Parse accounts
        var accounts = accResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(accResult.StandardOutput)
            ? OrcaSnapshotParser.ParseAccounts(accResult.StandardOutput)
            : Array.Empty<AccountInfo>();

        _aggregator.UpdateOrcaSnapshot(mergedAgents, accounts);
        return true;
    }

    public static async Task<CliResult> ExecuteProcessAsync(string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "orca",
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
            if (process == null)
            {
                return new CliResult(-1, "", "Failed to start process");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var waitForExitTask = process.WaitForExitAsync(cts.Token);
            var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);

            if (completedTask != waitForExitTask)
            {
                // Timeout
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CliResult(-1, "", "Command timed out", TimedOut: true);
            }

            await waitForExitTask.ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new CliResult(process.ExitCode, stdout, stderr);
        }
        catch (Win32Exception ex)
        {
            // Orca CLI not found or access denied
            return new CliResult(-1, "", ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            return new CliResult(-1, "", "Operation canceled");
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
