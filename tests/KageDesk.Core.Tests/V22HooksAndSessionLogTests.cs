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
        string openCodePlugin = Path.Combine(openCodeDir, "plugins", "kagedesk-plugin.js");

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

            string originalOpenCodeConfig = """
            {
              // user comment in opencode.jsonc-style file must stay 100% untouched!
              "provider": "anthropic",
              "keybinds": { "submit": "enter" }
            }
            """;
            File.WriteAllText(openCodeConfig, originalOpenCodeConfig);
            byte[] originalOpenCodeBytes = File.ReadAllBytes(openCodeConfig);

            var fixedTime = new DateTimeOffset(2026, 4, 10, 14, 30, 0, TimeSpan.Zero);
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319, clock: () => fixedTime, codexHomeDir: codexDir, openCodeVersionProbe: () => "opencode 1.18.29");

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
            Assert.Contains("trust", codexRes.Message, StringComparison.OrdinalIgnoreCase);

            var openCodeRes = install1.Results.Single(r => r.Target == HookTargetKind.OpenCode);
            Assert.Equal(HookActionStatus.Installed, openCodeRes.Status);
            Assert.True(File.Exists(openCodePlugin));
            Assert.Contains("// kagedesk-managed-opencode-plugin:v1", File.ReadAllText(openCodePlugin), StringComparison.Ordinal);

            // OpenCode user JSON/JSONC MUST remain 100% untouched byte-for-byte!
            Assert.Equal(originalOpenCodeBytes, File.ReadAllBytes(openCodeConfig));

            // Verify Claude schema: PreToolUse (tool_start), PostToolUse (tool_complete), Notification (approval-scoped), Stop (turn_done), all async:true
            var claudeJson = JsonNode.Parse(File.ReadAllText(claudeSettings))!.AsObject();
            Assert.Equal("dark-daltonized", claudeJson["theme"]!.GetValue<string>());
            Assert.Equal("Bash(git status)", claudeJson["permissions"]!["allow"]![0]!.GetValue<string>());
            string claudeRaw = File.ReadAllText(claudeSettings);
            Assert.Contains("echo keep-user-hook", claudeRaw, StringComparison.Ordinal);
            Assert.Contains("-Type tool_start", claudeRaw, StringComparison.Ordinal);
            Assert.Contains("-Type tool_complete", claudeRaw, StringComparison.Ordinal);
            Assert.Contains("-Type turn_done", claudeRaw, StringComparison.Ordinal);
            Assert.Contains("permission_prompt|elicitation_dialog", claudeRaw, StringComparison.Ordinal);
            Assert.Contains("\"async\": true", claudeRaw, StringComparison.Ordinal);

            // Verify Codex schema: unrelated keys preserved, supported events ONLY (NO Notification!)
            var codexJson = JsonNode.Parse(File.ReadAllText(codexConfig))!.AsObject();
            Assert.Equal("o3", codexJson["model"]!.GetValue<string>());
            Assert.Equal("on-request", codexJson["approval_policy"]!.GetValue<string>());
            var codexHooksObj = codexJson["hooks"]!.AsObject();
            Assert.True(codexHooksObj.ContainsKey("PreToolUse"));
            Assert.True(codexHooksObj.ContainsKey("PostToolUse"));
            Assert.True(codexHooksObj.ContainsKey("Stop"));
            Assert.False(codexHooksObj.ContainsKey("Notification"));

            // 2. Second install must be strictly idempotent (zero duplicate entries, byte-preserving no-op)
            byte[] claudeBytesAfterInstall = File.ReadAllBytes(claudeSettings);
            byte[] codexBytesAfterInstall = File.ReadAllBytes(codexConfig);
            byte[] openPluginBytesAfterInstall = File.ReadAllBytes(openCodePlugin);

            HookOperationSummary install2 = installer.InstallAll();
            Assert.True(install2.AllSucceeded);
            Assert.Equal(0, install2.ChangedCount);
            Assert.All(install2.Results, r => Assert.Equal(HookActionStatus.AlreadyInstalled, r.Status));
            Assert.Equal(claudeBytesAfterInstall, File.ReadAllBytes(claudeSettings));
            Assert.Equal(codexBytesAfterInstall, File.ReadAllBytes(codexConfig));
            Assert.Equal(openPluginBytesAfterInstall, File.ReadAllBytes(openCodePlugin));
            Assert.Equal(originalOpenCodeBytes, File.ReadAllBytes(openCodeConfig));

            // 3. Uninstall removes ONLY KageDesk entries & standalone OpenCode plugin while preserving 100% user settings
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

            Assert.False(File.Exists(openCodePlugin));
            Assert.Equal(originalOpenCodeBytes, File.ReadAllBytes(openCodeConfig));

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
    public void HookInstaller_BytePreservation_MalformedJson_Comments_NonArray_AndExplicitNull()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-hooks-safety-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);

            // Case 1: Malformed JSON -> MalformedConfig, bytes untouched
            byte[] malformedBytes = Encoding.UTF8.GetBytes("{ \"theme\": \"dark\", broken");
            File.WriteAllBytes(claudeSettings, malformedBytes);
            var r1 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.MalformedConfig, r1.Status);
            Assert.Equal(malformedBytes, File.ReadAllBytes(claudeSettings));

            // Case 2: JSON with comments (JSONC) -> UnsupportedLayout, bytes untouched
            byte[] commentBytes = Encoding.UTF8.GetBytes("{\n  // keep my comment\n  \"theme\": \"dark\"\n}\n");
            File.WriteAllBytes(claudeSettings, commentBytes);
            var r2 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.UnsupportedLayout, r2.Status);
            Assert.Equal(commentBytes, File.ReadAllBytes(claudeSettings));

            // Case 3: Non-array managed event -> preflight rejects entire file without partial writes!
            byte[] nonArrayBytes = Encoding.UTF8.GetBytes("""{"hooks":{"PreToolUse":[],"Stop":{"bad":true}}}""");
            File.WriteAllBytes(claudeSettings, nonArrayBytes);
            var r3 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.UnsupportedLayout, r3.Status);
            Assert.Equal(nonArrayBytes, File.ReadAllBytes(claudeSettings));

            // Case 4: Explicit null vs absent property ("hooks": null and "PreToolUse": null)
            byte[] nullHooksBytes = Encoding.UTF8.GetBytes("""{"theme":"dark","hooks":null}""");
            File.WriteAllBytes(claudeSettings, nullHooksBytes);
            var r4 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.UnsupportedLayout, r4.Status);
            Assert.Equal(nullHooksBytes, File.ReadAllBytes(claudeSettings));

            byte[] nullEventBytes = Encoding.UTF8.GetBytes("""{"hooks":{"PreToolUse":null}}""");
            File.WriteAllBytes(claudeSettings, nullEventBytes);
            var r5 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.UnsupportedLayout, r5.Status);
            Assert.Equal(nullEventBytes, File.ReadAllBytes(claudeSettings));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_ExactOwnership_PreservesThirdPartyWrapperAndPreExistingEmptyGroups()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-hooks-own-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            // Seed a third-party wrapper mentioning kagedesk-emit.ps1, a pre-existing empty group, and an empty unknown event array
            File.WriteAllText(claudeSettings, """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "*",
                    "hooks": [
                      {
                        "type": "command",
                        "command": "pwsh -Command \"Write-Host custom; & 'C:/custom/kagedesk-emit.ps1' -Type tool_start\""
                      }
                    ]
                  },
                  {
                    "matcher": "Read",
                    "hooks": []
                  }
                ],
                "UnknownCustomEvent": []
              }
            }
            """);

            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);
            var installRes = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, installRes.Status);

            var uninstallRes = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninstallRes.Status);

            var afterRoot = JsonNode.Parse(File.ReadAllText(claudeSettings))!.AsObject();
            var hooksObj = afterRoot["hooks"]!.AsObject();

            // Unknown empty event array preserved!
            Assert.True(hooksObj.ContainsKey("UnknownCustomEvent"));
            Assert.Empty(hooksObj["UnknownCustomEvent"]!.AsArray());

            // Third-party wrapper AND pre-existing empty group preserved!
            var preToolArr = hooksObj["PreToolUse"]!.AsArray();
            Assert.Equal(2, preToolArr.Count);
            string preToolJson = preToolArr.ToJsonString();
            Assert.Contains("Write-Host custom", preToolJson, StringComparison.Ordinal);
            Assert.Contains("\"matcher\":\"Read\"", preToolJson, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_OpenCodeUnownedPlugin_RefusesOverwriteAndDeleteWithoutModifyingBytes()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-oc-unowned-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string pluginDir = Path.Combine(fakeHome, ".config", "opencode", "plugins");
        string pluginPath = Path.Combine(pluginDir, "kagedesk-plugin.js");
        Directory.CreateDirectory(pluginDir);

        try
        {
            byte[] customPluginBytes = Encoding.UTF8.GetBytes("// user custom plugin\nexport default () => ({});\n");
            File.WriteAllBytes(pluginPath, customPluginBytes);

            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319, openCodeVersionProbe: () => "opencode 1.18.29");
            var installRes = installer.InstallOpenCode();
            Assert.Equal(HookActionStatus.UnsupportedLayout, installRes.Status);
            Assert.Equal(customPluginBytes, File.ReadAllBytes(pluginPath));

            var uninstallRes = installer.UninstallOpenCode();
            Assert.Equal(HookActionStatus.UnsupportedLayout, uninstallRes.Status);
            Assert.Equal(customPluginBytes, File.ReadAllBytes(pluginPath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_ConcurrentModification_SnapshotRevalidationAbortsWithoutClobbering()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-hooks-race-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            File.WriteAllText(claudeSettings, """{"theme":"before"}""");
            string concurrentEdit = """{"theme":"concurrent-user-edit"}""";

            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319)
            {
                BeforeCommitHookForTest = path => File.WriteAllText(path, concurrentEdit)
            };

            var res = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Failed, res.Status);
            Assert.Contains("modified concurrently", res.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(concurrentEdit, File.ReadAllText(claudeSettings));
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
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319, codexHomeDir: Path.Combine(fakeHome, ".codex"));
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
    public void Aggregator_ClaudeHookEventMapping_PostToolUseStaysWorking_StopTriggersDone()
    {
        var agg = new HqStateAggregator();
        agg.UpdatePushedEvent(new PushedEventPayload(Type: "tool_start", AgentId: "claude", Name: "Claude", ToolName: "Edit"));
        Assert.Equal(AgentState.Working, agg.CurrentState.Agents.Single(a => a.Id == "claude").State);

        // PostToolUse emits tool_complete -> stays Working (NOT premature Done celebration!)
        agg.UpdatePushedEvent(new PushedEventPayload(Type: "tool_complete", AgentId: "claude", Name: "Claude", ToolName: "Edit"));
        Assert.Equal(AgentState.Working, agg.CurrentState.Agents.Single(a => a.Id == "claude").State);

        // Stop emits turn_done -> actual turn-end Done!
        agg.UpdatePushedEvent(new PushedEventPayload(Type: "turn_done", AgentId: "claude", Name: "Claude"));
        Assert.Equal(AgentState.Done, agg.CurrentState.Agents.Single(a => a.Id == "claude").State);
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
    public void SessionLogActivityCollector_BoundaryAppends_NeverDropsFirstLine_SingleAndMultiLine()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-sessionlog-append-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string logPath = Path.Combine(tempDir, "session.jsonl");
            var now = new DateTimeOffset(2026, 4, 10, 15, 0, 0, TimeSpan.Zero);
            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => now);

            // Initial complete line
            File.WriteAllText(logPath, "{\"type\":\"user\",\"content\":\"hello\"}\n");
            var first = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(first);
            Assert.Equal(AgentState.Working, first!.State);

            // Single-line append from exact record boundary (MUST NOT drop line!)
            File.AppendAllText(logPath, "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"AskUserQuestion\"}]}}\n");
            var second = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(second);
            Assert.Equal(AgentState.NeedsApproval, second!.State);

            // Multi-line batch where the FIRST line has the meaningful tool_use and the second line is non-semantic noise
            // If first line were wrongly skipped, state would NOT update to TEST!
            File.AppendAllText(logPath,
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"pytest\"}]}}\n" +
                "{\"type\":\"heartbeat\",\"ok\":true}\n");
            var third = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(third);
            Assert.Equal(AgentState.Working, third!.State);
            Assert.Equal("TEST", third.DerivedActionToken, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_SplitMultiByteUtf8AndPartialLine_ReconstructsCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-sessionlog-utf8-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string logPath = Path.Combine(tempDir, "utf8.jsonl");
            var now = new DateTimeOffset(2026, 4, 10, 15, 0, 0, TimeSpan.Zero);
            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => now);

            // Full UTF-8 record containing 3-byte Vietnamese characters ("Độ Mixi")
            string line1 = "{\"type\":\"user\",\"content\":\"init\"}\n";
            string line2 = "{\"type\":\"assistant\",\"note\":\"Độ Mixi\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n";
            byte[] line1Bytes = Encoding.UTF8.GetBytes(line1);
            byte[] line2Bytes = Encoding.UTF8.GetBytes(line2);

            // Find the middle of the multi-byte UTF-8 sequence for 'Đ' (0xC4 0x90) or 'ộ' (0xE1 0xBB 0x99)
            int splitIdx = Array.IndexOf(line2Bytes, (byte)0xE1) + 1;
            Assert.True(splitIdx > 1 && splitIdx < line2Bytes.Length - 2);

            // Write line 1 + first half of line 2 (mid-UTF-8 char, no trailing \n)
            using (var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Write(line1Bytes);
                fs.Write(line2Bytes, 0, splitIdx);
            }

            var step1 = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(step1);
            Assert.Equal("THINK", step1!.DerivedActionToken, ignoreCase: true); // Only line 1 parsed; partial UTF-8 line NOT committed

            // Append second half of line 2 completing the multi-byte UTF-8 character and \n
            using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Write(line2Bytes, splitIdx, line2Bytes.Length - splitIdx);
            }

            var step2 = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(step2);
            Assert.Equal(AgentState.Working, step2!.State);
            Assert.Equal("CODE", step2.DerivedActionToken, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_FileRotation_ExactSizeAndLargerReplacement_DetectedCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-sessionlog-rotate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string logPath = Path.Combine(tempDir, "rotated.jsonl");
            var now = new DateTimeOffset(2026, 4, 10, 15, 0, 0, TimeSpan.Zero);
            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => now);

            // Initial file
            string contentA = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\"}]}}\n";
            File.WriteAllText(logPath, contentA);
            var obsA = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.Equal("READ", obsA!.DerivedActionToken, ignoreCase: true);

            // Exact-size replacement with DIFFERENT content (same byte count!)
            string contentB = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n";
            Assert.Equal(contentA.Length, contentB.Length);
            File.Delete(logPath);
            File.WriteAllText(logPath, contentB);

            var obsB = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(obsB);
            Assert.Equal("CODE", obsB!.DerivedActionToken, ignoreCase: true);

            // Larger replacement with different header
            string contentC = "{\"event\":\"new_session\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"pytest\"}]}}\n";
            Assert.True(contentC.Length > contentB.Length);
            File.Delete(logPath);
            File.WriteAllText(logPath, contentC);

            var obsC = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(obsC);
            Assert.Equal("TEST", obsC!.DerivedActionToken, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_ContinuousRapidWrites_NoStarvationAndOverflowDeletedRenamedReconcile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-sessionlog-rapid-{Guid.NewGuid():N}");
        string delayedRoot = Path.Combine(tempDir, "delayed-codex");
        Directory.CreateDirectory(tempDir);

        try
        {
            string logPath = Path.Combine(tempDir, "rapid.jsonl");
            using var processedEvent = new ManualResetEventSlim(false);
            int publishCount = 0;

            using var collector = new SessionLogActivityCollector(
                watchRoots: new[]
                {
                    new SessionWatchRoot("claude", "Claude", tempDir),
                    new SessionWatchRoot("codex", "Codex", delayedRoot)
                },
                debounceWindow: TimeSpan.FromMilliseconds(120),
                maxCoalescingDelay: TimeSpan.FromMilliseconds(220));

            collector.ActivityChanged += (_, _) =>
            {
                Interlocked.Increment(ref publishCount);
                processedEvent.Set();
            };

            collector.Start();
            Assert.Equal(1, collector.ActiveWatcherCount);

            // Continuous rapid writes faster than debounce (every 35ms for ~450ms) MUST NOT starve under maxCoalescingDelay!
            for (int i = 0; i < 12; i++)
            {
                File.AppendAllText(logPath, $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"tool_use\",\"name\":\"Edit\"}}]}}}}\n");
                Thread.Sleep(35);
            }

            Assert.True(processedEvent.Wait(TimeSpan.FromSeconds(3)), "Continuous rapid writes must not starve consumer.");
            Assert.True(Volatile.Read(ref publishCount) >= 1);

            // Overflow reconciliation picks up changes even if FSW events were dropped
            string overflowFile = Path.Combine(tempDir, "overflow.jsonl");
            File.WriteAllText(overflowFile, "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"pytest\"}]}}\n");
            collector.SimulateWatcherOverflow();
            Assert.True(SpinWait.SpinUntil(
                () => collector.GetCurrentSnapshots().Any(s => s.Id.StartsWith("session:claude:", StringComparison.OrdinalIgnoreCase) && string.Equals(s.ToolName, "test", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(3)), "Overflow reconciliation should pick up overflow.jsonl.");

            // Missing root created after startup attaches via bounded reconciliation
            Directory.CreateDirectory(delayedRoot);
            collector.ReconcileMissingRootsNow();
            Assert.Equal(2, collector.ActiveWatcherCount);

            // Deletion invalidates state cleanly
            File.Delete(logPath);
            File.Delete(overflowFile);
            Assert.True(SpinWait.SpinUntil(
                () => !collector.GetCurrentSnapshots().Any(s => s.Id.StartsWith("session:claude:", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(3)), "Deleting all tracked log files should clear session:claude snapshots.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SessionLogActivityCollector_DisableWhileCallbackBlocked_StalePublishCannotReinstateState()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-sessionlog-gen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var agg = new HqStateAggregator();
            string logPath = Path.Combine(tempDir, "blocked.jsonl");
            File.WriteAllText(logPath, "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n");

            using var enteredCallback = new ManualResetEventSlim(false);
            using var releaseCallback = new ManualResetEventSlim(false);

            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                debounceWindow: TimeSpan.FromMilliseconds(30),
                maxCoalescingDelay: TimeSpan.FromMilliseconds(60));

            long activeGen = agg.GetSessionLogGeneration();
            collector.ActivityChanged += (_, snapshots) =>
            {
                enteredCallback.Set();
                releaseCallback.Wait(TimeSpan.FromSeconds(5));
                agg.UpdateSessionLogActivity(snapshots, expectedGeneration: activeGen);
            };

            collector.Start();
            // Trigger file change
            File.AppendAllText(logPath, "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n");

            Assert.True(enteredCallback.Wait(TimeSpan.FromSeconds(3)));

            // User disables watcher WHILE callback is blocked in-flight
            agg.DisableAndClearSessionLogActivity();
            Task stopTask = Task.Run(() => collector.Stop());

            // Release the blocked stale callback
            releaseCallback.Set();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(3));

            // Stale publish MUST NOT have reinstated session-log state!
            Assert.Empty(agg.CurrentState.Agents);
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
    public void Aggregator_VerifiedSessionLogEnrichesProcessWhileUnverifiedEmitsSeparateTile()
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

        // Unverified log (no verified PID+StartTicks or workspace) MUST NOT mutate the lone live process;
        // instead it emits one separate provider activity tile!
        var unverifiedLog = new AgentSnapshot(
            Id: "session:claude:unverified01",
            Name: "Claude",
            Role: AgentRole.External,
            SpriteKey: "claude",
            State: AgentState.Working,
            ToolName: "CODE",
            Source: "session-log",
            UpdatedAt: now);

        agg.UpdateSessionLogActivity(new[] { unverifiedLog });
        Assert.Equal(2, agg.CurrentState.Agents.Count);
        Assert.Equal(AgentState.Idle, agg.CurrentState.Agents.Single(a => a.Id == "proc-claude-4242").State);
        Assert.Single(agg.CurrentState.Agents, a => a.Source == "session-log" && a.State == AgentState.Working);

        // Verified (Provider + Pid + ProcessStartTicks) enriches the exact process while preserving metadata!
        var verifiedLog = new AgentSnapshot(
            Id: "session:claude:verified0001",
            Name: "Claude",
            Role: AgentRole.External,
            SpriteKey: "claude",
            State: AgentState.Working,
            ToolName: "TEST",
            Source: "session-log",
            ProcessId: 4242,
            ProcessStartTicks: 123456789L,
            UpdatedAt: now);

        agg.UpdateSessionLogActivity(new[] { verifiedLog });

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

    [Fact]
    public void Aggregator_TwoConcurrentSameProviderProcesses_DoesNotClobberUnrelatedProcess()
    {
        var now = new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);
        var agg = new HqStateAggregator(timeProvider: () => now);

        var proc1 = new AgentSnapshot("proc-claude-1001", "Claude #1", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 1001, ProcessStartTicks: 111L);
        var proc2 = new AgentSnapshot("proc-claude-2002", "Claude #2", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 2002, ProcessStartTicks: 222L);
        agg.UpdateProcessAgents(new[] { proc1, proc2 });

        // 1. Uncorrelated session-log observation (no verified PID+StartTicks/Repo) MUST NOT clobber proc1 or proc2; displays separate tile instead
        var uncorrelatedLog = new AgentSnapshot("session:claude:uncorr000001", "Claude", AgentRole.External, "claude", AgentState.Working, ToolName: "CODE", Source: "session-log", UpdatedAt: now);
        agg.UpdateSessionLogActivity(new[] { uncorrelatedLog });

        var agents1 = agg.CurrentState.Agents;
        Assert.Equal(AgentState.Idle, agents1.Single(a => a.Id == "proc-claude-1001").State);
        Assert.Equal(AgentState.Idle, agents1.Single(a => a.Id == "proc-claude-2002").State);
        Assert.Contains(agents1, a => a.Source == "session-log" && a.State == AgentState.Working);

        // 2. Bare PID without ProcessStartTicks OR mismatched/reused ProcessStartTicks MUST NOT correlate!
        var barePidLog = new AgentSnapshot("session:claude:barepid00001", "Claude", AgentRole.External, "claude", AgentState.Working, ToolName: "TEST", Source: "session-log", ProcessId: 1001, ProcessStartTicks: null, UpdatedAt: now);
        agg.UpdateSessionLogActivity(new[] { barePidLog });
        Assert.Equal(AgentState.Idle, agg.CurrentState.Agents.Single(a => a.Id == "proc-claude-1001").State);

        var reusedPidLog = new AgentSnapshot("session:claude:reused000001", "Claude", AgentRole.External, "claude", AgentState.Working, ToolName: "TEST", Source: "session-log", ProcessId: 1001, ProcessStartTicks: 999L, UpdatedAt: now);
        agg.UpdateSessionLogActivity(new[] { reusedPidLog });
        Assert.Equal(AgentState.Idle, agg.CurrentState.Agents.Single(a => a.Id == "proc-claude-1001").State);

        // 3. Verified (Provider + PID + ProcessStartTicks) observation updates ONLY proc-claude-1001, leaving proc-claude-2002 Idle!
        var correlatedLog = new AgentSnapshot("session:claude:corr00000001", "Claude", AgentRole.External, "claude", AgentState.Working, ToolName: "TEST", Source: "session-log", ProcessId: 1001, ProcessStartTicks: 111L, UpdatedAt: now);
        agg.UpdateSessionLogActivity(new[] { correlatedLog });

        var agents2 = agg.CurrentState.Agents;
        Assert.Equal(2, agents2.Count);
        Assert.Equal(AgentState.Working, agents2.Single(a => a.Id == "proc-claude-1001").State);
        Assert.Equal("TEST", agents2.Single(a => a.Id == "proc-claude-1001").ToolName);
        Assert.Equal(AgentState.Idle, agents2.Single(a => a.Id == "proc-claude-2002").State);
    }

    [Fact]
    public void Aggregator_SessionLogFreshnessExpiry_PromptFallbackAt20SecondsWithoutTtlRenewal()
    {
        var clock = new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);
        var agg = new HqStateAggregator(timeProvider: () => clock);

        var procAgent = new AgentSnapshot("proc-claude-4242", "Claude", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 4242, ProcessStartTicks: 123L);
        agg.UpdateProcessAgents(new[] { procAgent });

        var obsTime = clock;
        var logAgent = new AgentSnapshot("session:claude:fresh0000001", "Claude", AgentRole.External, "claude", AgentState.Working, ToolName: "CODE", Source: "session-log", ProcessId: 4242, ProcessStartTicks: 123L, UpdatedAt: obsTime);
        agg.UpdateSessionLogActivity(new[] { logAgent }, ttlSeconds: 20.0);
        Assert.Equal(AgentState.Working, agg.CurrentState.Agents.Single().State);

        // Advance to t=+15s and republish the SAME unchanged observation -> MUST NOT renew TTL past t=+20s!
        clock = obsTime.AddSeconds(15);
        agg.UpdateSessionLogActivity(new[] { logAgent }, ttlSeconds: 20.0);
        Assert.Equal(AgentState.Working, agg.CurrentState.Agents.Single().State);

        // Advance to t=+21s -> MUST promptly expire and fall back to ProcessAgentCollector (Idle), NOT stay masked for 90s!
        clock = obsTime.AddSeconds(21);
        agg.Reevaluate();
        Assert.Equal(AgentState.Idle, agg.CurrentState.Agents.Single().State);
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
    public async Task LocalEventBridge_ReviewChangesMixiPokeAndStrictDtoValidation()
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

        // 2. Reject unknown event type -> 400 Bad Request
        var unknownTypeResp = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/event",
            new StringContent("{\"type\":\"totally_unknown_event\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownTypeResp.StatusCode);

        // 3. Reject arbitrary unresolved alias on review_changes -> 400 Bad Request
        var badAliasResp = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/event",
            new StringContent("{\"type\":\"review_changes\",\"fromAgentId\":\"kakashi\",\"toAgentId\":\"nonexistent_ninja\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badAliasResp.StatusCode);

        // 4. Reject non-positive TTL -> 400 Bad Request
        var badTtlResp = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/event",
            new StringContent("{\"type\":\"celebrate\",\"ttlSeconds\":-5}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badTtlResp.StatusCode);

        // 5. Trigger mixi_poke via HTTP bridge (must NOT create phantom agents)
        agg.SetDemoScenario("clear");
        var pokeResp = await client.PostAsync(
            $"http://127.0.0.1:{port}/api/event",
            new StringContent("{\"type\":\"mixi_poke\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, pokeResp.StatusCode);
        Assert.Empty(agg.CurrentState.Agents);
        Assert.Contains(agg.CurrentState.PendingVisualEvents, e => e.Type == "mixi_poke");

        // 6. Cap visual events at MaxVisualEvents (16) and clamp overly long messages
        string hugeMessage = new string('X', 500);
        for (int i = 0; i < 24; i++)
        {
            var resp = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/event",
                new StringContent($"{{\"type\":\"celebrate\",\"message\":\"{hugeMessage}\"}}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        Assert.True(agg.CurrentState.PendingVisualEvents.Count <= HqStateAggregator.MaxVisualEvents);
        Assert.All(agg.CurrentState.PendingVisualEvents, e => Assert.True((e.Message?.Length ?? 0) <= HqStateAggregator.MaxMessageLength));

        // 7. Demo endpoints for drama & celebrate
        var demoDrama = await client.PostAsync($"http://127.0.0.1:{port}/api/demo/drama", null);
        Assert.Equal(HttpStatusCode.OK, demoDrama.StatusCode);
        Assert.Contains(agg.CurrentState.PendingVisualEvents, e => e.Type == "review_changes");

        var demoCelebrate = await client.PostAsync($"http://127.0.0.1:{port}/api/demo/celebrate", null);
        Assert.Equal(HttpStatusCode.OK, demoCelebrate.StatusCode);
        Assert.Contains(agg.CurrentState.PendingVisualEvents, e => e.Type == "celebrate");
    }

    [Fact]
    public void HookInstaller_LosslessSpanSplicing_PreservesFormattingEscapesBomEolEof_AndPreExistingEmptyContainer()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-lossless-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);

            // Case 1: UTF-8 BOM + CRLF + unusual spacing/indentation + escaped unicode (\u0041) + no trailing newline
            byte[] utf8Bom = { 0xEF, 0xBB, 0xBF };
            string richJson = "{\r\n    \"zKey\"   :   \"\\u0041\\u0042\",\r\n    \"aKey\": [1,  2,  3],\r\n    \"hooks\" : {\r\n        \"PreToolUse\" : [\r\n            {\"matcher\":\"*\",   \"hooks\":[{\"type\":\"command\",\"command\":\"echo keep\\u0021\"}]}\r\n        ]\r\n    }\r\n}";
            byte[] originalRichBytes = utf8Bom.Concat(Encoding.UTF8.GetBytes(richJson)).ToArray();
            File.WriteAllBytes(claudeSettings, originalRichBytes);

            var installRes = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, installRes.Status);

            byte[] installedBytes = File.ReadAllBytes(claudeSettings);
            // BOM preserved, unrelated bytes (indentation, spacing, unicode escapes, key order) preserved byte-for-byte!
            Assert.True(installedBytes.Take(3).SequenceEqual(utf8Bom));
            string installedText = Encoding.UTF8.GetString(installedBytes, 3, installedBytes.Length - 3);
            Assert.Contains("\"zKey\"   :   \"\\u0041\\u0042\"", installedText, StringComparison.Ordinal);
            Assert.Contains("\"aKey\": [1,  2,  3]", installedText, StringComparison.Ordinal);
            Assert.Contains("{\"matcher\":\"*\",   \"hooks\":[{\"type\":\"command\",\"command\":\"echo keep\\u0021\"}]}", installedText, StringComparison.Ordinal);
            Assert.False(installedText.EndsWith("\n", StringComparison.Ordinal));

            var uninstallRes = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninstallRes.Status);
            byte[] actualRichBytes = File.ReadAllBytes(claudeSettings);
            Assert.Equal(Encoding.UTF8.GetString(originalRichBytes), Encoding.UTF8.GetString(actualRichBytes));
            Assert.Equal(originalRichBytes, actualRichBytes);

            // Case 2: Starting from exact compact {"hooks":{"PreToolUse":[]}} -> install -> uninstall returns exact byte-identical {"hooks":{"PreToolUse":[]}}!
            byte[] exactEmptyContainerBytes = Encoding.UTF8.GetBytes("""{"hooks":{"PreToolUse":[]}}""");
            File.WriteAllBytes(claudeSettings, exactEmptyContainerBytes);

            var install2 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, install2.Status);

            var uninstall2 = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninstall2.Status);
            byte[] actualEmptyBytes = File.ReadAllBytes(claudeSettings);
            Assert.Equal(Encoding.UTF8.GetString(exactEmptyContainerBytes), Encoding.UTF8.GetString(actualEmptyBytes));
            Assert.Equal(exactEmptyContainerBytes, actualEmptyBytes);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_OpenCodeEventHandler_AndLegacyHookUpgradeAndUninstall()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-opencode-legacy-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        string openCodeDir = Path.Combine(fakeHome, ".config", "opencode");
        string openCodePlugin = Path.Combine(openCodeDir, "plugins", "kagedesk-plugin.js");
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(openCodeDir);

        try
        {
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319, openCodeVersionProbe: () => "opencode 1.18.29");

            // 1. OpenCode plugin exports tool.execute.before/after AND supported event({ event }) handler branching strictly on event.type
            var ocInstall = installer.InstallOpenCode();
            Assert.Equal(HookActionStatus.Installed, ocInstall.Status);
            string pluginJs = File.ReadAllText(openCodePlugin);
            Assert.Contains("\"tool.execute.before\"", pluginJs, StringComparison.Ordinal);
            Assert.Contains("\"tool.execute.after\"", pluginJs, StringComparison.Ordinal);
            Assert.Contains("event: async ({ event }) =>", pluginJs, StringComparison.Ordinal);
            Assert.Contains("event.type", pluginJs, StringComparison.Ordinal);
            Assert.Contains("\"permission.asked\"", pluginJs, StringComparison.Ordinal);
            Assert.Contains("\"permission.updated\"", pluginJs, StringComparison.Ordinal);
            Assert.Contains("\"session.idle\"", pluginJs, StringComparison.Ordinal);
            Assert.Contains("\"session.error\"", pluginJs, StringComparison.Ordinal);
            // Must NOT inspect or forward arbitrary event payloads
            Assert.DoesNotContain("event.properties", pluginJs, StringComparison.Ordinal);
            Assert.DoesNotContain("event.payload", pluginJs, StringComparison.Ordinal);
            Assert.DoesNotContain("JSON.stringify(event)", pluginJs, StringComparison.Ordinal);

            // 2. Seed Claude with exact legacy KageDesk fingerprints (tool_done, notification, idle) + unrelated user hook
            string emitterEscaped = installer.DefaultEmitterScriptPath.Replace("\\", "\\\\");
            string legacyDoc =
                "{\n" +
                "  \"customUserKey\": \"stay-intact\\u0021\",\n" +
                "  \"hooks\": {\n" +
                "    \"PostToolUse\": [\n" +
                "      {\n" +
                "        \"matcher\": \"*\",\n" +
                "        \"hooks\": [\n" +
                "          { \"type\": \"command\", \"command\": \"echo user-post-tool\" },\n" +
                $"          {{ \"type\": \"command\", \"command\": \"powershell -NoProfile -ExecutionPolicy Bypass -File \\\"{emitterEscaped}\\\" -Type tool_done -AgentId claude -Message \\\"Tool execution completed\\\" -Port 4319\", \"async\": true }}\n" +
                "        ]\n" +
                "      }\n" +
                "    ],\n" +
                "    \"Notification\": [\n" +
                "      {\n" +
                "        \"hooks\": [\n" +
                $"          {{ \"type\": \"command\", \"command\": \"powershell -NoProfile -ExecutionPolicy Bypass -File \\\"{emitterEscaped}\\\" -Type notification -AgentId claude -Message \\\"Notification received\\\" -Port 4319\" }}\n" +
                "        ]\n" +
                "      }\n" +
                "    ],\n" +
                "    \"Stop\": [\n" +
                "      {\n" +
                "        \"hooks\": [\n" +
                $"          {{ \"type\": \"command\", \"command\": \"powershell -NoProfile -ExecutionPolicy Bypass -File \\\"{emitterEscaped}\\\" -Type idle -AgentId claude -Message \\\"Agent stopped\\\" -Port 4319\", \"async\": true }}\n" +
                "        ]\n" +
                "      }\n" +
                "    ]\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(claudeSettings, legacyDoc);

            // Install upgrades/replaces legacy fingerprints with current schema while leaving unrelated user hook intact
            var upgraded = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, upgraded.Status);
            string upgradedText = File.ReadAllText(claudeSettings);
            Assert.DoesNotContain("-Type tool_done", upgradedText, StringComparison.Ordinal);
            Assert.DoesNotContain("-Type notification", upgradedText, StringComparison.Ordinal);
            Assert.DoesNotContain("-Type idle", upgradedText, StringComparison.Ordinal);
            Assert.Contains("-Type tool_complete", upgradedText, StringComparison.Ordinal);
            Assert.Contains("-Type approval", upgradedText, StringComparison.Ordinal);
            Assert.Contains("-Type turn_done", upgradedText, StringComparison.Ordinal);
            Assert.Contains("echo user-post-tool", upgradedText, StringComparison.Ordinal);
            Assert.Contains("\"customUserKey\": \"stay-intact\\u0021\"", upgradedText, StringComparison.Ordinal);

            // Now test direct uninstall from legacy-only config (without prior upgrade) cleans exact legacy fingerprints cleanly
            File.WriteAllText(claudeSettings, legacyDoc);
            var uninstalledLegacy = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninstalledLegacy.Status);
            string afterUninstall = File.ReadAllText(claudeSettings);
            Assert.DoesNotContain("kagedesk-emit.ps1", afterUninstall, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("echo user-post-tool", afterUninstall, StringComparison.Ordinal);
            Assert.Contains("\"customUserKey\": \"stay-intact\\u0021\"", afterUninstall, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_PostFlushConflict_AbortsDeletesTempFileAndLeavesConcurrentWriterIntact()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-postflush-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            File.WriteAllText(claudeSettings, """{"theme":"initial"}""");
            string externalWriterContent = """{"theme":"written-after-temp-flush"}""";
            bool sawTempFileOnDisk = false;

            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319)
            {
                BeforeCommitHookForTest = path =>
                {
                    // BeforeCommitHookForTest fires AFTER temp file write + Flush(true), right before final check & File.Replace
                    sawTempFileOnDisk = Directory.GetFiles(claudeDir, "*.tmp").Length > 0;
                    File.WriteAllText(path, externalWriterContent);
                }
            };

            var res = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.True(sawTempFileOnDisk, "Temp file must be staged and flushed BEFORE final conflict check.");
            Assert.Equal(HookActionStatus.Failed, res.Status);
            Assert.Equal(externalWriterContent, File.ReadAllText(claudeSettings));
            Assert.Empty(Directory.GetFiles(claudeDir, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_MaintenanceDuringAppend_NeverSwallowsFinalTurnEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-maint-drain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string logPath = Path.Combine(tempDir, "session-turn.jsonl");
            File.WriteAllText(logPath, "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n");

            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                debounceWindow: TimeSpan.FromMilliseconds(20),
                maxCoalescingDelay: TimeSpan.FromMilliseconds(40));

            collector.Start();
            // Seed initial Working state
            collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.Contains(collector.GetCurrentSnapshots(), s => s.State == AgentState.Working);

            // Inject final turn-end append DETERMINISTICALLY while maintenance is executing inside the serial consumer!
            int injected = 0;
            collector.OnMaintenanceStepForTest = () =>
            {
                if (Interlocked.Exchange(ref injected, 1) == 0)
                {
                    File.AppendAllText(logPath, "{\"type\":\"assistant\",\"stop_reason\":\"end_turn\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}\n");
                    // Enqueue the changed path while consumer is actively inside maintenance
                    collector.EnqueuePathForTest(logPath);
                }
            };

            collector.TriggerMaintenanceNowForTest();

            // Consumer MUST re-check on exit and drain the turn-end append without swallowing it!
            Assert.True(SpinWait.SpinUntil(
                () => collector.GetCurrentSnapshots().Any(s => s.State == AgentState.Done),
                TimeSpan.FromSeconds(3)), "Turn-end append queued during maintenance MUST be drained immediately.");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_RootDeletedAndRecreated_RecoversDeadWatcherAndDetectsNewWrites()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-watcher-recover-{Guid.NewGuid():N}");
        string watchedRoot = Path.Combine(tempRoot, "claude-projects");
        Directory.CreateDirectory(watchedRoot);

        try
        {
            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", watchedRoot) },
                debounceWindow: TimeSpan.FromMilliseconds(25),
                maxCoalescingDelay: TimeSpan.FromMilliseconds(60));

            collector.Start();
            Assert.Equal(1, collector.ActiveWatcherCount);

            // Simulate dead/broken FileSystemWatcher.Error AND delete/recreate root directory
            collector.SimulateWatcherErrorForTest(watchedRoot);
            Assert.Equal(0, collector.ActiveWatcherCount);

            Directory.Delete(watchedRoot, recursive: true);
            Directory.CreateDirectory(watchedRoot);

            // Reconciliation must recreate and re-attach a fresh watcher on the recreated root
            collector.ReconcileMissingRootsNow();
            Assert.Equal(1, collector.ActiveWatcherCount);

            string freshLog = Path.Combine(watchedRoot, "recovered-session.jsonl");
            File.WriteAllText(freshLog, "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"pytest\"}]}}\n");

            Assert.True(SpinWait.SpinUntil(
                () => collector.GetCurrentSnapshots().Any(s =>
                    s.Id.StartsWith("session:claude:", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.ToolName, "TEST", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(3)), "Recreated watcher MUST detect writes in recreated root directory.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogCollectorAndAggregator_EndToEnd_TwoConcurrentLogs_ReusedPidRejection_EventTimeOrder_And8KBReadCap()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-e2e-corr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var baseTime = new DateTimeOffset(2026, 4, 10, 16, 0, 0, TimeSpan.Zero);
            var agg = new HqStateAggregator(timeProvider: () => baseTime.AddSeconds(5));

            var procA = new AgentSnapshot("proc-claude-1001", "Claude A", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 1001, ProcessStartTicks: 111000L);
            var procB = new AgentSnapshot("proc-claude-2002", "Claude B", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 2002, ProcessStartTicks: 222000L);
            agg.UpdateProcessAgents(new[] { procA, procB });

            string logA = Path.Combine(tempDir, "session-a.jsonl");
            string logB = Path.Combine(tempDir, "session-b.jsonl");
            string logReusedPid = Path.Combine(tempDir, "session-reused.jsonl");

            // Write >100 KB into logA so we also verify total file bytes read (including header probe) <= 8192 B (Item 8.3)!
            var sbA = new StringBuilder(120_000);
            for (int i = 0; i < 600; i++)
            {
                sbA.AppendLine($"{{\"timestamp\":\"2026-04-10T15:50:00Z\",\"type\":\"user\",\"content\":\"padding line {i} xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\"}}");
            }
            sbA.AppendLine("{\"timestamp\":\"2026-04-10T16:00:02Z\",\"pid\":1001,\"process_start_ticks\":111000,\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"pytest\"}]}}");
            File.WriteAllText(logA, sbA.ToString());

            // Second concurrent log for procB (different tool: Edit -> CODE)
            File.WriteAllText(logB, "{\"timestamp\":\"2026-04-10T16:00:03Z\",\"pid\":2002,\"process_start_ticks\":222000,\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n");

            // Third log with REUSED PID 1001 but WRONG process_start_ticks (999999 != 111000) and OLDER event timestamp
            File.WriteAllText(logReusedPid, "{\"timestamp\":\"2026-04-10T15:59:50Z\",\"pid\":1001,\"process_start_ticks\":999999,\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"AskUserQuestion\"}]}}\n");

            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => baseTime.AddSeconds(5));

            collector.ProcessFileDelta(logA, "claude", "Claude");
            // Verify Item 8.3: total file bytes read (header probe + tail read) NEVER exceeds 8192 B!
            Assert.True(collector.LastTotalFileBytesReadForTest <= SessionLogActivityCollector.MaxTailReadBytes,
                $"Expected total read <= {SessionLogActivityCollector.MaxTailReadBytes}, got {collector.LastTotalFileBytesReadForTest}");

            collector.ProcessFileDelta(logB, "claude", "Claude");
            collector.ProcessFileDelta(logReusedPid, "claude", "Claude");

            var snapshots = collector.GetCurrentSnapshots();
            // Per-session snapshots (NOT collapsed into one session:claude)
            Assert.Equal(3, snapshots.Count);
            Assert.Equal(3, snapshots.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            // Event-time freshness ordering: logB (16:00:03) > logA (16:00:02) > logReusedPid (15:59:50)
            Assert.Equal(new DateTimeOffset(2026, 4, 10, 16, 0, 3, TimeSpan.Zero), snapshots[0].UpdatedAt);
            Assert.Equal(new DateTimeOffset(2026, 4, 10, 16, 0, 2, TimeSpan.Zero), snapshots[1].UpdatedAt);
            Assert.Equal(new DateTimeOffset(2026, 4, 10, 15, 59, 50, TimeSpan.Zero), snapshots[2].UpdatedAt);

            agg.UpdateSessionLogActivity(snapshots);

            var mergedA = agg.CurrentState.Agents.Single(a => a.Id == "proc-claude-1001");
            var mergedB = agg.CurrentState.Agents.Single(a => a.Id == "proc-claude-2002");
            // Each concurrent process enriched independently, and reused PID did NOT misattribute NeedsApproval onto procA!
            Assert.Equal(AgentState.Working, mergedA.State);
            Assert.Equal("TEST", mergedA.ToolName, ignoreCase: true);
            Assert.Equal(AgentState.Working, mergedB.State);
            Assert.Equal("CODE", mergedB.ToolName, ignoreCase: true);
            // Reused-PID log emitted as one separate provider activity tile instead of mutating procA
            Assert.Single(agg.CurrentState.Agents, a => a.Source == "session-log" && a.State == AgentState.NeedsApproval);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_EmptyContainerWhitespaceTrivia_ByteIdenticalRoundTrip_CompactAndMultilineRoot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-hooks-trivia-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);

            // Case 1: Compact empty array with interior spaces {"hooks":{"PreToolUse":[  ]}}
            byte[] compactOriginal = Encoding.UTF8.GetBytes("""{"hooks":{"PreToolUse":[  ]}}""");
            File.WriteAllBytes(claudeSettings, compactOriginal);

            var inst1 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, inst1.Status);

            string installedCompact = File.ReadAllText(claudeSettings);
            // Zero-width insertion keeps original interior spaces intact while remaining valid JSON
            using (JsonDocument.Parse(installedCompact)) { }
            Assert.Contains("  ]", installedCompact, StringComparison.Ordinal);

            var uninst1 = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninst1.Status);
            Assert.Equal(compactOriginal, File.ReadAllBytes(claudeSettings));

            // Case 2: Multiline empty root "{\n  \n}"
            byte[] multilineLfOriginal = Encoding.UTF8.GetBytes("{\n  \n}");
            File.WriteAllBytes(claudeSettings, multilineLfOriginal);

            var inst2 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, inst2.Status);
            using (JsonDocument.Parse(File.ReadAllBytes(claudeSettings))) { }

            var uninst2 = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninst2.Status);
            Assert.Equal(multilineLfOriginal, File.ReadAllBytes(claudeSettings));

            // Case 3: CRLF multiline empty root "{\r\n  \r\n}"
            byte[] multilineCrLfOriginal = Encoding.UTF8.GetBytes("{\r\n  \r\n}");
            File.WriteAllBytes(claudeSettings, multilineCrLfOriginal);

            var inst3 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, inst3.Status);
            using (JsonDocument.Parse(File.ReadAllBytes(claudeSettings))) { }

            var uninst3 = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninst3.Status);
            Assert.Equal(multilineCrLfOriginal, File.ReadAllBytes(claudeSettings));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_ExternalReplacementAfterInstall_InvalidatesHashBoundProvenanceAndKeepsEmptyContainers()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-hooks-prov-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            // Initially no "hooks" object -> install creates "hooks" + all 4 event keys and binds provenance to post-install SHA-256
            File.WriteAllText(claudeSettings, """
            {
              "theme": "dark"
            }
            """);

            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);
            var installRes = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, installRes.Status);

            // External edit changes the file content after install -> invalidates hash-bound provenance!
            string externallyEdited = File.ReadAllText(claudeSettings).Replace("\"dark\"", "\"light\"", StringComparison.Ordinal);
            File.WriteAllText(claudeSettings, externallyEdited);

            // Uninstall must remove ONLY KageDesk hook entries and MUST NOT delete empty "hooks" or empty event arrays!
            var uninstallRes = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninstallRes.Status);

            string afterText = File.ReadAllText(claudeSettings);
            Assert.DoesNotContain("kagedesk-emit.ps1", afterText, StringComparison.OrdinalIgnoreCase);

            var root = JsonNode.Parse(afterText)!.AsObject();
            Assert.Equal("light", root["theme"]!.GetValue<string>());
            Assert.True(root.ContainsKey("hooks"), "External edit invalidated SHA-256 provenance; empty 'hooks' must NOT be deleted.");
            var hooksObj = root["hooks"]!.AsObject();
            Assert.True(hooksObj.ContainsKey("PreToolUse"));
            Assert.True(hooksObj.ContainsKey("PostToolUse"));
            Assert.True(hooksObj.ContainsKey("Notification"));
            Assert.True(hooksObj.ContainsKey("Stop"));
            Assert.Empty(hooksObj["PreToolUse"]!.AsArray());
            Assert.Empty(hooksObj["PostToolUse"]!.AsArray());
            Assert.Empty(hooksObj["Notification"]!.AsArray());
            Assert.Empty(hooksObj["Stop"]!.AsArray());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_LegacyClaudeNotificationSharedGroup_SeparatesUpgradedHookIntoDedicatedApprovalWrapper()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-hooks-notif-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);
            string emitterPath = installer.DefaultEmitterScriptPath;
            string legacyCmd = installer.BuildHookCommand(emitterPath, "notification", "claude", "Notification received");
            string encodedLegacyCmd = JsonSerializer.Serialize(legacyCmd);

            // Shared Notification groups (both broad matcher "*" and omitted matcher) containing
            // BOTH a user hook AND a legacy KageDesk hook
            File.WriteAllText(claudeSettings, $$"""
            {
              "hooks": {
                "Notification": [
                  {
                    "matcher": "*",
                    "hooks": [
                      {
                        "type": "command",
                        "command": "echo keep-user-notification-hook"
                      },
                      {
                        "type": "command",
                        "command": {{encodedLegacyCmd}}
                      }
                    ]
                  }
                ]
              }
            }
            """);

            var installRes = installer.InstallClaudeCode(emitterPath);
            Assert.Equal(HookActionStatus.Installed, installRes.Status);

            var root = JsonNode.Parse(File.ReadAllText(claudeSettings))!.AsObject();
            var notifArr = root["hooks"]!["Notification"]!.AsArray();
            Assert.Equal(2, notifArr.Count);

            // Group 0: original user group keeps broad matcher "*" and ONLY the user hook (legacy KageDesk hook removed!)
            var userGroup = notifArr[0]!.AsObject();
            Assert.Equal("*", userGroup["matcher"]!.GetValue<string>());
            var userGroupHooks = userGroup["hooks"]!.AsArray();
            Assert.Single(userGroupHooks);
            Assert.Equal("echo keep-user-notification-hook", userGroupHooks[0]!["command"]!.GetValue<string>());

            // Group 1: dedicated KageDesk approval-scoped wrapper group with approval matcher and async:true
            var kageGroup = notifArr[1]!.AsObject();
            Assert.Equal("permission_prompt|elicitation_dialog", kageGroup["matcher"]!.GetValue<string>());
            var kageGroupHooks = kageGroup["hooks"]!.AsArray();
            Assert.Single(kageGroupHooks);
            Assert.Contains("-Type approval", kageGroupHooks[0]!["command"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(kageGroupHooks[0]!["async"]!.GetValue<bool>());

            // Second install is strictly idempotent
            byte[] afterInstall = File.ReadAllBytes(claudeSettings);
            var install2 = installer.InstallClaudeCode(emitterPath);
            Assert.Equal(HookActionStatus.AlreadyInstalled, install2.Status);
            Assert.Equal(afterInstall, File.ReadAllBytes(claudeSettings));

            // Uninstall removes ONLY KageDesk's dedicated wrapper and keeps user's hook + "*" matcher intact
            var uninstallRes = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninstallRes.Status);
            var afterRoot = JsonNode.Parse(File.ReadAllText(claudeSettings))!.AsObject();
            var afterNotif = afterRoot["hooks"]!["Notification"]!.AsArray();
            Assert.Single(afterNotif);
            Assert.Equal("*", afterNotif[0]!["matcher"]!.GetValue<string>());
            Assert.Equal("echo keep-user-notification-hook", afterNotif[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HookInstaller_OpenCodeVersionDetection_V1V2AndUnsupportedMajor_WithIsolatedNodeExecution()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-oc-ver-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string openCodeDir = Path.Combine(fakeHome, ".config", "opencode");
        string pluginPath = Path.Combine(openCodeDir, "plugins", "kagedesk-plugin.js");
        Directory.CreateDirectory(openCodeDir);

        try
        {
            // 1. Unsupported major versions (v3+, v0.x, unparseable) MUST return Unsupported without writing a plugin!
            foreach (string badVersion in new[] { "opencode 3.0.0", "0.9.4", "unknown-build" })
            {
                var unsupportedInstaller = new HookInstaller(
                    fakeHome,
                    fakeAppData,
                    bridgePort: 4319,
                    openCodeVersionProbe: () => badVersion);

                var res = unsupportedInstaller.InstallOpenCode();
                Assert.Equal(HookActionStatus.Unsupported, res.Status);
                Assert.False(File.Exists(pluginPath), $"Unsupported OpenCode version '{badVersion}' must NOT write a plugin file.");
            }

            // 2. Spin up a real local loopback HTTP listener to verify real ESM plugin execution for V1 and V2!
            int port = GetAvailablePort();
            var receivedTypes = new System.Collections.Concurrent.ConcurrentQueue<(string Type, string AgentId)>();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            using var serverCts = new CancellationTokenSource();
            Task serverTask = Task.Run(async () =>
            {
                while (!serverCts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await listener.GetContextAsync().WaitAsync(serverCts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    string body = await sr.ReadToEndAsync(serverCts.Token);
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        string t = doc.RootElement.GetProperty("type").GetString() ?? "";
                        string a = doc.RootElement.GetProperty("agentId").GetString() ?? "";
                        receivedTypes.Enqueue((t, a));
                    }
                    catch { }

                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
            });

            // 2a. OpenCode V1 install & real Node.js ESM execution test
            var v1Installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: port, openCodeVersionProbe: () => "opencode 1.18.29");
            var v1Install = v1Installer.InstallOpenCode();
            Assert.Equal(HookActionStatus.Installed, v1Install.Status);
            Assert.True(File.Exists(pluginPath));
            string v1Source = File.ReadAllText(pluginPath);
            Assert.Contains("// kagedesk-managed-opencode-plugin:v1", v1Source, StringComparison.Ordinal);
            Assert.Contains("export const KageDeskPlugin", v1Source, StringComparison.Ordinal);

            string v1Harness = Path.Combine(tempRoot, "run-v1.mjs");
            File.WriteAllText(v1Harness, """
            import { pathToFileURL } from "node:url";
            const origFetch = globalThis.fetch;
            const pending = [];
            globalThis.fetch = (...args) => {
              const p = origFetch(...args);
              pending.push(p);
              return p;
            };
            const mod = await import(pathToFileURL(process.argv[2]).href);
            if (typeof mod.KageDeskPlugin !== "function") throw new Error("Missing V1 KageDeskPlugin export");
            const hooks = await mod.KageDeskPlugin({});
            await hooks["tool.execute.before"]();
            await hooks["tool.execute.after"]();
            await hooks.event({ event: { type: "session.idle" } });
            await hooks.event({ event: { type: "permission.asked" } });
            await hooks.event({ event: { type: "session.error" } });
            await Promise.all(pending);
            """);

            RunNodeScriptOrFail(v1Harness, pluginPath);
            var v1Events = DrainQueue(receivedTypes);
            Assert.Equal(5, v1Events.Count);
            Assert.Contains(("tool_start", "opencode"), v1Events);
            Assert.Contains(("tool_complete", "opencode"), v1Events);
            Assert.Contains(("turn_done", "opencode"), v1Events);
            Assert.Contains(("approval", "opencode"), v1Events);
            Assert.Contains(("fail", "opencode"), v1Events);

            // 2b. OpenCode V2 upgrade & real Node.js ESM execution test (default export + setup(ctx) + cleanup abort)
            var v2Installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: port, openCodeVersionProbe: () => "opencode 2.1.0");
            var v2Install = v2Installer.InstallOpenCode();
            Assert.Equal(HookActionStatus.Installed, v2Install.Status);
            string v2Source = File.ReadAllText(pluginPath);
            Assert.Contains("// kagedesk-managed-opencode-plugin:v2", v2Source, StringComparison.Ordinal);
            Assert.Contains("export default", v2Source, StringComparison.Ordinal);
            Assert.Contains("ctx.tool.hook", v2Source, StringComparison.Ordinal);
            Assert.Contains("ctx.event.subscribe", v2Source, StringComparison.Ordinal);

            // V2 second install is strictly idempotent
            Assert.Equal(HookActionStatus.AlreadyInstalled, v2Installer.InstallOpenCode().Status);

            string v2Harness = Path.Combine(tempRoot, "run-v2.mjs");
            File.WriteAllText(v2Harness, """
            import { pathToFileURL } from "node:url";
            const origFetch = globalThis.fetch;
            const pending = [];
            globalThis.fetch = (...args) => {
              const p = origFetch(...args);
              pending.push(p);
              return p;
            };
            const mod = await import(pathToFileURL(process.argv[2]).href);
            const plugin = mod.default;
            if (!plugin || plugin.id !== "kagedesk" || typeof plugin.setup !== "function") {
              throw new Error("Invalid V2 default plugin contract");
            }
            const toolHooks = new Map();
            let subSignal = null;
            let resolveEventsDone;
            const eventsDone = new Promise(r => { resolveEventsDone = r; });
            const ctx = {
              tool: {
                async hook(name, fn) { toolHooks.set(name, fn); }
              },
              event: {
                async *subscribe({ signal }) {
                  subSignal = signal;
                  yield { type: "session.idle" };
                  yield { type: "permission.asked" };
                  yield { type: "session.error" };
                  resolveEventsDone();
                }
              }
            };
            const cleanup = await plugin.setup(ctx);
            await toolHooks.get("execute.before")({}, {});
            await toolHooks.get("execute.after")({}, {});
            await eventsDone;
            await new Promise(r => setTimeout(r, 20));
            await Promise.all(pending);
            if (typeof cleanup !== "function") throw new Error("V2 setup must return cleanup function");
            cleanup();
            if (!subSignal || !subSignal.aborted) throw new Error("V2 cleanup must abort event subscription signal");
            """);

            RunNodeScriptOrFail(v2Harness, pluginPath);
            var v2Events = DrainQueue(receivedTypes);
            Assert.Equal(5, v2Events.Count);
            Assert.Contains(("tool_start", "opencode"), v2Events);
            Assert.Contains(("tool_complete", "opencode"), v2Events);
            Assert.Contains(("turn_done", "opencode"), v2Events);
            Assert.Contains(("approval", "opencode"), v2Events);
            Assert.Contains(("fail", "opencode"), v2Events);

            // Uninstall removes V2 plugin cleanly
            Assert.Equal(HookActionStatus.Uninstalled, v2Installer.UninstallOpenCode().Status);
            Assert.False(File.Exists(pluginPath));

            serverCts.Cancel();
            listener.Stop();
            try { await serverTask; } catch { }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void RunNodeScriptOrFail(string scriptPath, string pluginArg)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(pluginArg);

        using var proc = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(proc);
        string stdout = proc!.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(10_000), $"Node harness timed out. stdout={stdout} stderr={stderr}");
        Assert.True(proc.ExitCode == 0, $"Node harness failed (exit={proc.ExitCode}): stdout={stdout} stderr={stderr}");
    }

    private static List<(string Type, string AgentId)> DrainQueue(
        System.Collections.Concurrent.ConcurrentQueue<(string Type, string AgentId)> queue)
    {
        var list = new List<(string Type, string AgentId)>();
        while (queue.TryDequeue(out var item))
        {
            list.Add(item);
        }
        return list;
    }

    [Fact]
    public void SessionLogActivityCollector_CoalescedBatch_AccumulatesIdentityIndependentlyAndSelectsFreshestTimestamp()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-coalesced-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var baseTime = new DateTimeOffset(2026, 4, 10, 16, 10, 0, TimeSpan.Zero);

            // (a) Single coalesced delta: record 1 carries ONLY verified (pid, process_start_ticks),
            //     record 2 carries activity with NO identity fields -> MUST correlate cleanly!
            string logIdentityBatch = Path.Combine(tempDir, "coalesced-identity.jsonl");
            File.WriteAllText(logIdentityBatch,
                "{\"type\":\"session_meta\",\"pid\":4242,\"process_start_ticks\":777888999}\n" +
                "{\"timestamp\":\"2026-04-10T16:10:02Z\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n");

            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => baseTime.AddSeconds(5));

            var derivedA = collector.ProcessFileDelta(logIdentityBatch, "claude", "Claude");
            Assert.NotNull(derivedA);
            Assert.Equal(AgentState.Working, derivedA!.State);
            Assert.Equal("CODE", derivedA.DerivedActionToken, ignoreCase: true);
            Assert.Equal(4242, derivedA.CorrelatedProcessId);
            Assert.Equal(777888999L, derivedA.CorrelatedProcessStartTicks);

            // Verify end-to-end correlation in HqStateAggregator
            var agg = new HqStateAggregator(timeProvider: () => baseTime.AddSeconds(5));
            var proc = new AgentSnapshot("proc-claude-4242", "Claude", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 4242, ProcessStartTicks: 777888999L);
            agg.UpdateProcessAgents(new[] { proc });
            agg.UpdateSessionLogActivity(collector.GetCurrentSnapshots());

            var merged = agg.CurrentState.Agents.Single(a => a.Id == "proc-claude-4242");
            Assert.Equal(AgentState.Working, merged.State);
            Assert.Equal("CODE", merged.ToolName, ignoreCase: true);

            // (b) Reversed physical order: record 1 has NEWER timestamp (16:10:30Z, Edit -> CODE),
            //     record 2 physically AFTER record 1 has OLDER timestamp (16:10:10Z, Read -> READ) -> MUST pick newer!
            string logReversed = Path.Combine(tempDir, "reversed-timestamps.jsonl");
            File.WriteAllText(logReversed,
                "{\"timestamp\":\"2026-04-10T16:10:30Z\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n" +
                "{\"timestamp\":\"2026-04-10T16:10:10Z\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\"}]}}\n");

            var derivedB = collector.ProcessFileDelta(logReversed, "claude", "Claude");
            Assert.NotNull(derivedB);
            Assert.Equal(new DateTimeOffset(2026, 4, 10, 16, 10, 30, TimeSpan.Zero), derivedB!.ObservedAt);
            Assert.Equal("CODE", derivedB.DerivedActionToken, ignoreCase: true);

            // Subsequent delta carrying an even older explicit timestamp must NOT overwrite the fresher stored activity
            File.AppendAllText(logReversed,
                "{\"timestamp\":\"2026-04-10T16:10:05Z\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"AskUserQuestion\"}]}}\n");
            var derivedB2 = collector.ProcessFileDelta(logReversed, "claude", "Claude");
            Assert.NotNull(derivedB2);
            Assert.Equal(new DateTimeOffset(2026, 4, 10, 16, 10, 30, TimeSpan.Zero), derivedB2!.ObservedAt);
            Assert.Equal(AgentState.Working, derivedB2.State);
            Assert.Equal("CODE", derivedB2.DerivedActionToken, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_DirectRandomAccessRead_NeverExceeds8192BytesAndZeroHiddenPrefetch()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-direct-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string logPath = Path.Combine(tempDir, "large-session.jsonl");
            var sb = new StringBuilder(130_000);
            for (int i = 0; i < 650; i++)
            {
                sb.AppendLine($"{{\"timestamp\":\"2026-04-10T16:00:00Z\",\"type\":\"user\",\"content\":\"padding record {i} aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}");
            }
            sb.AppendLine("{\"timestamp\":\"2026-04-10T16:05:00Z\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}");
            File.WriteAllText(logPath, sb.ToString());

            var recordedReads = new List<(long Offset, int RequestedBytes, int ActualBytes)>();
            var now = new DateTimeOffset(2026, 4, 10, 16, 5, 5, TimeSpan.Zero);

            using var collector = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => now)
            {
                LowLevelReadOverrideForTest = (handle, destination, fileOffset) =>
                {
                    int actual = RandomAccess.Read(handle, destination, fileOffset);
                    recordedReads.Add((fileOffset, destination.Length, actual));
                    return actual;
                }
            };

            // Poll 1: large >120 KB file -> 64 B header probe + <= 8128 B tail read (ZERO 4 KiB FileStream prefetch!)
            var obs1 = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(obs1);
            Assert.Equal("CODE", obs1!.DerivedActionToken, ignoreCase: true);

            Assert.Equal(2, recordedReads.Count);
            Assert.Equal(0L, recordedReads[0].Offset);
            Assert.Equal(64, recordedReads[0].RequestedBytes);
            Assert.Equal(64, recordedReads[0].ActualBytes);

            int totalRequested1 = recordedReads.Sum(r => r.RequestedBytes);
            int totalActual1 = recordedReads.Sum(r => r.ActualBytes);
            Assert.True(totalRequested1 <= SessionLogActivityCollector.MaxTailReadBytes,
                $"Underlying requested bytes ({totalRequested1}) exceeded {SessionLogActivityCollector.MaxTailReadBytes}.");
            Assert.True(totalActual1 <= SessionLogActivityCollector.MaxTailReadBytes,
                $"Underlying actual bytes read ({totalActual1}) exceeded {SessionLogActivityCollector.MaxTailReadBytes}.");
            Assert.Equal(totalActual1, collector.LastTotalFileBytesReadForTest);

            // Poll 2: small 95-byte append -> 64 B header probe + exact delta bytes (ZERO extra prefetch!)
            recordedReads.Clear();
            string smallAppend = "{\"timestamp\":\"2026-04-10T16:05:06Z\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"pytest\"}]}}\n";
            int appendLen = Encoding.UTF8.GetByteCount(smallAppend);
            File.AppendAllText(logPath, smallAppend);

            var obs2 = collector.ProcessFileDelta(logPath, "claude", "Claude");
            Assert.NotNull(obs2);
            Assert.Equal("TEST", obs2!.DerivedActionToken, ignoreCase: true);

            Assert.Equal(2, recordedReads.Count);
            Assert.Equal(64, recordedReads[0].RequestedBytes);
            Assert.Equal(appendLen, recordedReads[1].RequestedBytes);
            Assert.Equal(64 + appendLen, recordedReads.Sum(r => r.ActualBytes));
            Assert.True(recordedReads.Sum(r => r.RequestedBytes) <= SessionLogActivityCollector.MaxTailReadBytes);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HookInstaller_OpenCodeProbe_FailClosedOnNullEmptyNonzeroTimeoutAndDrainsConcurrentStderr()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-oc-failclosed-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string openCodeDir = Path.Combine(fakeHome, ".config", "opencode");
        string pluginDir = Path.Combine(openCodeDir, "plugins");
        string pluginPath = Path.Combine(pluginDir, "kagedesk-plugin.js");
        Directory.CreateDirectory(openCodeDir);

        try
        {
            // 1. Custom probe returning null, empty, whitespace, throwing, or unparseable -> MUST fail-closed (never V1 fallback, never write plugin!)
            Func<string?>[] failingProbes =
            {
                () => null,
                () => "",
                () => "   \r\n\t  ",
                () => throw new InvalidOperationException("simulated probe error"),
                () => "opencode dev-snapshot"
            };

            foreach (var probe in failingProbes)
            {
                var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319, openCodeVersionProbe: probe);
                var res = installer.InstallOpenCode();
                Assert.Equal(HookActionStatus.Unsupported, res.Status);
                Assert.NotEqual(HookActionStatus.Installed, res.Status);
                Assert.False(File.Exists(pluginPath), "Failed/unknown OpenCode probe must NOT write kagedesk-plugin.js.");
                Assert.False(Directory.Exists(pluginDir), "Failed/unknown OpenCode probe must NOT create plugins directory.");
            }

            // 2. Real child process probe: missing CLI -> ProbeFailedOrUnavailable
            var missingRes = await HookInstaller.ProbeOpenCodeExecutableVersionAsync(
                "kagedesk_nonexistent_opencode_binary_xyz",
                new[] { "--version" },
                TimeSpan.FromMilliseconds(500));
            Assert.Equal(OpenCodeVersionProbeOutcome.ProbeFailedOrUnavailable, missingRes.Outcome);

            // 3. Real child process probe: nonzero exit code even with v1-looking stdout -> ProbeFailedOrUnavailable!
            var nonzeroRes = await HookInstaller.ProbeOpenCodeExecutableVersionAsync(
                "node",
                new[] { "-e", "process.stdout.write('opencode 1.18.29\\n'); process.exit(1);" },
                TimeSpan.FromSeconds(5));
            Assert.Equal(OpenCodeVersionProbeOutcome.ProbeFailedOrUnavailable, nonzeroRes.Outcome);

            // 4. Real child process probe: empty output with exit 0 -> ProbeFailedOrUnavailable!
            var emptyRes = await HookInstaller.ProbeOpenCodeExecutableVersionAsync(
                "node",
                new[] { "-e", "" },
                TimeSpan.FromSeconds(5));
            Assert.Equal(OpenCodeVersionProbeOutcome.ProbeFailedOrUnavailable, emptyRes.Outcome);

            // 5. Real child process probe: heavy >128KB stderr BEFORE writing v2 stdout -> concurrent drain avoids deadlock and detects V2!
            var heavyStderrRes = await HookInstaller.ProbeOpenCodeExecutableVersionAsync(
                "node",
                new[] { "-e", "process.stderr.write('W'.repeat(131072)); process.stdout.write('opencode 2.1.0\\n');" },
                TimeSpan.FromSeconds(5));
            Assert.Equal(OpenCodeVersionProbeOutcome.DetectedV2, heavyStderrRes.Outcome);
            Assert.Equal(OpenCodePluginApiVersion.V2, heavyStderrRes.ApiVersion);

            // 6. Real stalled child process (writes >128KB stderr and sleeps 10s) -> aborts cleanly within single deadline without hanging!
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var timeoutRes = await HookInstaller.ProbeOpenCodeExecutableVersionAsync(
                "node",
                new[] { "-e", "process.stderr.write('E'.repeat(131072)); setTimeout(() => {}, 10000);" },
                TimeSpan.FromMilliseconds(300));
            sw.Stop();

            Assert.Equal(OpenCodeVersionProbeOutcome.ProbeFailedOrUnavailable, timeoutRes.Outcome);
            Assert.Contains("timed out", timeoutRes.Reason ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected stalled OpenCode probe to abort promptly near 300ms deadline, took {sw.ElapsedMilliseconds}ms.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HookInstaller_UnprovenMultilineEmptyContainer_UninstallPreservesOriginalCrlfInteriorWhitespace()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-unproven-multiline-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string claudeDir = Path.Combine(fakeHome, ".claude");
        string claudeSettings = Path.Combine(claudeDir, "settings.json");
        Directory.CreateDirectory(claudeDir);

        try
        {
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);

            // Case 1: Originally empty CRLF multiline PreToolUse array + unrelated key "other": 1
            const string originalSingleEvent = "{\r\n  \"other\": 1,\r\n  \"hooks\": {\r\n    \"PreToolUse\": [\r\n      \r\n    ]\r\n  }\r\n}";
            File.WriteAllBytes(claudeSettings, Encoding.UTF8.GetBytes(originalSingleEvent));

            var inst1 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, inst1.Status);

            // Externally edit "other": 1 -> "other": 2, invalidating whole-file SHA-256 provenance!
            string edited1 = File.ReadAllText(claudeSettings, Encoding.UTF8)
                .Replace("\"other\": 1", "\"other\": 2", StringComparison.Ordinal);
            File.WriteAllBytes(claudeSettings, Encoding.UTF8.GetBytes(edited1));

            var uninst1 = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninst1.Status);

            byte[] afterBytes1 = File.ReadAllBytes(claudeSettings);
            string afterText1 = Encoding.UTF8.GetString(afterBytes1);
            using (JsonDocument.Parse(afterBytes1)) { }
            Assert.DoesNotContain("kagedesk-emit.ps1", afterText1, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"other\": 2", afterText1, StringComparison.Ordinal);
            // Exact byte-for-byte survival of original CRLF interior whitespace inside PreToolUse!
            Assert.Contains("\"PreToolUse\": [\r\n      \r\n    ]", afterText1, StringComparison.Ordinal);

            // Case 2: All 4 managed arrays pre-exist as empty CRLF multiline containers + unrelated key edited
            const string originalAllEvents =
                "{\r\n" +
                "  \"other\": 1,\r\n" +
                "  \"hooks\": {\r\n" +
                "    \"PreToolUse\": [\r\n      \r\n    ],\r\n" +
                "    \"PostToolUse\": [\r\n      \r\n    ],\r\n" +
                "    \"Notification\": [\r\n      \r\n    ],\r\n" +
                "    \"Stop\": [\r\n      \r\n    ]\r\n" +
                "  }\r\n" +
                "}";
            byte[] expectedAfterAllEvents = Encoding.UTF8.GetBytes(originalAllEvents.Replace("\"other\": 1", "\"other\": 2", StringComparison.Ordinal));
            File.WriteAllBytes(claudeSettings, Encoding.UTF8.GetBytes(originalAllEvents));

            var inst2 = installer.InstallClaudeCode(installer.DefaultEmitterScriptPath);
            Assert.Equal(HookActionStatus.Installed, inst2.Status);

            string edited2 = File.ReadAllText(claudeSettings, Encoding.UTF8)
                .Replace("\"other\": 1", "\"other\": 2", StringComparison.Ordinal);
            File.WriteAllBytes(claudeSettings, Encoding.UTF8.GetBytes(edited2));

            var uninst2 = installer.UninstallClaudeCode();
            Assert.Equal(HookActionStatus.Uninstalled, uninst2.Status);

            byte[] actualAfterAllEvents = File.ReadAllBytes(claudeSettings);
            Assert.Equal(Encoding.UTF8.GetString(expectedAfterAllEvents), Encoding.UTF8.GetString(actualAfterAllEvents));
            Assert.Equal(expectedAfterAllEvents, actualAfterAllEvents);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_CompetingIdentityTuples_RejectsAmbiguityAndPreservesOwnActivityTuple()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-competing-tuples-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var baseTime = new DateTimeOffset(2026, 4, 10, 16, 20, 0, TimeSpan.Zero);

            // (a) Freshest activity WITHOUT its own tuple + two competing (pid, start_ticks) tuples in same batch
            //     (newer generation + delayed older metadata record) -> declines correlation (ambiguous, neither hijacked)!
            string ambiguousLog = Path.Combine(tempDir, "ambiguous-batch.jsonl");
            File.WriteAllText(ambiguousLog,
                "{\"timestamp\":\"2026-04-10T16:20:20Z\",\"type\":\"session_meta\",\"pid\":2002,\"process_start_ticks\":222000}\n" +
                "{\"timestamp\":\"2026-04-10T16:20:25Z\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n" +
                "{\"timestamp\":\"2026-04-10T16:20:05Z\",\"type\":\"session_meta\",\"pid\":1001,\"process_start_ticks\":111000}\n");

            using var collectorA = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => baseTime.AddSeconds(30));

            var derivedAmbiguous = collectorA.ProcessFileDelta(ambiguousLog, "claude", "Claude");
            Assert.NotNull(derivedAmbiguous);
            Assert.Equal(AgentState.Working, derivedAmbiguous!.State);
            Assert.Equal("CODE", derivedAmbiguous.DerivedActionToken, ignoreCase: true);
            Assert.Null(derivedAmbiguous.CorrelatedProcessId);
            Assert.Null(derivedAmbiguous.CorrelatedProcessStartTicks);

            // End-to-end with HqStateAggregator: NEITHER proc1 nor proc2 is falsely hijacked!
            var aggA = new HqStateAggregator(timeProvider: () => baseTime.AddSeconds(30));
            var procOld = new AgentSnapshot("proc-claude-1001", "Claude Old", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 1001, ProcessStartTicks: 111000L);
            var procNew = new AgentSnapshot("proc-claude-2002", "Claude New", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 2002, ProcessStartTicks: 222000L);
            aggA.UpdateProcessAgents(new[] { procOld, procNew });
            aggA.UpdateSessionLogActivity(collectorA.GetCurrentSnapshots());

            Assert.Equal(AgentState.Idle, aggA.CurrentState.Agents.Single(a => a.Id == "proc-claude-1001").State);
            Assert.Equal(AgentState.Idle, aggA.CurrentState.Agents.Single(a => a.Id == "proc-claude-2002").State);
            Assert.Single(aggA.CurrentState.Agents, a => a.Source == "session-log" && a.State == AgentState.Working);

            // (b) Freshest activity CARRYING its own (pid_new, start_new) tuple + later physical line carrying
            //     older (pid_old, start_old) metadata -> activity KEEPS (pid_new, start_new)!
            string ownTupleLog = Path.Combine(tempDir, "own-tuple-precedence.jsonl");
            File.WriteAllText(ownTupleLog,
                "{\"timestamp\":\"2026-04-10T16:20:30Z\",\"pid\":2002,\"process_start_ticks\":222000,\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"pytest\"}]}}\n" +
                "{\"timestamp\":\"2026-04-10T16:20:05Z\",\"type\":\"session_meta\",\"pid\":1001,\"process_start_ticks\":111000}\n");

            using var collectorB = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => baseTime.AddSeconds(35));

            var derivedOwn = collectorB.ProcessFileDelta(ownTupleLog, "claude", "Claude");
            Assert.NotNull(derivedOwn);
            Assert.Equal(AgentState.Working, derivedOwn!.State);
            Assert.Equal("TEST", derivedOwn.DerivedActionToken, ignoreCase: true);
            Assert.Equal(2002, derivedOwn.CorrelatedProcessId);
            Assert.Equal(222000L, derivedOwn.CorrelatedProcessStartTicks);

            // Even a subsequent delayed older metadata delta MUST NOT overwrite or clear the activity's own tuple!
            File.AppendAllText(ownTupleLog,
                "{\"timestamp\":\"2026-04-10T16:20:01Z\",\"type\":\"session_meta\",\"pid\":1001,\"process_start_ticks\":111000}\n");
            var derivedOwnAfterDelayed = collectorB.ProcessFileDelta(ownTupleLog, "claude", "Claude");
            Assert.NotNull(derivedOwnAfterDelayed);
            Assert.Equal(2002, derivedOwnAfterDelayed!.CorrelatedProcessId);
            Assert.Equal(222000L, derivedOwnAfterDelayed.CorrelatedProcessStartTicks);

            var aggB = new HqStateAggregator(timeProvider: () => baseTime.AddSeconds(35));
            aggB.UpdateProcessAgents(new[] { procOld, procNew });
            aggB.UpdateSessionLogActivity(collectorB.GetCurrentSnapshots());

            Assert.Equal(AgentState.Idle, aggB.CurrentState.Agents.Single(a => a.Id == "proc-claude-1001").State);
            var enrichedNew = aggB.CurrentState.Agents.Single(a => a.Id == "proc-claude-2002");
            Assert.Equal(AgentState.Working, enrichedNew.State);
            Assert.Equal("TEST", enrichedNew.ToolName, ignoreCase: true);

            // (c) Cross-delta ambiguity: Delta 1 correlates inherited identity -> Delta 2 introduces conflicting tuple -> clears inherited correlation!
            string crossDeltaLog = Path.Combine(tempDir, "cross-delta-ambiguous.jsonl");
            File.WriteAllText(crossDeltaLog,
                "{\"timestamp\":\"2026-04-10T16:20:20Z\",\"type\":\"session_meta\",\"pid\":2002,\"process_start_ticks\":222000}\n" +
                "{\"timestamp\":\"2026-04-10T16:20:22Z\",\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n");

            using var collectorC = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => baseTime.AddSeconds(35));

            var delta1 = collectorC.ProcessFileDelta(crossDeltaLog, "claude", "Claude");
            Assert.NotNull(delta1);
            Assert.Equal(2002, delta1!.CorrelatedProcessId);
            Assert.Equal(222000L, delta1.CorrelatedProcessStartTicks);

            File.AppendAllText(crossDeltaLog,
                "{\"timestamp\":\"2026-04-10T16:20:05Z\",\"type\":\"session_meta\",\"pid\":1001,\"process_start_ticks\":111000}\n");
            var delta2 = collectorC.ProcessFileDelta(crossDeltaLog, "claude", "Claude");
            Assert.NotNull(delta2);
            Assert.Null(delta2!.CorrelatedProcessId);
            Assert.Null(delta2.CorrelatedProcessStartTicks);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HookInstaller_OpenCodeProbe_StrictAuthoritativeStdoutRejectsDiagnosticsMultiVersionAndStderrOnly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kagedesk-oc-strict-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string openCodeDir = Path.Combine(fakeHome, ".config", "opencode");
        string pluginDir = Path.Combine(openCodeDir, "plugins");
        string pluginPath = Path.Combine(pluginDir, "kagedesk-plugin.js");
        Directory.CreateDirectory(openCodeDir);

        try
        {
            // 1. Diagnostic prose, ambiguous multi-line/multi-version stdout, and trailing commentary MUST all classify as ProbeFailedOrUnavailable
            //    and return non-Installed with ZERO plugins/ directory or plugin file created!
            string[] invalidOutputs =
            {
                "OpenCode version unavailable; plugin API 2.1.0 required",
                "opencode 1.18.29\nopencode 2.1.0",
                "1.18.29\r\n2.1.0",
                "warning: plugin api deprecated\n2.1.0",
                "opencode 2.1.0 (plugin API 1.0.0)",
                "opencode\n2.1.0",
                "v2.1"
            };

            foreach (string raw in invalidOutputs)
            {
                var classified = HookInstaller.ClassifyOpenCodeVersionOutput(raw);
                Assert.Equal(OpenCodeVersionProbeOutcome.ProbeFailedOrUnavailable, classified.Outcome);
                Assert.Null(classified.ApiVersion);

                var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319, openCodeVersionProbe: () => raw);
                var res = installer.InstallOpenCode();
                Assert.Equal(HookActionStatus.Unsupported, res.Status);
                Assert.NotEqual(HookActionStatus.Installed, res.Status);
                Assert.False(Directory.Exists(pluginDir), $"Invalid output '{raw}' must NOT create plugins/ directory.");
                Assert.False(File.Exists(pluginPath), $"Invalid output '{raw}' must NOT write kagedesk-plugin.js.");
            }

            // 2. Real child process emitting diagnostic prose on stdout -> ProbeFailedOrUnavailable
            var diagProcRes = await HookInstaller.ProbeOpenCodeExecutableVersionAsync(
                "node",
                new[] { "-e", "process.stdout.write('OpenCode version unavailable; plugin API 2.1.0 required\\n');" },
                TimeSpan.FromSeconds(5));
            Assert.Equal(OpenCodeVersionProbeOutcome.ProbeFailedOrUnavailable, diagProcRes.Outcome);

            // 3. Real child process emitting ambiguous multi-version stdout -> ProbeFailedOrUnavailable
            var multiProcRes = await HookInstaller.ProbeOpenCodeExecutableVersionAsync(
                "node",
                new[] { "-e", "process.stdout.write('opencode 1.18.29\\nopencode 2.1.0\\n');" },
                TimeSpan.FromSeconds(5));
            Assert.Equal(OpenCodeVersionProbeOutcome.ProbeFailedOrUnavailable, multiProcRes.Outcome);

            // 4. Real child process emitting stderr-only "2.1.0" (empty stdout, exit 0) -> MUST classify as ProbeFailedOrUnavailable,
            //    return non-Installed, and create ZERO plugins/ directory or plugin file!
            var stderrOnlyRes = await HookInstaller.ProbeOpenCodeExecutableVersionAsync(
                "node",
                new[] { "-e", "process.stderr.write('2.1.0\\n');" },
                TimeSpan.FromSeconds(5));
            Assert.Equal(OpenCodeVersionProbeOutcome.ProbeFailedOrUnavailable, stderrOnlyRes.Outcome);
            Assert.Null(stderrOnlyRes.ApiVersion);

            var stderrInstaller = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319)
            {
                OpenCodeProbeOverrideForTest = ct => HookInstaller.ProbeOpenCodeExecutableVersion(
                    "node",
                    new[] { "-e", "process.stderr.write('2.1.0\\n');" },
                    TimeSpan.FromSeconds(5),
                    ct)
            };
            var stderrInstallResult = stderrInstaller.InstallOpenCode();
            Assert.Equal(HookActionStatus.Unsupported, stderrInstallResult.Status);
            Assert.NotEqual(HookActionStatus.Installed, stderrInstallResult.Status);
            Assert.False(Directory.Exists(pluginDir), "Stderr-only OpenCode output must NOT create plugins/ directory.");
            Assert.False(File.Exists(pluginPath), "Stderr-only OpenCode output must NOT write kagedesk-plugin.js.");

            // 5. Positive sanity checks: strict anchored single-line CLI formats still parse cleanly
            Assert.Equal(OpenCodeVersionProbeOutcome.DetectedV1, HookInstaller.ClassifyOpenCodeVersionOutput("opencode 1.18.29\n").Outcome);
            Assert.Equal(OpenCodeVersionProbeOutcome.DetectedV2, HookInstaller.ClassifyOpenCodeVersionOutput("  v2.1.0-beta.1+build.7\r\n").Outcome);
            Assert.Equal(OpenCodeVersionProbeOutcome.DetectedV2, HookInstaller.ClassifyOpenCodeVersionOutput("2.1.0").Outcome);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SessionLogActivityCollector_PartialIdentityFields_StrictlyVetoContradictingDonatedTuplesAndLateBackfill()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kagedesk-partial-veto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var baseTime = new DateTimeOffset(2026, 4, 10, 16, 30, 0, TimeSpan.Zero);
            var procA = new AgentSnapshot("proc-claude-1001", "Claude A", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 1001, ProcessStartTicks: 111000L);
            var procB = new AgentSnapshot("proc-claude-2002", "Claude B", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 2002, ProcessStartTicks: 222000L);

            // (a) Cross-delta veto: Delta 1 records sole verified tuple (PID A=1001, start A=111000);
            //     Delta 2 appends activity naming PID B=2002 (without start_ticks) ->
            //     MUST veto (PID A, start A), NEVER update process A (or B), and stay on separate unverified session tile!
            string crossDeltaLog = Path.Combine(tempDir, "cross-delta-partial-veto.jsonl");
            File.WriteAllText(crossDeltaLog,
                "{\"timestamp\":\"2026-04-10T16:30:10Z\",\"type\":\"session_meta\",\"pid\":1001,\"process_start_ticks\":111000}\n");

            using var collectorA = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => baseTime.AddSeconds(25));

            _ = collectorA.ProcessFileDelta(crossDeltaLog, "claude", "Claude");

            File.AppendAllText(crossDeltaLog,
                "{\"timestamp\":\"2026-04-10T16:30:20Z\",\"pid\":2002,\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n");

            var derivedCross = collectorA.ProcessFileDelta(crossDeltaLog, "claude", "Claude");
            Assert.NotNull(derivedCross);
            Assert.Equal(AgentState.Working, derivedCross!.State);
            Assert.Equal("CODE", derivedCross.DerivedActionToken, ignoreCase: true);
            Assert.Null(derivedCross.CorrelatedProcessId);
            Assert.Null(derivedCross.CorrelatedProcessStartTicks);

            var aggA = new HqStateAggregator(timeProvider: () => baseTime.AddSeconds(25));
            aggA.UpdateProcessAgents(new[] { procA, procB });
            aggA.UpdateSessionLogActivity(collectorA.GetCurrentSnapshots());

            Assert.Equal(AgentState.Idle, aggA.CurrentState.Agents.Single(a => a.Id == "proc-claude-1001").State);
            Assert.Equal(AgentState.Idle, aggA.CurrentState.Agents.Single(a => a.Id == "proc-claude-2002").State);
            var separateTileA = Assert.Single(aggA.CurrentState.Agents, a => a.Source == "session-log");
            Assert.Equal(AgentState.Working, separateTileA.State);
            Assert.Equal("CODE", separateTileA.ToolName, ignoreCase: true);

            // (b) Late metadata backfill contradicting partial PID:
            //     Delta 1 appends activity naming PID B=2002 (without start_ticks);
            //     Delta 2 appends late session_meta with verified (PID A=1001, start A=111000) ->
            //     MUST veto late backfill of (PID A, start A), NEVER update process A, and keep separate session tile!
            string lateBackfillLog = Path.Combine(tempDir, "late-backfill-partial-veto.jsonl");
            File.WriteAllText(lateBackfillLog,
                "{\"timestamp\":\"2026-04-10T16:30:20Z\",\"pid\":2002,\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"pytest\"}]}}\n");

            using var collectorB = new SessionLogActivityCollector(
                watchRoots: new[] { new SessionWatchRoot("claude", "Claude", tempDir) },
                clock: () => baseTime.AddSeconds(25));

            var derivedBeforeMeta = collectorB.ProcessFileDelta(lateBackfillLog, "claude", "Claude");
            Assert.NotNull(derivedBeforeMeta);
            Assert.Null(derivedBeforeMeta!.CorrelatedProcessId);
            Assert.Null(derivedBeforeMeta.CorrelatedProcessStartTicks);

            File.AppendAllText(lateBackfillLog,
                "{\"timestamp\":\"2026-04-10T16:30:05Z\",\"type\":\"session_meta\",\"pid\":1001,\"process_start_ticks\":111000}\n");

            var derivedAfterMeta = collectorB.ProcessFileDelta(lateBackfillLog, "claude", "Claude");
            Assert.NotNull(derivedAfterMeta);
            Assert.Equal(AgentState.Working, derivedAfterMeta!.State);
            Assert.Equal("TEST", derivedAfterMeta.DerivedActionToken, ignoreCase: true);
            Assert.Null(derivedAfterMeta.CorrelatedProcessId);
            Assert.Null(derivedAfterMeta.CorrelatedProcessStartTicks);

            var aggB = new HqStateAggregator(timeProvider: () => baseTime.AddSeconds(25));
            aggB.UpdateProcessAgents(new[] { procA, procB });
            aggB.UpdateSessionLogActivity(collectorB.GetCurrentSnapshots());

            Assert.Equal(AgentState.Idle, aggB.CurrentState.Agents.Single(a => a.Id == "proc-claude-1001").State);
            Assert.Equal(AgentState.Idle, aggB.CurrentState.Agents.Single(a => a.Id == "proc-claude-2002").State);
            var separateTileB = Assert.Single(aggB.CurrentState.Agents, a => a.Source == "session-log");
            Assert.Equal(AgentState.Working, separateTileB.State);
            Assert.Equal("TEST", separateTileB.ToolName, ignoreCase: true);

            // (c) Same-batch partial PID veto AND partial process_start_ticks veto
            string sameBatchPidVeto =
                "{\"timestamp\":\"2026-04-10T16:30:10Z\",\"type\":\"session_meta\",\"pid\":1001,\"process_start_ticks\":111000}\n" +
                "{\"timestamp\":\"2026-04-10T16:30:20Z\",\"pid\":2002,\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n";
            var sameBatchPidDerived = SessionLogActivityCollector.ParseDerivedActivityFromTail("claude", "Claude", Encoding.UTF8.GetBytes(sameBatchPidVeto), baseTime.AddSeconds(25));
            Assert.NotNull(sameBatchPidDerived);
            Assert.Null(sameBatchPidDerived!.CorrelatedProcessId);
            Assert.Null(sameBatchPidDerived.CorrelatedProcessStartTicks);

            string sameBatchTicksVeto =
                "{\"timestamp\":\"2026-04-10T16:30:10Z\",\"type\":\"session_meta\",\"pid\":1001,\"process_start_ticks\":111000}\n" +
                "{\"timestamp\":\"2026-04-10T16:30:20Z\",\"process_start_ticks\":222000,\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"}]}}\n";
            var sameBatchTicksDerived = SessionLogActivityCollector.ParseDerivedActivityFromTail("claude", "Claude", Encoding.UTF8.GetBytes(sameBatchTicksVeto), baseTime.AddSeconds(25));
            Assert.NotNull(sameBatchTicksDerived);
            Assert.Null(sameBatchTicksDerived!.CorrelatedProcessId);
            Assert.Null(sameBatchTicksDerived.CorrelatedProcessStartTicks);

            // (d) HqStateAggregator also refuses partial start_ticks-only snapshots without falling through to Repo/WorktreeId
            var aggC = new HqStateAggregator(timeProvider: () => baseTime.AddSeconds(25));
            var procWithRepo = procA with { Repo = "kagedesk", WorktreeId = "wt-1" };
            aggC.UpdateProcessAgents(new[] { procWithRepo });
            aggC.UpdateSessionLogActivity(new[]
            {
                new AgentSnapshot(
                    Id: "session:claude:partialticks",
                    Name: "Claude",
                    Role: AgentRole.External,
                    SpriteKey: "generic",
                    State: AgentState.Working,
                    ToolName: "CODE",
                    Repo: "kagedesk",
                    WorktreeId: "wt-1",
                    LastActivityAt: baseTime.AddSeconds(20),
                    UpdatedAt: baseTime.AddSeconds(20),
                    Source: "session-log",
                    ProcessId: null,
                    ProcessStartTicks: 222000L)
            });

            Assert.Equal(AgentState.Idle, aggC.CurrentState.Agents.Single(a => a.Id == "proc-claude-1001").State);
            Assert.Single(aggC.CurrentState.Agents, a => a.Source == "session-log" && a.State == AgentState.Working);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
