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
            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319, clock: () => fixedTime, codexHomeDir: codexDir);

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

            var installer = new HookInstaller(fakeHome, fakeAppData, bridgePort: 4319);
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
                () => collector.GetCurrentSnapshots().Any(s => s.Id == "session:claude" && string.Equals(s.ToolName, "test", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(3)), "Overflow reconciliation should pick up overflow.jsonl.");

            // Missing root created after startup attaches via bounded reconciliation
            Directory.CreateDirectory(delayedRoot);
            collector.ReconcileMissingRootsNow();
            Assert.Equal(2, collector.ActiveWatcherCount);

            // Deletion invalidates state cleanly
            File.Delete(logPath);
            File.Delete(overflowFile);
            Assert.True(SpinWait.SpinUntil(
                () => !collector.GetCurrentSnapshots().Any(s => s.Id == "session:claude"),
                TimeSpan.FromSeconds(3)), "Deleting all tracked log files should clear session:claude snapshot.");
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
    public void Aggregator_SessionLogOverridesSingleUnambiguousProcessWhilePreservingMetadata()
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
            Source: "session-log",
            UpdatedAt: now);

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

    [Fact]
    public void Aggregator_TwoConcurrentSameProviderProcesses_DoesNotClobberUnrelatedProcess()
    {
        var now = new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);
        var agg = new HqStateAggregator(timeProvider: () => now);

        var proc1 = new AgentSnapshot("proc-claude-1001", "Claude #1", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 1001, ProcessStartTicks: 111L);
        var proc2 = new AgentSnapshot("proc-claude-2002", "Claude #2", AgentRole.External, "claude", AgentState.Idle, Source: "process", ProcessId: 2002, ProcessStartTicks: 222L);
        agg.UpdateProcessAgents(new[] { proc1, proc2 });

        // 1. Uncorrelated session-log observation (no PID/Repo) MUST NOT clobber proc1 or proc2; displays separate tile instead
        var uncorrelatedLog = new AgentSnapshot("sessionlog-claude", "Claude", AgentRole.External, "claude", AgentState.Working, ToolName: "CODE", Source: "session-log", UpdatedAt: now);
        agg.UpdateSessionLogActivity(new[] { uncorrelatedLog });

        var agents1 = agg.CurrentState.Agents;
        Assert.Equal(AgentState.Idle, agents1.Single(a => a.Id == "proc-claude-1001").State);
        Assert.Equal(AgentState.Idle, agents1.Single(a => a.Id == "proc-claude-2002").State);
        Assert.Contains(agents1, a => a.Source == "session-log" && a.State == AgentState.Working);

        // 2. Verified PID-correlated observation updates ONLY proc-claude-1001, leaving proc-claude-2002 Idle!
        var correlatedLog = new AgentSnapshot("sessionlog-claude-1001", "Claude", AgentRole.External, "claude", AgentState.Working, ToolName: "TEST", Source: "session-log", ProcessId: 1001, UpdatedAt: now);
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
        var logAgent = new AgentSnapshot("sessionlog-claude", "Claude", AgentRole.External, "claude", AgentState.Working, ToolName: "CODE", Source: "session-log", UpdatedAt: obsTime);
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
}
