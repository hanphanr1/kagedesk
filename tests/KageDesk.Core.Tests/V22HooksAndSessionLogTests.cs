using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KageDesk.Core.Bridge;
using KageDesk.Core.Collectors;
using KageDesk.Core.Hooks;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using Xunit;

namespace KageDesk.Core.Tests;

public class V22HooksAndSessionLogTests
{
    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void HookInstaller_InstallAndUninstall_IdempotentPreservesUnrelatedSettingsAndCreatesBackups()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-hooks-test-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");

        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        string codexDir = Path.Combine(fakeHome, ".codex");
        string codexConfig = Path.Combine(codexDir, "hooks.json");
        string openCodeDir = Path.Combine(fakeHome, ".config", "opencode");
        string openCodeConfig = Path.Combine(openCodeDir, "opencode.json");

        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(codexDir);
        Directory.CreateDirectory(openCodeDir);

        try
        {
            // Seed rich pre-existing user configurations with unrelated keys & existing hooks
            File.WriteAllText(claudeSettings, """
            {
              "theme": "dark-daltonized",
              "permissions": {
                "allow": ["Bash(git status)"]
              },
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "*",
                    "hooks": [
                      {
                        "type": "command",
                        "command": "echo keep-user-hook"
                      }
                    ]
                  }
                ]
              }
            }
            """);

            File.WriteAllText(codexConfig, """
            {
              "model": "o3",
              "approval_policy": "on-request"
            }
            """);

            File.WriteAllText(openCodeConfig, """
            {
              "provider": "anthropic",
              "keybinds": { "submit": "enter" }
            }
            """);

            var fixedTime = new DateTimeOffset(2026, 4, 10, 14, 30, 0, TimeSpan.Zero);
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319, clock: () => fixedTime);

            // 1. First install
            HookOperationSummary install1 = installer.InstallAll();
            Assert.True(install1.AllSucceeded);
            Assert.True(File.Exists(install1.EmitterScriptPath));

            var claudeRes = install1.Results.Single(r => r.Target == HookTargetKind.ClaudeCode);
            Assert.Equal(HookActionStatus.Installed, claudeRes.Status);
            Assert.NotNull(claudeRes.BackupPath);
            Assert.True(File.Exists(claudeRes.BackupPath));
            Assert.Contains(".kagedesk.bak", claudeRes.BackupPath!, StringComparison.OrdinalIgnoreCase);

            var codexRes = install1.Results.Single(r => r.Target == HookTargetKind.Codex);
            Assert.Equal(HookActionStatus.Installed, codexRes.Status);
            Assert.NotNull(codexRes.BackupPath);
            Assert.True(File.Exists(codexRes.BackupPath));

            var openCodeRes = install1.Results.Single(r => r.Target == HookTargetKind.OpenCode);
            Assert.Equal(HookActionStatus.Installed, openCodeRes.Status);
            Assert.NotNull(openCodeRes.BackupPath);
            Assert.True(File.Exists(openCodeRes.BackupPath));

            // Verify 100% of unrelated Claude settings & user hooks are preserved
            var claudeJson = JsonNode.Parse(File.ReadAllText(claudeSettings))!.AsObject();
            Assert.Equal("dark-daltonized", claudeJson["theme"]!.GetValue<string>());
            Assert.Equal("Bash(git status)", claudeJson["permissions"]!["allow"]![0]!.GetValue<string>());
            string claudeRaw = File.ReadAllText(claudeSettings);
            Assert.Contains("echo keep-user-hook", claudeRaw, StringComparison.Ordinal);
            Assert.Contains("kagedesk-emit.ps1", claudeRaw, StringComparison.OrdinalIgnoreCase);

            // Verify Codex unrelated JSON keys preserved + KageDesk hook present
            var codexJson = JsonNode.Parse(File.ReadAllText(codexConfig))!.AsObject();
            Assert.Equal("o3", codexJson["model"]!.GetValue<string>());
            Assert.Equal("on-request", codexJson["approval_policy"]!.GetValue<string>());
            Assert.Contains("kagedesk-emit.ps1", File.ReadAllText(codexConfig), StringComparison.OrdinalIgnoreCase);

            // Verify OpenCode unrelated JSON preserved + KageDesk hook present
            var openJson = JsonNode.Parse(File.ReadAllText(openCodeConfig))!.AsObject();
            Assert.Equal("anthropic", openJson["provider"]!.GetValue<string>());
            Assert.Equal("enter", openJson["keybinds"]!["submit"]!.GetValue<string>());
            Assert.Contains("kagedesk-emit.ps1", File.ReadAllText(openCodeConfig), StringComparison.OrdinalIgnoreCase);

            // 2. Second install must be strictly idempotent (zero duplicate entries)
            HookOperationSummary install2 = installer.InstallAll();
            Assert.True(install2.AllSucceeded);
            Assert.Equal(0, install2.ChangedCount);
            Assert.All(install2.Results, r => Assert.Equal(HookActionStatus.AlreadyInstalled, r.Status));

            // 3. Uninstall removes ONLY KageDesk entries and preserves 100% user settings
            HookOperationSummary uninstall1 = installer.UninstallAll();
            Assert.True(uninstall1.AllSucceeded);
            Assert.All(uninstall1.Results, r => Assert.Equal(HookActionStatus.Uninstalled, r.Status));

            string claudeAfter = File.ReadAllText(claudeSettings);
            Assert.DoesNotContain("kagedesk-emit.ps1", claudeAfter, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dark-daltonized", claudeAfter, StringComparison.Ordinal);
            Assert.Contains("echo keep-user-hook", claudeAfter, StringComparison.Ordinal);

            string codexAfter = File.ReadAllText(codexConfig);
            Assert.DoesNotContain("kagedesk-emit.ps1", codexAfter, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"model\": \"o3\"", codexAfter, StringComparison.Ordinal);
            Assert.Contains("\"approval_policy\": \"on-request\"", codexAfter, StringComparison.Ordinal);

            string openAfter = File.ReadAllText(openCodeConfig);
            Assert.DoesNotContain("kagedesk-emit.ps1", openAfter, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("anthropic", openAfter, StringComparison.Ordinal);
            Assert.Contains("enter", openAfter, StringComparison.Ordinal);

            // 4. Second uninstall is strictly idempotent
            HookOperationSummary uninstall2 = installer.UninstallAll();
            Assert.True(uninstall2.AllSucceeded);
            Assert.Equal(0, uninstall2.ChangedCount);
            Assert.All(uninstall2.Results, r => Assert.Equal(HookActionStatus.AlreadyRemoved, r.Status));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_MissingOptionalDirectories_SkipsGracefully()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-hooks-empty-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        Directory.CreateDirectory(fakeHome);

        try
        {
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);
            var summary = installer.InstallAll();

            Assert.True(summary.AllSucceeded);
            Assert.Equal(HookActionStatus.Installed, summary.Results.Single(r => r.Target == HookTargetKind.ClaudeCode).Status);
            Assert.Equal(HookActionStatus.SkippedNotPresent, summary.Results.Single(r => r.Target == HookTargetKind.Codex).Status);
            Assert.Equal(HookActionStatus.SkippedNotPresent, summary.Results.Single(r => r.Target == HookTargetKind.OpenCode).Status);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_LargeFileOver100KB_ReadsBoundedTailAndNeverRetainsSecrets()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-sessionlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string logPath = Path.Combine(tempDir, "session-001.jsonl");
            var sb = new StringBuilder(140_000);
            for (int i = 0; i < 600; i++)
            {
                sb.AppendLine($"{{\"type\":\"user\",\"content\":\"SECRET_API_KEY_998877 C:\\\\Users\\\\private\\\\id_rsa prompt #{i} padding padding padding padding padding padding\"}}");
            }
            sb.AppendLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\",\"input\":{\"file_path\":\"C:\\\\Users\\\\private\\\\secret.env\",\"content\":\"TOP_SECRET_PASSWORD\"}}]}}");
            File.WriteAllText(logPath, sb.ToString());

            var now = new DateTimeOffset(2026, 4, 10, 15, 0, 0, TimeSpan.Zero);
            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => now);

            byte[]? tailChunk = collector.ReadBoundedTailDelta(logPath);
            Assert.NotNull(tailChunk);
            Assert.True(tailChunk!.Length <= SessionLogActivityCollector.MaxTailReadBytes,
                $"Expected bounded tail <= {SessionLogActivityCollector.MaxTailReadBytes} bytes, got {tailChunk.Length}.");

            // Reset with fresh collector to exercise ProcessFileDelta end-to-end
            using var collector2 = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => now);

            IReadOnlyList<AgentSnapshot>? emitted = null;
            collector2.ActivityChanged += (_, list) => emitted = list;

            var derived = collector2.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(derived);
            Assert.Equal(AgentState.Working, derived!.State);
            Assert.Equal("CODE", derived.DerivedActionToken, ignoreCase: true);

            Assert.NotNull(emitted);
            Assert.Single(emitted!);
            AgentSnapshot snap = emitted![0];
            Assert.Equal(AgentRole.External, snap.Role);
            Assert.Equal(AgentState.Working, snap.State);
            Assert.Equal("CODE", snap.ToolName, ignoreCase: true);
            Assert.Equal("session-log", snap.Source);

            // Privacy gate: ZERO secrets, prompt text, or file paths retained
            string combined = $"{snap.Id}|{snap.Name}|{snap.TaskTitle}|{snap.ToolName}|{snap.Source}|{snap.Prompt}|{snap.LastMessage}";
            Assert.DoesNotContain("SECRET_API_KEY", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("id_rsa", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret.env", combined, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TOP_SECRET_PASSWORD", combined, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_MissingFolders_StartsAndDisposesSafely()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"kagedesk-missing-{Guid.NewGuid():N}");
        using var collector = new SessionLogActivityCollector(
            watchRoots: new[] { new SessionWatchRoot("codex", "Codex", missingPath) });

        collector.Start();
        Assert.Equal(0, collector.ActiveWatcherCount);
        Assert.Empty(collector.GetCurrentSnapshots());
        collector.Stop();
    }

    [Fact]
    public void Aggregator_SessionLogOverridesProcessHeuristicWhilePreservingProcessFocusMetadata()
    {
        var now = new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);
        var agg = new HqStateAggregator(timeProvider: () => now);

        var procAgent = new AgentSnapshot(
            Id: "proc-claude-4242",
            Name: "Claude",
            Role: AgentRole.External,
            SpriteKey: "claude",
            State: AgentState.Idle,
            Source: "process",
            ProcessId: 4242,
            ProcessStartTicks: 123456789L);

        agg.UpdateProcessAgents(new[] { procAgent });
        Assert.Equal(AgentState.Idle, agg.CurrentState.Agents.Single().State);

        var logAgent = new AgentSnapshot(
            Id: "sessionlog-claude",
            Name: "Claude",
            Role: AgentRole.External,
            SpriteKey: "claude",
            State: AgentState.Working,
            ToolName: "TEST",
            Source: "session-log");

        agg.UpdateSessionLogActivity(new[] { logAgent });

        var merged = Assert.Single(agg.CurrentState.Agents);
        Assert.Equal(AgentState.Working, merged.State);
        Assert.Equal("TEST", merged.ToolName);
        Assert.Equal("process", merged.Source);
        Assert.Equal(4242, merged.ProcessId);
        Assert.Equal(123456789L, merged.ProcessStartTicks);

        // Clearing session log activity gracefully falls back to ProcessAgentCollector heuristic
        agg.ClearSessionLogActivity();
        var fallback = Assert.Single(agg.CurrentState.Agents);
        Assert.Equal(AgentState.Idle, fallback.State);
        Assert.Equal("process", fallback.Source);
    }

    [Theory]
    [InlineData(0, 0, EffectiveWeather.MidnightRain)]
    [InlineData(4, 59, EffectiveWeather.MidnightRain)]
    [InlineData(5, 0, EffectiveWeather.Dawn)]
    [InlineData(8, 59, EffectiveWeather.Dawn)]
    [InlineData(9, 0, EffectiveWeather.Day)]
    [InlineData(16, 59, EffectiveWeather.Day)]
    [InlineData(17, 0, EffectiveWeather.Sunset)]
    [InlineData(19, 59, EffectiveWeather.Sunset)]
    [InlineData(20, 0, EffectiveWeather.MidnightRain)]
    [InlineData(23, 59, EffectiveWeather.MidnightRain)]
    public void ComputeEffectiveWeather_AutoRealTimeDayNightBoundaries_MatchesSchedule(int localHour, int localMinute, EffectiveWeather expected)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 4, 10, localHour, localMinute, 0));
        var localTime = new DateTimeOffset(2026, 4, 10, localHour, localMinute, 0, offset);
        var agg = new HqStateAggregator(new AppConfig { WeatherMode = WeatherMode.Auto }, timeProvider: () => localTime);

        Assert.Equal(expected, agg.CurrentState.Weather);
    }

    [Fact]
    public async Task LocalEventBridge_ReviewChangesAndMixiPoke_EmitVisualEventsCleanly()
    {
        int port = GetAvailablePort();
        var agg = new HqStateAggregator();
        agg.SetDemoScenario("working");
        await using var bridge = new LocalEventBridge(agg, port);
        bridge.Start();

        using var client = new HttpClient();

        // 1. Trigger review_changes drama via HTTP bridge (resolves minato -> concrete demo-coder ID)
        var dramaResp = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/event",
            new StringContent("{\"type\":\"review_changes\",\"fromAgentId\":\"kakashi\",\"toAgentId\":\"minato\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, dramaResp.StatusCode);
        Assert.Contains(agg.CurrentState.PendingVisualEvents,
            e => e.Type == "review_changes" && e.AgentId == "kakashi" && e.TargetAgentId == "demo-coder" && e.ToIsConcreteId);

        // 2. Trigger mixi_poke via HTTP bridge (must NOT create phantom agents)
        agg.SetDemoScenario("clear");
        var pokeResp = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/event",
            new StringContent("{\"type\":\"mixi_poke\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, pokeResp.StatusCode);
        Assert.Empty(agg.CurrentState.Agents);
        Assert.Contains(agg.CurrentState.PendingVisualEvents, e => e.Type == "mixi_poke");

        // 3. Demo endpoints for drama & celebrate
        var demoDrama = await client.PostAsync($"http://127.0.0.1:{port}/api/demo/drama", null);
        Assert.Equal(HttpStatusCode.OK, demoDrama.StatusCode);
        Assert.Contains(agg.CurrentState.PendingVisualEvents, e => e.Type == "review_changes");

        var demoCelebrate = await client.PostAsync($"http://127.0.0.1:{port}/api/demo/celebrate", null);
        Assert.Equal(HttpStatusCode.OK, demoCelebrate.StatusCode);
        Assert.Contains(agg.CurrentState.PendingVisualEvents, e => e.Type == "celebrate");
    }
}
