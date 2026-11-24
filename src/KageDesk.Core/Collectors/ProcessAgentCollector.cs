using System.ComponentModel;
using System.Diagnostics;
using KageDesk.Core.Models;
using KageDesk.Core.State;

namespace KageDesk.Core.Collectors;

public class ProcessAgentCollector
{
    private static readonly string[] TargetProcesses = { "omp", "antigravity", "claude", "codex", "opencode" };

    private readonly HqStateAggregator _aggregator;

    public bool Enabled { get; set; }

    public ProcessAgentCollector(HqStateAggregator aggregator, bool enabled = false)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        Enabled = enabled;
    }

    public IReadOnlyList<AgentSnapshot> DetectProcesses()
    {
        if (!Enabled)
            return Array.Empty<AgentSnapshot>();

        Process[]? processes = null;
        try
        {
            processes = Process.GetProcesses();
            var detected = new List<AgentSnapshot>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in processes)
            {
                try
                {
                    var name = p.ProcessName.ToLowerInvariant();
                    foreach (var target in TargetProcesses)
                    {
                        if (name.Contains(target, StringComparison.OrdinalIgnoreCase))
                        {
                            if (seen.Add(target))
                            {
                                var role = target switch
                                {
                                    "claude" => AgentRole.Coder,
                                    "codex" => AgentRole.Coder,
                                    "antigravity" => AgentRole.Leader,
                                    _ => AgentRole.External
                                };

                                var sprite = role switch
                                {
                                    AgentRole.Leader => "shikamaru",
                                    AgentRole.Coder => "minato",
                                    _ => "generic"
                                };

                                detected.Add(new AgentSnapshot(
                                    Id: $"proc:{target}",
                                    Name: $"Process: {target}",
                                    Role: role,
                                    SpriteKey: sprite,
                                    State: AgentState.Working,
                                    Source: "process"
                                ));
                            }
                            break;
                        }
                    }
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (Exception) { }
            }

            return detected;
        }
        catch (Exception)
        {
            return Array.Empty<AgentSnapshot>();
        }
        finally
        {
            if (processes != null)
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    public void CollectOnce()
    {
        if (!Enabled) return;
        var detected = DetectProcesses();
        _aggregator.UpdateProcessAgents(detected);
    }
}
