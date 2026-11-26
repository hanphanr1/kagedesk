using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KageDesk.Core.Hooks;

public enum HookTargetKind
{
    ClaudeCode,
    Codex,
    OpenCode
}

public enum HookActionStatus
{
    Installed,
    AlreadyInstalled,
    Uninstalled,
    AlreadyRemoved,
    SkippedNotPresent,
    UnsupportedLayout,
    MalformedConfig,
    Failed
}

public sealed record HookTargetResult(
    HookTargetKind Target,
    string ConfigPath,
    HookActionStatus Status,
    string Message,
    string? BackupPath = null
);

public sealed record HookOperationSummary(
    bool IsInstall,
    string EmitterScriptPath,
    IReadOnlyList<HookTargetResult> Results
)
{
    public bool AllSucceeded => Results.All(r => r.Status is
        HookActionStatus.Installed or
        HookActionStatus.AlreadyInstalled or
        HookActionStatus.Uninstalled or
        HookActionStatus.AlreadyRemoved or
        HookActionStatus.SkippedNotPresent);

    public int ChangedCount => Results.Count(r => r.Status is HookActionStatus.Installed or HookActionStatus.Uninstalled);
}

public sealed class HookInstaller
{
    private const string EmitterMarkerPs1 = "kagedesk-emit.ps1";
    private const string OpenCodePluginFileName = "kagedesk-plugin.js";
    private const string OpenCodePluginOwnershipMarker = "// kagedesk-managed-opencode-plugin:v1";
    private const string ClaudeApprovalNotificationMatcher = "permission_prompt|elicitation_dialog";
    private const string CodexTrustNote = "Codex requires interactive trust approval for workspace/hooks before hooks execute.";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static readonly char[] ForbiddenShellChars =
    {
        '\0', '\r', '\n', '"', ';', '|', '&', '<', '>', '`', '$', '%'
    };

    private readonly string _userProfileDir;
    private readonly string _appDataDir;
    private readonly string? _codexHomeDir;
    private readonly int _bridgePort;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Internal test seam invoked immediately before snapshot revalidation & commit under the per-target lock.
    /// </summary>
    internal Action<string>? BeforeCommitHookForTest { get; set; }

    public HookInstaller(
        string? userProfileDir = null,
        string? appDataDir = null,
        int bridgePort = 4319,
        Func<DateTimeOffset>? clock = null,
        string? codexHomeDir = null)
    {
        _userProfileDir = string.IsNullOrWhiteSpace(userProfileDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileDir;
        _appDataDir = string.IsNullOrWhiteSpace(appDataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KageDesk")
            : appDataDir;
        _codexHomeDir = string.IsNullOrWhiteSpace(codexHomeDir) ? null : codexHomeDir;
        _bridgePort = bridgePort is >= 1 and <= 65535 ? bridgePort : 4319;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string DefaultEmitterScriptPath => Path.Combine(_appDataDir, "hooks", EmitterMarkerPs1);

    public string ResolveCodexHomeDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_codexHomeDir))
        {
            return _codexHomeDir;
        }

        string? envCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(envCodexHome))
        {
            return envCodexHome.Trim();
        }

        return Path.Combine(_userProfileDir, ".codex");
    }

    public HookOperationSummary InstallAll(string? explicitEmitterScriptPath = null)
    {
        string reportedEmitterPath = string.IsNullOrWhiteSpace(explicitEmitterScriptPath)
            ? DefaultEmitterScriptPath
            : explicitEmitterScriptPath;

        var results = new List<HookTargetResult>(3)
        {
            InstallClaudeCodeWithEmitterEnsure(explicitEmitterScriptPath),
            InstallCodexWithEmitterEnsure(explicitEmitterScriptPath),
            InstallOpenCode()
        };

        return new HookOperationSummary(true, reportedEmitterPath, results);
    }

    public HookOperationSummary UninstallAll(string? explicitEmitterScriptPath = null)
    {
        string emitterPath = string.IsNullOrWhiteSpace(explicitEmitterScriptPath)
            ? DefaultEmitterScriptPath
            : explicitEmitterScriptPath;

        var results = new List<HookTargetResult>(3)
        {
            UninstallClaudeCode(emitterPath),
            UninstallCodex(emitterPath),
            UninstallOpenCode()
        };

        return new HookOperationSummary(false, emitterPath, results);
    }

    public string EnsureEmitterScript(string? explicitEmitterScriptPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitEmitterScriptPath))
        {
            return ValidateEmitterScriptPath(explicitEmitterScriptPath);
        }

        string targetPath = ValidateEmitterScriptPath(DefaultEmitterScriptPath);
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            EnsureDirectorySafe(dir);
        }

        WithTargetLock(targetPath, () =>
        {
            ValidateNoReparsePointsInPath(targetPath);
            byte[] desiredBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(EmbeddedEmitterScript);
            if (File.Exists(targetPath))
            {
                byte[] existing = File.ReadAllBytes(targetPath);
                if (existing.AsSpan().SequenceEqual(desiredBytes))
                {
                    return;
                }

                WriteBytesAtomicWithReplace(targetPath, desiredBytes, backupPath: null, fileExisted: true);
            }
            else
            {
                WriteBytesAtomicWithReplace(targetPath, desiredBytes, backupPath: null, fileExisted: false);
            }
        });

        return targetPath;
    }

    private HookTargetResult InstallClaudeCodeWithEmitterEnsure(string? explicitEmitterScriptPath)
    {
        string settingsPath = Path.Combine(_userProfileDir, ".claude", "settings.json");
        try
        {
            string emitterPath = EnsureEmitterScript(explicitEmitterScriptPath);
            return InstallClaudeCode(emitterPath);
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.ClaudeCode, settingsPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    private HookTargetResult InstallCodexWithEmitterEnsure(string? explicitEmitterScriptPath)
    {
        string codexDir;
        try
        {
            codexDir = ResolveCodexHomeDirectory();
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.Codex, Path.Combine(_userProfileDir, ".codex", "hooks.json"), HookActionStatus.Failed, $"Failed: {ex.Message}");
        }

        string hooksPath = Path.Combine(codexDir, "hooks.json");
        try
        {
            ValidateSafePathSyntax(codexDir, "Codex home directory");
            if (!Directory.Exists(codexDir))
            {
                return new HookTargetResult(
                    HookTargetKind.Codex,
                    hooksPath,
                    HookActionStatus.SkippedNotPresent,
                    $"Codex config directory not detected ({codexDir}).");
            }

            string emitterPath = EnsureEmitterScript(explicitEmitterScriptPath);
            return InstallCodex(emitterPath);
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.Codex, hooksPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult InstallClaudeCode(string emitterScriptPath)
    {
        string claudeDir = Path.Combine(_userProfileDir, ".claude");
        string settingsPath = Path.Combine(claudeDir, "settings.json");

        try
        {
            string validatedEmitter = ValidateEmitterScriptPath(emitterScriptPath);
            EnsureDirectorySafe(claudeDir);
            return InstallHookJsonFile(
                HookTargetKind.ClaudeCode,
                settingsPath,
                agentId: "claude",
                emitterScriptPath: validatedEmitter,
                specs: GetClaudeHookSpecs(),
                extraNote: null);
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.ClaudeCode, settingsPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult UninstallClaudeCode(string? emitterScriptPath = null)
    {
        string settingsPath = Path.Combine(_userProfileDir, ".claude", "settings.json");
        try
        {
            string validatedEmitter = ValidateEmitterScriptPath(string.IsNullOrWhiteSpace(emitterScriptPath) ? DefaultEmitterScriptPath : emitterScriptPath);
            return UninstallHookJsonFile(
                HookTargetKind.ClaudeCode,
                settingsPath,
                agentId: "claude",
                emitterScriptPath: validatedEmitter,
                specs: GetClaudeHookSpecs());
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.ClaudeCode, settingsPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult InstallCodex(string emitterScriptPath)
    {
        string codexDir = ResolveCodexHomeDirectory();
        string hooksPath = Path.Combine(codexDir, "hooks.json");

        try
        {
            ValidateSafePathSyntax(codexDir, "Codex home directory");
            ValidateNoReparsePointsInPath(codexDir);

            if (!Directory.Exists(codexDir))
            {
                return new HookTargetResult(
                    HookTargetKind.Codex,
                    hooksPath,
                    HookActionStatus.SkippedNotPresent,
                    $"Codex config directory not detected ({codexDir}).");
            }

            string validatedEmitter = ValidateEmitterScriptPath(emitterScriptPath);
            return InstallHookJsonFile(
                HookTargetKind.Codex,
                hooksPath,
                agentId: "codex",
                emitterScriptPath: validatedEmitter,
                specs: GetCodexHookSpecs(),
                extraNote: CodexTrustNote);
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.Codex, hooksPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult UninstallCodex(string? emitterScriptPath = null)
    {
        string codexDir = ResolveCodexHomeDirectory();
        string hooksPath = Path.Combine(codexDir, "hooks.json");

        try
        {
            ValidateSafePathSyntax(codexDir, "Codex home directory");
            string validatedEmitter = ValidateEmitterScriptPath(string.IsNullOrWhiteSpace(emitterScriptPath) ? DefaultEmitterScriptPath : emitterScriptPath);
            return UninstallHookJsonFile(
                HookTargetKind.Codex,
                hooksPath,
                agentId: "codex",
                emitterScriptPath: validatedEmitter,
                specs: GetCodexHookSpecs());
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.Codex, hooksPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult InstallOpenCode(string? unusedEmitterScriptPath = null)
    {
        string configDir = Path.Combine(_userProfileDir, ".config", "opencode");
        string legacyDir = Path.Combine(_userProfileDir, ".opencode");
        string pluginDir;

        if (Directory.Exists(configDir))
        {
            pluginDir = Path.Combine(configDir, "plugins");
        }
        else if (Directory.Exists(legacyDir))
        {
            pluginDir = Path.Combine(legacyDir, "plugins");
        }
        else
        {
            return new HookTargetResult(
                HookTargetKind.OpenCode,
                Path.Combine(configDir, "plugins", OpenCodePluginFileName),
                HookActionStatus.SkippedNotPresent,
                "OpenCode config directory not detected.");
        }

        string pluginPath = Path.Combine(pluginDir, OpenCodePluginFileName);

        try
        {
            ValidateSafePathSyntax(pluginPath, "OpenCode plugin path");
            EnsureDirectorySafe(pluginDir);

            return WithTargetLock(pluginPath, () =>
            {
                ValidateNoReparsePointsInPath(pluginPath);

                FileSnapshot before = CaptureSnapshot(pluginPath);
                string desiredContent = BuildOpenCodePluginSource(_bridgePort);
                byte[] desiredBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(desiredContent);

                if (before.Exists)
                {
                    byte[] existingBytes = File.ReadAllBytes(pluginPath);
                    string existingText = Encoding.UTF8.GetString(existingBytes);
                    if (!IsOwnedOpenCodePlugin(existingText))
                    {
                        return new HookTargetResult(
                            HookTargetKind.OpenCode,
                            pluginPath,
                            HookActionStatus.UnsupportedLayout,
                            "Existing OpenCode plugin file is not owned by KageDesk; refused to overwrite.");
                    }

                    if (existingBytes.AsSpan().SequenceEqual(desiredBytes))
                    {
                        return new HookTargetResult(
                            HookTargetKind.OpenCode,
                            pluginPath,
                            HookActionStatus.AlreadyInstalled,
                            "KageDesk OpenCode plugin already installed and up to date (opencode.json untouched).");
                    }
                }

                BeforeCommitHookForTest?.Invoke(pluginPath);

                if (!MatchesSnapshot(pluginPath, before))
                {
                    return new HookTargetResult(
                        HookTargetKind.OpenCode,
                        pluginPath,
                        HookActionStatus.Failed,
                        "OpenCode plugin file was modified concurrently; aborted to prevent clobbering changes.");
                }

                string? backupPath = before.Exists ? BuildUniqueBackupPath(pluginPath) : null;
                WriteBytesAtomicWithReplace(pluginPath, desiredBytes, backupPath, before.Exists);

                return new HookTargetResult(
                    HookTargetKind.OpenCode,
                    pluginPath,
                    HookActionStatus.Installed,
                    backupPath != null
                        ? $"Installed standalone KageDesk OpenCode plugin (backup saved to {Path.GetFileName(backupPath)}; opencode.json untouched)."
                        : "Installed standalone KageDesk OpenCode plugin (opencode.json untouched).",
                    backupPath);
            });
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.OpenCode, pluginPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult UninstallOpenCode()
    {
        string primaryDir = Path.Combine(_userProfileDir, ".config", "opencode");
        string primaryPlugin = Path.Combine(primaryDir, "plugins", OpenCodePluginFileName);
        string legacyDir = Path.Combine(_userProfileDir, ".opencode");
        string legacyPlugin = Path.Combine(legacyDir, "plugins", OpenCodePluginFileName);

        string targetPlugin;
        if (File.Exists(primaryPlugin))
        {
            targetPlugin = primaryPlugin;
        }
        else if (File.Exists(legacyPlugin))
        {
            targetPlugin = legacyPlugin;
        }
        else if (Directory.Exists(primaryDir) || Directory.Exists(legacyDir))
        {
            targetPlugin = Directory.Exists(primaryDir) ? primaryPlugin : legacyPlugin;
            return new HookTargetResult(
                HookTargetKind.OpenCode,
                targetPlugin,
                HookActionStatus.AlreadyRemoved,
                "No KageDesk OpenCode plugin found.");
        }
        else
        {
            return new HookTargetResult(
                HookTargetKind.OpenCode,
                primaryPlugin,
                HookActionStatus.SkippedNotPresent,
                "OpenCode config directory not detected.");
        }

        try
        {
            ValidateSafePathSyntax(targetPlugin, "OpenCode plugin path");

            return WithTargetLock(targetPlugin, () =>
            {
                ValidateNoReparsePointsInPath(targetPlugin);
                FileSnapshot before = CaptureSnapshot(targetPlugin);
                if (!before.Exists)
                {
                    return new HookTargetResult(
                        HookTargetKind.OpenCode,
                        targetPlugin,
                        HookActionStatus.AlreadyRemoved,
                        "No KageDesk OpenCode plugin found.");
                }

                string existingText = File.ReadAllText(targetPlugin, Encoding.UTF8);
                if (!IsOwnedOpenCodePlugin(existingText))
                {
                    return new HookTargetResult(
                        HookTargetKind.OpenCode,
                        targetPlugin,
                        HookActionStatus.UnsupportedLayout,
                        "Existing OpenCode plugin file is not owned by KageDesk; refused to remove.");
                }

                BeforeCommitHookForTest?.Invoke(targetPlugin);

                if (!MatchesSnapshot(targetPlugin, before))
                {
                    return new HookTargetResult(
                        HookTargetKind.OpenCode,
                        targetPlugin,
                        HookActionStatus.Failed,
                        "OpenCode plugin file was modified concurrently; aborted uninstall.");
                }

                File.Delete(targetPlugin);
                return new HookTargetResult(
                    HookTargetKind.OpenCode,
                    targetPlugin,
                    HookActionStatus.Uninstalled,
                    "Removed standalone KageDesk OpenCode plugin (opencode.json untouched).");
            });
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.OpenCode, targetPlugin, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    private sealed record HookEventSpec(
        string EventKey,
        string EventType,
        string Message,
        string? Matcher = null,
        bool AsyncDispatch = false
    );

    private static HookEventSpec[] GetClaudeHookSpecs() =>
    [
        new HookEventSpec("PreToolUse", "tool_start", "Tool execution started", Matcher: null, AsyncDispatch: true),
        new HookEventSpec("PostToolUse", "tool_complete", "Tool execution completed", Matcher: null, AsyncDispatch: true),
        new HookEventSpec("Notification", "approval", "Approval requested", Matcher: ClaudeApprovalNotificationMatcher, AsyncDispatch: true),
        new HookEventSpec("Stop", "turn_done", "Agent turn completed", Matcher: null, AsyncDispatch: true)
    ];

    private static HookEventSpec[] GetCodexHookSpecs() =>
    [
        // Codex officially supports PreToolUse, PostToolUse, Stop (NOT Notification).
        new HookEventSpec("PreToolUse", "tool_start", "Tool execution started", Matcher: null, AsyncDispatch: false),
        new HookEventSpec("PostToolUse", "tool_complete", "Tool execution completed", Matcher: null, AsyncDispatch: false),
        new HookEventSpec("Stop", "turn_done", "Agent turn completed", Matcher: null, AsyncDispatch: false)
    ];

    private HookTargetResult InstallHookJsonFile(
        HookTargetKind target,
        string configPath,
        string agentId,
        string emitterScriptPath,
        HookEventSpec[] specs,
        string? extraNote)
    {
        ValidateSafePathSyntax(configPath, "Target config path");

        return WithTargetLock(configPath, () =>
        {
            ValidateNoReparsePointsInPath(configPath);
            FileSnapshot before = CaptureSnapshot(configPath);

            var loadResult = LoadStrictJsonObject(configPath, before);
            if (loadResult.ErrorStatus.HasValue)
            {
                return new HookTargetResult(target, configPath, loadResult.ErrorStatus.Value, loadResult.ErrorMessage!);
            }

            JsonObject root = loadResult.Root!;

            // Preflight "hooks" property: distinguish absent vs explicit null vs non-object
            JsonObject? existingHooks = null;
            bool hooksWasAbsent = false;
            if (root.TryGetPropertyValue("hooks", out JsonNode? hooksNode))
            {
                if (hooksNode == null)
                {
                    return new HookTargetResult(
                        target,
                        configPath,
                        HookActionStatus.UnsupportedLayout,
                        "Existing 'hooks' property is explicit null; refused to modify user config.");
                }

                if (hooksNode is not JsonObject obj)
                {
                    return new HookTargetResult(
                        target,
                        configPath,
                        HookActionStatus.UnsupportedLayout,
                        "Existing 'hooks' property is not a JSON object; refused to modify user config.");
                }

                existingHooks = obj;

                // Preflight ALL managed event keys before mutating anything
                foreach (var spec in specs)
                {
                    if (existingHooks.TryGetPropertyValue(spec.EventKey, out JsonNode? eventNode))
                    {
                        if (eventNode == null)
                        {
                            return new HookTargetResult(
                                target,
                                configPath,
                                HookActionStatus.UnsupportedLayout,
                                $"Managed hook event '{spec.EventKey}' is explicit null; refused to modify user config.");
                        }

                        if (eventNode is not JsonArray)
                        {
                            return new HookTargetResult(
                                target,
                                configPath,
                                HookActionStatus.UnsupportedLayout,
                                $"Managed hook event '{spec.EventKey}' is not a JSON array; refused to modify user config.");
                        }
                    }
                }
            }
            else
            {
                hooksWasAbsent = true;
            }

            JsonObject hooksObj = existingHooks ?? new JsonObject();
            bool modified = false;

            foreach (var spec in specs)
            {
                string desiredCommand = BuildHookCommand(emitterScriptPath, spec.EventType, agentId, spec.Message);
                if (UpsertHookEvent(hooksObj, spec, agentId, emitterScriptPath, desiredCommand))
                {
                    modified = true;
                }
            }

            if (!modified && before.Exists)
            {
                string alreadyMsg = extraNote == null
                    ? "KageDesk hooks already installed and up to date."
                    : $"KageDesk hooks already installed and up to date. Note: {extraNote}";
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.AlreadyInstalled,
                    alreadyMsg);
            }

            if (hooksWasAbsent)
            {
                root["hooks"] = hooksObj;
            }

            BeforeCommitHookForTest?.Invoke(configPath);

            if (!MatchesSnapshot(configPath, before))
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.Failed,
                    "Target config was modified concurrently; aborted to prevent clobbering changes.");
            }

            byte[] outBytes = SerializeJsonPreservingStyle(root, loadResult.HasUtf8Bom, loadResult.NewLine);
            string? backupPath = before.Exists ? BuildUniqueBackupPath(configPath) : null;
            WriteBytesAtomicWithReplace(configPath, outBytes, backupPath, before.Exists);

            string noteSuffix = extraNote == null ? "" : $" Note: {extraNote}";
            string installedMsg = backupPath != null
                ? $"Installed KageDesk hooks (backup saved to {Path.GetFileName(backupPath)}).{noteSuffix}"
                : $"Installed KageDesk hooks.{noteSuffix}";

            return new HookTargetResult(
                target,
                configPath,
                HookActionStatus.Installed,
                installedMsg,
                backupPath);
        });
    }

    private HookTargetResult UninstallHookJsonFile(
        HookTargetKind target,
        string configPath,
        string agentId,
        string emitterScriptPath,
        HookEventSpec[] specs)
    {
        ValidateSafePathSyntax(configPath, "Target config path");

        return WithTargetLock(configPath, () =>
        {
            ValidateNoReparsePointsInPath(configPath);
            FileSnapshot before = CaptureSnapshot(configPath);
            if (!before.Exists)
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.SkippedNotPresent,
                    "Config file does not exist.");
            }

            var loadResult = LoadStrictJsonObject(configPath, before);
            if (loadResult.ErrorStatus.HasValue)
            {
                return new HookTargetResult(target, configPath, loadResult.ErrorStatus.Value, loadResult.ErrorMessage!);
            }

            JsonObject root = loadResult.Root!;
            if (!root.TryGetPropertyValue("hooks", out JsonNode? hooksNode))
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.AlreadyRemoved,
                    "No KageDesk hooks found.");
            }

            if (hooksNode == null || hooksNode is not JsonObject hooksObj)
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.UnsupportedLayout,
                    "Existing 'hooks' property is null or not a JSON object; refused to modify user config.");
            }

            // Preflight managed events before mutating anything
            foreach (var spec in specs)
            {
                if (hooksObj.TryGetPropertyValue(spec.EventKey, out JsonNode? eventNode))
                {
                    if (eventNode == null || eventNode is not JsonArray)
                    {
                        return new HookTargetResult(
                            target,
                            configPath,
                            HookActionStatus.UnsupportedLayout,
                            $"Managed hook event '{spec.EventKey}' is null or not a JSON array; refused to modify user config.");
                    }
                }
            }

            bool modified = RemoveOwnedKageDeskEntries(hooksObj, specs, agentId, emitterScriptPath);
            if (!modified)
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.AlreadyRemoved,
                    "No KageDesk hooks found.");
            }

            // Remove "hooks" object ONLY because KageDesk removed entries and it became empty
            if (hooksObj.Count == 0)
            {
                root.Remove("hooks");
            }

            BeforeCommitHookForTest?.Invoke(configPath);

            if (!MatchesSnapshot(configPath, before))
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.Failed,
                    "Target config was modified concurrently; aborted uninstall to prevent clobbering changes.");
            }

            byte[] outBytes = SerializeJsonPreservingStyle(root, loadResult.HasUtf8Bom, loadResult.NewLine);
            string backupPath = BuildUniqueBackupPath(configPath);
            WriteBytesAtomicWithReplace(configPath, outBytes, backupPath, fileExisted: true);

            return new HookTargetResult(
                target,
                configPath,
                HookActionStatus.Uninstalled,
                $"Removed KageDesk hooks (backup saved to {Path.GetFileName(backupPath)}).",
                backupPath);
        });
    }

    internal string BuildHookCommand(string emitterScriptPath, string eventType, string agentId, string message)
    {
        string normalizedEmitter = ValidateEmitterScriptPath(emitterScriptPath);
        string quotedScript = EscapeForWindowsDoubleQuotedArg(normalizedEmitter);
        string quotedMessage = EscapeForWindowsDoubleQuotedArg(message);
        string portArg = _bridgePort != 4319 ? $" -Port {_bridgePort}" : "";
        return $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{quotedScript}\" -Type {eventType} -AgentId {agentId} -Message \"{quotedMessage}\"{portArg}";
    }

    private static string EscapeForWindowsDoubleQuotedArg(string value)
    {
        // Escape embedded quotes and trailing backslashes so Win32 CommandLineToArgvW parses verbatim
        var sb = new StringBuilder(value.Length + 4);
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }
        }

        if (backslashes > 0)
        {
            sb.Append('\\', backslashes * 2);
        }

        return sb.ToString();
    }

    private static bool UpsertHookEvent(
        JsonObject hooksObj,
        HookEventSpec spec,
        string agentId,
        string emitterScriptPath,
        string desiredCommand)
    {
        JsonArray eventArr;
        if (hooksObj.TryGetPropertyValue(spec.EventKey, out JsonNode? existingNode) && existingNode is JsonArray existingArr)
        {
            eventArr = existingArr;
        }
        else
        {
            eventArr = new JsonArray();
            hooksObj[spec.EventKey] = eventArr;
        }

        // Look ONLY for an exact KageDesk-owned entry for this spec & emitter path
        foreach (JsonNode? entryNode in eventArr)
        {
            if (entryNode is not JsonObject groupObj) continue;

            if (groupObj.TryGetPropertyValue("hooks", out JsonNode? innerNode) && innerNode is JsonArray innerHooks)
            {
                foreach (JsonNode? item in innerHooks)
                {
                    if (item is JsonObject hookObj && IsExactOwnedHookObject(hookObj, spec, agentId, emitterScriptPath))
                    {
                        bool changed = false;
                        string? existingCmd = hookObj["command"]?.GetValue<string>();
                        if (!string.Equals(existingCmd, desiredCommand, StringComparison.Ordinal))
                        {
                            hookObj["type"] = "command";
                            hookObj["command"] = desiredCommand;
                            changed = true;
                        }

                        if (spec.AsyncDispatch)
                        {
                            bool existingAsync = hookObj.TryGetPropertyValue("async", out JsonNode? aNode)
                                && aNode is JsonValue av && av.TryGetValue<bool>(out bool ab) && ab;
                            if (!existingAsync)
                            {
                                hookObj["async"] = true;
                                changed = true;
                            }
                        }

                        if (!string.IsNullOrEmpty(spec.Matcher))
                        {
                            string? existingMatcher = groupObj.TryGetPropertyValue("matcher", out JsonNode? mNode)
                                && mNode is JsonValue mv && mv.TryGetValue<string>(out string? ms) ? ms : null;
                            if (!string.Equals(existingMatcher, spec.Matcher, StringComparison.Ordinal) && innerHooks.Count == 1)
                            {
                                groupObj["matcher"] = spec.Matcher;
                                changed = true;
                            }
                        }

                        return changed;
                    }
                }
            }
        }

        var newHookObj = new JsonObject
        {
            ["type"] = "command",
            ["command"] = desiredCommand
        };
        if (spec.AsyncDispatch)
        {
            newHookObj["async"] = true;
        }

        var newGroup = new JsonObject();
        if (!string.IsNullOrEmpty(spec.Matcher))
        {
            newGroup["matcher"] = spec.Matcher;
        }
        newGroup["hooks"] = new JsonArray { newHookObj };

        eventArr.Add(newGroup);
        return true;
    }

    private static bool RemoveOwnedKageDeskEntries(
        JsonObject hooksObj,
        HookEventSpec[] specs,
        string agentId,
        string emitterScriptPath)
    {
        bool anyModified = false;
        var specByEventKey = new Dictionary<string, HookEventSpec>(StringComparer.Ordinal);
        foreach (var s in specs)
        {
            specByEventKey[s.EventKey] = s;
        }

        var keysToRemove = new List<string>();

        foreach (var kv in hooksObj)
        {
            if (!specByEventKey.TryGetValue(kv.Key, out var spec))
            {
                // Never touch unrelated/unknown user event arrays (even if empty!)
                continue;
            }

            if (kv.Value is not JsonArray eventArr) continue;

            bool removedFromThisEvent = false;

            for (int i = eventArr.Count - 1; i >= 0; i--)
            {
                if (eventArr[i] is not JsonObject groupObj) continue;

                if (groupObj.TryGetPropertyValue("hooks", out JsonNode? innerNode) && innerNode is JsonArray innerHooks)
                {
                    bool removedFromThisGroup = false;
                    for (int j = innerHooks.Count - 1; j >= 0; j--)
                    {
                        if (innerHooks[j] is JsonObject hookObj && IsExactOwnedHookObject(hookObj, spec, agentId, emitterScriptPath))
                        {
                            innerHooks.RemoveAt(j);
                            removedFromThisGroup = true;
                            removedFromThisEvent = true;
                            anyModified = true;
                        }
                    }

                    // Remove wrapper group ONLY if KageDesk itself emptied it AND it has only KageDesk-owned keys
                    if (removedFromThisGroup && innerHooks.Count == 0 && IsKageDeskOwnedWrapperGroup(groupObj, spec))
                    {
                        eventArr.RemoveAt(i);
                    }
                }
            }

            // Remove event array key ONLY if KageDesk itself removed entries from it and it became empty
            if (removedFromThisEvent && eventArr.Count == 0)
            {
                keysToRemove.Add(kv.Key);
            }
        }

        foreach (string key in keysToRemove)
        {
            hooksObj.Remove(key);
        }

        return anyModified;
    }

    private static bool IsKageDeskOwnedWrapperGroup(JsonObject groupObj, HookEventSpec spec)
    {
        if (groupObj.Count == 1 && groupObj.ContainsKey("hooks"))
        {
            return true;
        }

        if (groupObj.Count == 2 &&
            !string.IsNullOrEmpty(spec.Matcher) &&
            groupObj.ContainsKey("hooks") &&
            groupObj.TryGetPropertyValue("matcher", out JsonNode? mNode) &&
            mNode is JsonValue mv &&
            mv.TryGetValue<string>(out string? matcherVal) &&
            string.Equals(matcherVal, spec.Matcher, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsExactOwnedHookObject(
        JsonObject hookObj,
        HookEventSpec spec,
        string agentId,
        string emitterScriptPath)
    {
        if (!hookObj.TryGetPropertyValue("type", out JsonNode? typeNode) ||
            typeNode is not JsonValue typeVal ||
            !typeVal.TryGetValue<string>(out string? typeStr) ||
            !string.Equals(typeStr, "command", StringComparison.Ordinal))
        {
            return false;
        }

        if (!hookObj.TryGetPropertyValue("command", out JsonNode? cmdNode) ||
            cmdNode is not JsonValue cmdVal ||
            !cmdVal.TryGetValue<string>(out string? cmd) ||
            string.IsNullOrWhiteSpace(cmd))
        {
            return false;
        }

        // Exact ownership verification:
        // Must match our exact generated command shape anchored start-to-end with the EXACT canonical emitter script path,
        // exact agentId, exact eventType (current or legacy v2.2 spec for clean upgrade), exact message, and optional valid -Port.
        string quotedEmitter = EscapeForWindowsDoubleQuotedArg(emitterScriptPath);
        string quotedMessage = EscapeForWindowsDoubleQuotedArg(spec.Message);

        string pattern =
            "^powershell -NoProfile -ExecutionPolicy Bypass -File \"" +
            Regex.Escape(quotedEmitter) +
            "\" -Type " + Regex.Escape(spec.EventType) +
            " -AgentId " + Regex.Escape(agentId) +
            " -Message \"" + Regex.Escape(quotedMessage) +
            "\"(?: -Port [0-9]{1,5})?$";

        return Regex.IsMatch(cmd, pattern, RegexOptions.CultureInvariant);
    }

    private static bool IsOwnedOpenCodePlugin(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        string trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith(OpenCodePluginOwnershipMarker, StringComparison.Ordinal);
    }

    public static string BuildOpenCodePluginSource(int bridgePort)
    {
        int port = bridgePort is >= 1 and <= 65535 ? bridgePort : 4319;
        return $$"""
{{OpenCodePluginOwnershipMarker}}
// Auto-generated by KageDesk. Safe to remove via `KageDesk.exe --uninstall-hooks`.
import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";

const BRIDGE_PORT = {{port}};

function readBridgeToken() {
  try {
    const appData = process.env.APPDATA;
    if (!appData) return "";
    const tokenPath = join(appData, "KageDesk", "bridge.token");
    if (!existsSync(tokenPath)) return "";
    return readFileSync(tokenPath, "utf8").trim();
  } catch {
    return "";
  }
}

function emitEvent(type, message) {
  try {
    const headers = { "Content-Type": "application/json" };
    const token = readBridgeToken();
    if (token) headers["X-KageDesk-Token"] = token;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 1000);
    fetch(`http://127.0.0.1:${BRIDGE_PORT}/api/event`, {
      method: "POST",
      headers,
      body: JSON.stringify({ type, agentId: "opencode", message }),
      signal: controller.signal
    }).catch(() => {}).finally(() => clearTimeout(timer));
  } catch {
    // Non-blocking: never interfere with OpenCode execution.
  }
}

export const KageDeskPlugin = async () => ({
  "tool.execute.before": async () => {
    emitEvent("tool_start", "Tool execution started");
  },
  "tool.execute.after": async () => {
    emitEvent("tool_complete", "Tool execution completed");
  },
  "permission.asked": async () => {
    emitEvent("approval", "Approval requested");
  },
  "session.idle": async () => {
    emitEvent("turn_done", "Agent turn completed");
  },
  "session.error": async () => {
    emitEvent("fail", "Session error");
  }
});
""";
    }

    private sealed record JsonLoadResult(
        JsonObject? Root,
        bool HasUtf8Bom,
        string NewLine,
        HookActionStatus? ErrorStatus,
        string? ErrorMessage
    );

    private static JsonLoadResult LoadStrictJsonObject(string path, FileSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            return new JsonLoadResult(new JsonObject(), HasUtf8Bom: false, NewLine: Environment.NewLine, null, null);
        }

        byte[] raw = File.ReadAllBytes(path);
        bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        int offset = hasBom ? 3 : 0;

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(raw, offset, raw.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return new JsonLoadResult(null, hasBom, Environment.NewLine, HookActionStatus.MalformedConfig,
                $"File '{path}' is not valid UTF-8 JSON; refused to modify.");
        }

        string newLine = text.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : (text.Contains('\n') ? "\n" : Environment.NewLine);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonLoadResult(null, hasBom, newLine, HookActionStatus.MalformedConfig,
                $"File '{path}' is empty/whitespace and not a valid JSON object; refused to modify.");
        }

        try
        {
            JsonNode? strictNode = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

            if (strictNode is not JsonObject obj)
            {
                return new JsonLoadResult(null, hasBom, newLine, HookActionStatus.UnsupportedLayout,
                    $"Root JSON value in '{path}' is not an object; refused to modify.");
            }

            return new JsonLoadResult(obj, hasBom, newLine, null, null);
        }
        catch (JsonException)
        {
            // Distinguish JSONC (comments/trailing commas -> UnsupportedLayout) from genuinely malformed JSON
            try
            {
                JsonNode? relaxedNode = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                if (relaxedNode != null)
                {
                    return new JsonLoadResult(null, hasBom, newLine, HookActionStatus.UnsupportedLayout,
                        $"File '{path}' contains JSON comments or trailing commas; refused to modify to avoid losing formatting/comments.");
                }
            }
            catch (JsonException)
            {
                // Truly malformed JSON
            }

            return new JsonLoadResult(null, hasBom, newLine, HookActionStatus.MalformedConfig,
                $"File '{path}' contains malformed JSON; refused to modify.");
        }
    }

    private static byte[] SerializeJsonPreservingStyle(JsonObject root, bool includeUtf8Bom, string newLine)
    {
        string serialized = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // Normalize EOL to match original file's newline style
        serialized = serialized.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.Equals(newLine, "\n", StringComparison.Ordinal))
        {
            serialized = serialized.Replace("\n", newLine, StringComparison.Ordinal);
        }

        serialized += newLine;

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: includeUtf8Bom);
        if (!includeUtf8Bom)
        {
            return encoding.GetBytes(serialized);
        }

        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(serialized);
        byte[] combined = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, combined, preamble.Length, body.Length);
        return combined;
    }

    private static string ValidateEmitterScriptPath(string rawPath)
    {
        ValidateSafePathSyntax(rawPath, "Emitter script path");

        string fullPath = Path.GetFullPath(rawPath);
        if (fullPath.IndexOfAny(ForbiddenShellChars) >= 0)
        {
            throw new InvalidOperationException("Emitter script path contains unsafe shell characters.");
        }

        ValidateNoReparsePointsInPath(fullPath);
        return fullPath;
    }

    private static void ValidateSafePathSyntax(string rawPath, string label)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new ArgumentException($"{label} cannot be empty.");
        }

        if (rawPath.IndexOf('\0') >= 0 || rawPath.IndexOf('\r') >= 0 || rawPath.IndexOf('\n') >= 0)
        {
            throw new InvalidOperationException($"{label} contains invalid control characters.");
        }

        if (!Path.IsPathFullyQualified(rawPath))
        {
            throw new InvalidOperationException($"{label} must be an absolute path.");
        }

        // Reject relative traversal segments (..)
        string[] segments = rawPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == "..")
            {
                throw new InvalidOperationException($"{label} must not contain relative traversal segments ('..').");
            }
        }
    }

    private static void ValidateNoReparsePointsInPath(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath);
        string? root = Path.GetPathRoot(normalized);

        string? cursor = normalized;
        while (!string.IsNullOrEmpty(cursor) && !string.Equals(cursor, root, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(cursor) || Directory.Exists(cursor))
            {
                FileAttributes attrs = File.GetAttributes(cursor);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Refused reparse point / symbolic link in path: '{cursor}'.");
                }
            }

            string? parent = Path.GetDirectoryName(cursor);
            if (string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            cursor = parent;
        }
    }

    private static void EnsureDirectorySafe(string dirPath)
    {
        ValidateSafePathSyntax(dirPath, "Target directory");
        ValidateNoReparsePointsInPath(dirPath);
        Directory.CreateDirectory(dirPath);
        ValidateNoReparsePointsInPath(dirPath);
    }

    private readonly record struct FileSnapshot(
        bool Exists,
        long Length,
        DateTime LastWriteTimeUtc,
        byte[]? Sha256
    );

    private static FileSnapshot CaptureSnapshot(string path)
    {
        if (!File.Exists(path))
        {
            return new FileSnapshot(false, 0, DateTime.MinValue, null);
        }

        var fi = new FileInfo(path);
        byte[] bytes = File.ReadAllBytes(path);
        byte[] hash = SHA256.HashData(bytes);
        fi.Refresh();
        return new FileSnapshot(true, bytes.Length, fi.LastWriteTimeUtc, hash);
    }

    private static bool MatchesSnapshot(string path, FileSnapshot expected)
    {
        ValidateNoReparsePointsInPath(path);
        bool existsNow = File.Exists(path);
        if (!expected.Exists)
        {
            return !existsNow;
        }

        if (!existsNow)
        {
            return false;
        }

        var fi = new FileInfo(path);
        if (fi.Length != expected.Length || fi.LastWriteTimeUtc != expected.LastWriteTimeUtc)
        {
            return false;
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length != expected.Length)
        {
            return false;
        }

        byte[] hash = SHA256.HashData(bytes);
        return expected.Sha256 != null && hash.AsSpan().SequenceEqual(expected.Sha256);
    }

    private static T WithTargetLock<T>(string targetPath, Func<T> action)
    {
        string canonical = Path.GetFullPath(targetPath).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        string mutexName = $"Local\\KageDesk_HookLock_{Convert.ToHexString(hash)[..32]}";

        using var mutex = new Mutex(initiallyOwned: false, name: mutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException($"Timed out waiting for exclusive lock on '{targetPath}'.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private static void WithTargetLock(string targetPath, Action action)
    {
        WithTargetLock(targetPath, () =>
        {
            action();
            return 0;
        });
    }

    private string BuildUniqueBackupPath(string targetPath)
    {
        string stamp = _clock().ToString("yyyyMMdd-HHmmss-fff");
        string backupPath = $"{targetPath}.{stamp}.kagedesk.bak";
        int suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = $"{targetPath}.{stamp}-{suffix}.kagedesk.bak";
            suffix++;
        }
        return backupPath;
    }

    private static void WriteBytesAtomicWithReplace(string targetPath, byte[] bytes, string? backupPath, bool fileExisted)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            EnsureDirectorySafe(dir);
        }

        string tempPath = Path.Combine(
            dir ?? ".",
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (fileExisted)
            {
                // Coupled atomic backup + replace; fail on metadata errors (ignoreMetadataErrors: false)
                File.Replace(tempPath, targetPath, destinationBackupFileName: backupPath, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(tempPath, targetPath, overwrite: false);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public static string EmbeddedEmitterScript => """
param(
    [string]$Type = "tool_start",
    [string]$State = "",
    [string]$AgentId = "external",
    [string]$Name = "",
    [string]$Role = "",
    [string]$Message = "event",
    [string]$Repo = "",
    [string]$BuildStatus = "",
    [int]$Port = 4319,
    [string]$Token = ""
)

if ($Port -lt 1 -or $Port -gt 65535) { exit 0 }

if ([string]::IsNullOrWhiteSpace($Token)) {
    $tokenPath = Join-Path $env:APPDATA "KageDesk\bridge.token"
    if (Test-Path $tokenPath) {
        $Token = (Get-Content $tokenPath -Raw).Trim()
    }
}

$payload = [ordered]@{}
if (![string]::IsNullOrWhiteSpace($Type)) { $payload["type"] = $Type }
if (![string]::IsNullOrWhiteSpace($State)) { $payload["state"] = $State }
if (![string]::IsNullOrWhiteSpace($AgentId)) { $payload["agentId"] = $AgentId }
if (![string]::IsNullOrWhiteSpace($Name)) { $payload["name"] = $Name }
if (![string]::IsNullOrWhiteSpace($Role)) { $payload["role"] = $Role }
if (![string]::IsNullOrWhiteSpace($Message)) { $payload["message"] = $Message }
if (![string]::IsNullOrWhiteSpace($Repo)) { $payload["repo"] = $Repo }
if (![string]::IsNullOrWhiteSpace($BuildStatus)) { $payload["buildStatus"] = $BuildStatus }

$json = $payload | ConvertTo-Json -Compress
$headers = @{}
if (![string]::IsNullOrWhiteSpace($Token)) {
    $headers["X-KageDesk-Token"] = $Token
}

try {
    Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/event" -Method Post -ContentType "application/json" -Headers $headers -Body $json -TimeoutSec 1 | Out-Null
}
catch {
    # Non-blocking: never break user CLI hook execution if KageDesk is not running
}
exit 0
""";
}
