using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    private const string ProvenanceFileName = "provenance.json";
    private const string OpenCodePluginFileName = "kagedesk-plugin.js";
    private const string OpenCodePluginOwnershipMarker = "// kagedesk-managed-opencode-plugin:v1";
    private const string ClaudeApprovalNotificationMatcher = "permission_prompt|elicitation_dialog";
    private const string CodexTrustNote = "Codex requires interactive trust approval for workspace/hooks before hooks execute.";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static readonly char[] ForbiddenShellChars =
    {
        '\0', '\r', '\n', '"', ';', '|', '&', '<', '>', '`', '$', '%'
    };

    private static readonly UTF8Encoding Utf8NoBomStrict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _userProfileDir;
    private readonly string _appDataDir;
    private readonly string? _codexHomeDir;
    private readonly int _bridgePort;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Internal test seam invoked after staging/flushing the temp file and immediately before
    /// final identity/content revalidation and commit under the per-target lock.
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

    private string ProvenanceStorePath => Path.Combine(_appDataDir, "hooks", ProvenanceFileName);

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
            byte[] desiredBytes = Utf8NoBom.GetBytes(EmbeddedEmitterScript);
            if (File.Exists(targetPath))
            {
                byte[] existing = File.ReadAllBytes(targetPath);
                if (existing.AsSpan().SequenceEqual(desiredBytes))
                {
                    return;
                }

                WriteInternalBytesAtomic(targetPath, desiredBytes, fileExisted: true);
            }
            else
            {
                WriteInternalBytesAtomic(targetPath, desiredBytes, fileExisted: false);
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

                // Single read into before.OriginalBytes: validate ownership & equality from THAT EXACT buffer
                FileSnapshot before = CaptureSnapshot(pluginPath);
                string desiredContent = BuildOpenCodePluginSource(_bridgePort);
                byte[] desiredBytes = Utf8NoBom.GetBytes(desiredContent);

                if (before.Exists)
                {
                    byte[] existingBytes = before.OriginalBytes!;
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

                string? backupPath = before.Exists ? BuildUniqueBackupPath(pluginPath) : null;
                if (!CommitBytesWithPreReplaceCheck(pluginPath, desiredBytes, backupPath, before))
                {
                    return new HookTargetResult(
                        HookTargetKind.OpenCode,
                        pluginPath,
                        HookActionStatus.Failed,
                        "OpenCode plugin file was modified concurrently; aborted to prevent clobbering changes.");
                }

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

                // Single read into before.OriginalBytes
                FileSnapshot before = CaptureSnapshot(targetPlugin);
                if (!before.Exists)
                {
                    return new HookTargetResult(
                        HookTargetKind.OpenCode,
                        targetPlugin,
                        HookActionStatus.AlreadyRemoved,
                        "No KageDesk OpenCode plugin found.");
                }

                string existingText = Encoding.UTF8.GetString(before.OriginalBytes!);
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

    private sealed record LegacyHookFingerprint(
        string EventKey,
        string EventType,
        string Message
    );

    private static readonly LegacyHookFingerprint[] LegacyHookFingerprints =
    [
        new LegacyHookFingerprint("PostToolUse", "tool_done", "Tool execution completed"),
        new LegacyHookFingerprint("Notification", "notification", "Notification received"),
        new LegacyHookFingerprint("Stop", "idle", "Agent stopped")
    ];

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

            // Single read into before.OriginalBytes; compute hash AND parse from THAT EXACT buffer
            FileSnapshot before = CaptureSnapshot(configPath);
            ContainerProvenance existingProv = LoadContainerProvenance(configPath);

            if (!before.Exists)
            {
                byte[] freshBytes = BuildFreshHooksJsonDocument(specs, agentId, emitterScriptPath);
                if (!CommitBytesWithPreReplaceCheck(configPath, freshBytes, backupPath: null, before))
                {
                    return new HookTargetResult(
                        target,
                        configPath,
                        HookActionStatus.Failed,
                        "Target config was created concurrently; aborted to prevent clobbering changes.");
                }

                var freshProv = new ContainerProvenance(
                    CreatedHooksObject: true,
                    CreatedEventKeys: new HashSet<string>(specs.Select(s => s.EventKey), StringComparer.Ordinal));
                SaveContainerProvenance(configPath, freshProv);

                string freshNote = extraNote == null ? "" : $" Note: {extraNote}";
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.Installed,
                    $"Installed KageDesk hooks.{freshNote}");
            }

            SpanParseResult parsed = ParseStrictJsonSpans(configPath, before.OriginalBytes!);
            if (parsed.ErrorStatus.HasValue)
            {
                return new HookTargetResult(target, configPath, parsed.ErrorStatus.Value, parsed.ErrorMessage!);
            }

            JsonSpanObject root = parsed.Root!;
            ReadOnlySpan<byte> body = before.OriginalBytes.AsSpan(parsed.BomLength);
            JsonDocStyle style = DetectJsonDocStyle(body, root);

            // Preflight "hooks" property (absent vs duplicate vs explicit null vs non-object)
            if (!TryGetUniqueProperty(root, "hooks", out JsonSpanProperty? hooksProp, out bool duplicateHooks))
            {
                if (duplicateHooks)
                {
                    return new HookTargetResult(
                        target,
                        configPath,
                        HookActionStatus.UnsupportedLayout,
                        "Existing 'hooks' property appears multiple times; refused to modify user config.");
                }
            }

            JsonSpanObject? existingHooks = null;
            bool hooksWasAbsent = hooksProp == null;
            if (hooksProp != null)
            {
                if (hooksProp.Value.TokenType == JsonTokenType.Null)
                {
                    return new HookTargetResult(
                        target,
                        configPath,
                        HookActionStatus.UnsupportedLayout,
                        "Existing 'hooks' property is explicit null; refused to modify user config.");
                }

                if (hooksProp.Value is not JsonSpanObject hooksObj)
                {
                    return new HookTargetResult(
                        target,
                        configPath,
                        HookActionStatus.UnsupportedLayout,
                        "Existing 'hooks' property is not a JSON object; refused to modify user config.");
                }

                existingHooks = hooksObj;

                // Preflight ALL managed event keys before planning any edits
                foreach (var spec in specs)
                {
                    if (!TryGetUniqueProperty(existingHooks, spec.EventKey, out JsonSpanProperty? eventProp, out bool duplicateEvent))
                    {
                        if (duplicateEvent)
                        {
                            return new HookTargetResult(
                                target,
                                configPath,
                                HookActionStatus.UnsupportedLayout,
                                $"Managed hook event '{spec.EventKey}' appears multiple times; refused to modify user config.");
                        }
                        continue;
                    }

                    if (eventProp!.Value.TokenType == JsonTokenType.Null)
                    {
                        return new HookTargetResult(
                            target,
                            configPath,
                            HookActionStatus.UnsupportedLayout,
                            $"Managed hook event '{spec.EventKey}' is explicit null; refused to modify user config.");
                    }

                    if (eventProp.Value is not JsonSpanArray)
                    {
                        return new HookTargetResult(
                            target,
                            configPath,
                            HookActionStatus.UnsupportedLayout,
                            $"Managed hook event '{spec.EventKey}' is not a JSON array; refused to modify user config.");
                    }
                }
            }

            var edits = new List<ByteEdit>();
            bool createdHooksObjNow = hooksWasAbsent;
            var createdEventKeysNow = new HashSet<string>(existingProv.CreatedEventKeys, StringComparer.Ordinal);
            bool effectiveCreatedHooksObj = existingProv.CreatedHooksObject || createdHooksObjNow;

            if (hooksWasAbsent)
            {
                foreach (var spec in specs)
                {
                    createdEventKeysNow.Add(spec.EventKey);
                }

                string rootChildIndent = GetChildIndentForObject(body, root, style);
                string rootObjIndent = GetLineIndent(body, root.Start);
                string hooksPropText = FormatHooksProperty(specs, agentId, emitterScriptPath, style, rootChildIndent);
                AddInsertPropertyIntoObjectEdit(edits, body, root, hooksPropText, style, rootChildIndent, rootObjIndent);
            }
            else
            {
                string hooksObjIndent = GetLineIndent(body, existingHooks!.Start);
                string hooksChildIndent = GetChildIndentForObject(body, existingHooks, style);

                var specByKey = new Dictionary<string, HookEventSpec>(StringComparer.Ordinal);
                foreach (var s in specs) specByKey[s.EventKey] = s;

                var newEventPropsToAppend = new List<string>();
                var deprecatedLegacyKeysToRemove = new HashSet<int>();

                // Plan edits on existing properties in hooksObj
                for (int pIdx = 0; pIdx < existingHooks.Properties.Count; pIdx++)
                {
                    JsonSpanProperty prop = existingHooks.Properties[pIdx];
                    if (specByKey.TryGetValue(prop.Name, out HookEventSpec? spec))
                    {
                        var eventArr = (JsonSpanArray)prop.Value;
                        string desiredCmd = BuildHookCommand(emitterScriptPath, spec.EventType, agentId, spec.Message);
                        PlanUpsertEventArrayEdits(edits, body, eventArr, spec, agentId, emitterScriptPath, desiredCmd, style);
                    }
                    else if (IsLegacyOnlyEventKey(prop.Name, specByKey) && prop.Value is JsonSpanArray legacyArr)
                    {
                        // E.g. Codex previously installed Notification; clean legacy KageDesk entries on upgrade!
                        bool removeKey = PlanRemoveEntriesFromEventArrayEdits(
                            edits,
                            body,
                            legacyArr,
                            eventKey: prop.Name,
                            currentSpec: null,
                            agentId,
                            emitterScriptPath,
                            canRemoveEmptyEventKey: existingProv.CreatedEventKeys.Contains(prop.Name),
                            out bool removedAny);
                        if (removedAny && removeKey)
                        {
                            deprecatedLegacyKeysToRemove.Add(pIdx);
                            createdEventKeysNow.Remove(prop.Name);
                        }
                    }
                }

                // Append missing managed event keys
                foreach (var spec in specs)
                {
                    if (!HasProperty(existingHooks, spec.EventKey))
                    {
                        createdEventKeysNow.Add(spec.EventKey);
                        string desiredCmd = BuildHookCommand(emitterScriptPath, spec.EventType, agentId, spec.Message);
                        string propText = FormatEventProperty(spec, desiredCmd, style, hooksChildIndent);
                        newEventPropsToAppend.Add(propText);
                    }
                }

                if (deprecatedLegacyKeysToRemove.Count > 0)
                {
                    // Remove deprecated legacy keys from hooksObj (only when they became empty AND provenance allows)
                    if (deprecatedLegacyKeysToRemove.Count == existingHooks.Properties.Count && newEventPropsToAppend.Count == 0)
                    {
                        edits.Add(new ByteEdit(existingHooks.Start + 1, existingHooks.CloseBraceIndex, Array.Empty<byte>()));
                    }
                    else if (deprecatedLegacyKeysToRemove.Count < existingHooks.Properties.Count)
                    {
                        AddRemoveObjectPropertiesEdits(edits, existingHooks, deprecatedLegacyKeysToRemove);
                    }
                }

                if (newEventPropsToAppend.Count > 0)
                {
                    string combined = string.Join(
                        style.IsCompact ? "," : "," + style.NewLine + hooksChildIndent,
                        newEventPropsToAppend);

                    if (deprecatedLegacyKeysToRemove.Count == existingHooks.Properties.Count)
                    {
                        // Replacing all old deprecated keys with new ones
                        string replacement = style.IsCompact
                            ? combined
                            : style.NewLine + hooksChildIndent + combined + style.NewLine + hooksObjIndent;
                        edits.Add(new ByteEdit(existingHooks.Start + 1, existingHooks.CloseBraceIndex, Utf8NoBom.GetBytes(replacement)));
                    }
                    else
                    {
                        // Append after last property (if last property is being removed, insert after its FullEnd which is right after deletion range)
                        AddInsertPropertyIntoObjectEdit(edits, body, existingHooks, combined, style, hooksChildIndent, hooksObjIndent);
                    }
                }
            }

            if (edits.Count == 0)
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

            byte[] outBytes = ApplyByteEdits(before.OriginalBytes!, parsed.BomLength, edits);
            string backupPath = BuildUniqueBackupPath(configPath);
            if (!CommitBytesWithPreReplaceCheck(configPath, outBytes, backupPath, before))
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.Failed,
                    "Target config was modified concurrently; aborted to prevent clobbering changes.");
            }

            SaveContainerProvenance(configPath, new ContainerProvenance(effectiveCreatedHooksObj, createdEventKeysNow));

            string noteSuffix = extraNote == null ? "" : $" Note: {extraNote}";
            string installedMsg = $"Installed KageDesk hooks (backup saved to {Path.GetFileName(backupPath)}).{noteSuffix}";

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

            // Single read into before.OriginalBytes; compute hash AND parse from THAT EXACT buffer
            FileSnapshot before = CaptureSnapshot(configPath);
            if (!before.Exists)
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.SkippedNotPresent,
                    "Config file does not exist.");
            }

            SpanParseResult parsed = ParseStrictJsonSpans(configPath, before.OriginalBytes!);
            if (parsed.ErrorStatus.HasValue)
            {
                return new HookTargetResult(target, configPath, parsed.ErrorStatus.Value, parsed.ErrorMessage!);
            }

            JsonSpanObject root = parsed.Root!;
            ReadOnlySpan<byte> body = before.OriginalBytes.AsSpan(parsed.BomLength);

            if (!TryGetUniqueProperty(root, "hooks", out JsonSpanProperty? hooksProp, out bool duplicateHooks))
            {
                if (duplicateHooks)
                {
                    return new HookTargetResult(
                        target,
                        configPath,
                        HookActionStatus.UnsupportedLayout,
                        "Existing 'hooks' property appears multiple times; refused to modify user config.");
                }

                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.AlreadyRemoved,
                    "No KageDesk hooks found.");
            }

            if (hooksProp!.Value.TokenType == JsonTokenType.Null || hooksProp.Value is not JsonSpanObject hooksObj)
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.UnsupportedLayout,
                    "Existing 'hooks' property is null or not a JSON object; refused to modify user config.");
            }

            var specByKey = new Dictionary<string, HookEventSpec>(StringComparer.Ordinal);
            foreach (var s in specs) specByKey[s.EventKey] = s;

            // Preflight managed events before mutating anything
            foreach (var spec in specs)
            {
                if (!TryGetUniqueProperty(hooksObj, spec.EventKey, out JsonSpanProperty? eventProp, out bool duplicateEvent))
                {
                    if (duplicateEvent)
                    {
                        return new HookTargetResult(
                            target,
                            configPath,
                            HookActionStatus.UnsupportedLayout,
                            $"Managed hook event '{spec.EventKey}' appears multiple times; refused to modify user config.");
                    }
                    continue;
                }

                if (eventProp!.Value.TokenType == JsonTokenType.Null || eventProp.Value is not JsonSpanArray)
                {
                    return new HookTargetResult(
                        target,
                        configPath,
                        HookActionStatus.UnsupportedLayout,
                        $"Managed hook event '{spec.EventKey}' is null or not a JSON array; refused to modify user config.");
                }
            }

            ContainerProvenance prov = LoadContainerProvenance(configPath);
            var remainingProvKeys = new HashSet<string>(prov.CreatedEventKeys, StringComparer.Ordinal);
            var innerEdits = new List<ByteEdit>();
            var eventPropIndicesToRemove = new HashSet<int>();
            bool anyModified = false;

            for (int pIdx = 0; pIdx < hooksObj.Properties.Count; pIdx++)
            {
                JsonSpanProperty prop = hooksObj.Properties[pIdx];
                bool isCurrentManaged = specByKey.TryGetValue(prop.Name, out HookEventSpec? spec);
                bool isLegacyEvent = IsLegacyOnlyEventKey(prop.Name, specByKey);

                if (!isCurrentManaged && !isLegacyEvent)
                {
                    // Never touch unrelated/unknown user event arrays (even if empty!)
                    continue;
                }

                if (prop.Value is not JsonSpanArray eventArr)
                {
                    continue;
                }

                // Never delete an empty managed event array unless KageDesk provably created it!
                bool canRemoveEmptyEventKey = prov.CreatedEventKeys.Contains(prop.Name);
                bool removeEventKey = PlanRemoveEntriesFromEventArrayEdits(
                    innerEdits,
                    body,
                    eventArr,
                    eventKey: prop.Name,
                    currentSpec: spec,
                    agentId,
                    emitterScriptPath,
                    canRemoveEmptyEventKey,
                    out bool removedAnyFromEvent);

                if (removedAnyFromEvent)
                {
                    anyModified = true;
                    if (removeEventKey)
                    {
                        eventPropIndicesToRemove.Add(pIdx);
                        remainingProvKeys.Remove(prop.Name);
                    }
                }
            }

            if (!anyModified)
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.AlreadyRemoved,
                    "No KageDesk hooks found.");
            }

            var finalEdits = new List<ByteEdit>();
            bool hooksBecomesEmpty = eventPropIndicesToRemove.Count == hooksObj.Properties.Count && hooksObj.Properties.Count > 0;
            bool removeHooksProp = hooksBecomesEmpty && prov.CreatedHooksObject;

            if (removeHooksProp)
            {
                // Remove entire "hooks" property from root (skipping any inner edits inside "hooks")
                int hooksIndex = root.Properties.IndexOf(hooksProp);
                if (root.Properties.Count == 1)
                {
                    finalEdits.Add(new ByteEdit(root.Start + 1, root.CloseBraceIndex, Array.Empty<byte>()));
                }
                else
                {
                    AddRemoveObjectPropertiesEdits(finalEdits, root, new HashSet<int> { hooksIndex });
                }
            }
            else
            {
                if (hooksBecomesEmpty)
                {
                    // Ownership of "hooks" is unproven: conservatively preserve empty "hooks": {}
                    finalEdits.Add(new ByteEdit(hooksObj.Start + 1, hooksObj.CloseBraceIndex, Array.Empty<byte>()));
                }
                else
                {
                    // Keep inner edits for properties that are NOT being removed as a whole
                    foreach (var edit in innerEdits)
                    {
                        bool insideRemovedProp = false;
                        foreach (int remIdx in eventPropIndicesToRemove)
                        {
                            var rp = hooksObj.Properties[remIdx];
                            if (edit.Start >= rp.FullStart && edit.End <= rp.FullEnd)
                            {
                                insideRemovedProp = true;
                                break;
                            }
                        }

                        if (!insideRemovedProp)
                        {
                            finalEdits.Add(edit);
                        }
                    }

                    if (eventPropIndicesToRemove.Count > 0)
                    {
                        AddRemoveObjectPropertiesEdits(finalEdits, hooksObj, eventPropIndicesToRemove);
                    }
                }
            }

            byte[] outBytes = ApplyByteEdits(before.OriginalBytes!, parsed.BomLength, finalEdits);
            string backupPath = BuildUniqueBackupPath(configPath);
            if (!CommitBytesWithPreReplaceCheck(configPath, outBytes, backupPath, before))
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.Failed,
                    "Target config was modified concurrently; aborted uninstall to prevent clobbering changes.");
            }

            if (removeHooksProp)
            {
                ClearContainerProvenance(configPath);
            }
            else if (prov.CreatedHooksObject || prov.CreatedEventKeys.Count > 0)
            {
                SaveContainerProvenance(configPath, new ContainerProvenance(prov.CreatedHooksObject, remainingProvKeys));
            }

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

    private static bool IsLegacyOnlyEventKey(string eventKey, Dictionary<string, HookEventSpec> currentSpecs)
    {
        if (currentSpecs.ContainsKey(eventKey))
            return false;

        foreach (var legacy in LegacyHookFingerprints)
        {
            if (string.Equals(legacy.EventKey, eventKey, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void PlanUpsertEventArrayEdits(
        List<ByteEdit> edits,
        ReadOnlySpan<byte> body,
        JsonSpanArray eventArr,
        HookEventSpec spec,
        string agentId,
        string emitterScriptPath,
        string desiredCommand,
        JsonDocStyle style)
    {
        // Search for existing current OR exact legacy KageDesk hook entries.
        // If multiple owned entries exist (e.g. a legacy entry + a current entry), upgrade/keep the first and remove duplicates.
        bool foundPrimary = false;
        var extraGroupIndicesToRemove = new HashSet<int>();

        for (int gIdx = 0; gIdx < eventArr.Elements.Count; gIdx++)
        {
            if (eventArr.Elements[gIdx] is not JsonSpanObject groupObj)
                continue;

            if (!TryGetUniqueProperty(groupObj, "hooks", out JsonSpanProperty? innerProp, out _) ||
                innerProp?.Value is not JsonSpanArray innerHooks)
            {
                continue;
            }

            var extraInnerIndicesToRemove = new HashSet<int>();
            bool matchedInThisGroup = false;

            for (int hIdx = 0; hIdx < innerHooks.Elements.Count; hIdx++)
            {
                if (innerHooks.Elements[hIdx] is not JsonSpanObject hookObj)
                    continue;

                bool isCurrent = IsCurrentOwnedHookObject(hookObj, spec, agentId, emitterScriptPath);
                bool isLegacy = !isCurrent && IsLegacyOwnedHookObject(hookObj, spec.EventKey, agentId, emitterScriptPath);
                if (!isCurrent && !isLegacy)
                    continue;

                matchedInThisGroup = true;
                if (!foundPrimary)
                {
                    foundPrimary = true;
                    bool isUpToDate = isCurrent && IsHookEntryUpToDate(groupObj, innerHooks, hookObj, spec, desiredCommand);
                    if (!isUpToDate)
                    {
                        if (innerHooks.Elements.Count == 1 && IsKageDeskOwnedWrapperGroup(groupObj, spec.Matcher))
                        {
                            string groupIndent = GetLineIndent(body, groupObj.Start);
                            string newGroupJson = FormatWrapperGroup(spec, desiredCommand, style, groupIndent);
                            edits.Add(new ByteEdit(groupObj.Start, groupObj.End, Utf8NoBom.GetBytes(newGroupJson)));
                        }
                        else
                        {
                            string hookIndent = GetLineIndent(body, hookObj.Start);
                            string newHookJson = FormatHookObject(desiredCommand, spec.AsyncDispatch, style, hookIndent);
                            edits.Add(new ByteEdit(hookObj.Start, hookObj.End, Utf8NoBom.GetBytes(newHookJson)));
                        }
                    }
                }
                else
                {
                    extraInnerIndicesToRemove.Add(hIdx);
                }
            }

            if (matchedInThisGroup && extraInnerIndicesToRemove.Count > 0)
            {
                if (extraInnerIndicesToRemove.Count == innerHooks.Elements.Count &&
                    IsKageDeskOwnedWrapperGroup(groupObj, spec.Matcher))
                {
                    extraGroupIndicesToRemove.Add(gIdx);
                }
                else
                {
                    AddRemoveArrayElementsEdits(edits, innerHooks, extraInnerIndicesToRemove);
                }
            }
        }

        if (extraGroupIndicesToRemove.Count > 0)
        {
            AddRemoveArrayElementsEdits(edits, eventArr, extraGroupIndicesToRemove);
        }

        if (!foundPrimary)
        {
            string arrIndent = GetLineIndent(body, eventArr.Start);
            string elemIndent = GetChildIndentForArray(body, eventArr, style);
            string newGroupJson = FormatWrapperGroup(spec, desiredCommand, style, elemIndent);
            AddInsertElementIntoArrayEdit(edits, body, eventArr, newGroupJson, style, elemIndent, arrIndent);
        }
    }

    private static bool IsHookEntryUpToDate(
        JsonSpanObject groupObj,
        JsonSpanArray innerHooks,
        JsonSpanObject hookObj,
        HookEventSpec spec,
        string desiredCommand)
    {
        if (!TryGetStringProperty(hookObj, "command", out string? existingCmd) ||
            !string.Equals(existingCmd, desiredCommand, StringComparison.Ordinal))
        {
            return false;
        }

        if (spec.AsyncDispatch)
        {
            if (!TryGetBoolProperty(hookObj, "async", out bool asyncVal) || !asyncVal)
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(spec.Matcher) && innerHooks.Elements.Count == 1)
        {
            if (!TryGetStringProperty(groupObj, "matcher", out string? existingMatcher) ||
                !string.Equals(existingMatcher, spec.Matcher, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PlanRemoveEntriesFromEventArrayEdits(
        List<ByteEdit> edits,
        ReadOnlySpan<byte> body,
        JsonSpanArray eventArr,
        string eventKey,
        HookEventSpec? currentSpec,
        string agentId,
        string emitterScriptPath,
        bool canRemoveEmptyEventKey,
        out bool removedAny)
    {
        removedAny = false;
        var groupIndicesToRemove = new HashSet<int>();
        var nestedEdits = new List<ByteEdit>();

        for (int gIdx = 0; gIdx < eventArr.Elements.Count; gIdx++)
        {
            if (eventArr.Elements[gIdx] is not JsonSpanObject groupObj)
                continue;

            if (!TryGetUniqueProperty(groupObj, "hooks", out JsonSpanProperty? innerProp, out _) ||
                innerProp?.Value is not JsonSpanArray innerHooks)
            {
                continue;
            }

            var innerToRemove = new HashSet<int>();
            for (int hIdx = 0; hIdx < innerHooks.Elements.Count; hIdx++)
            {
                if (innerHooks.Elements[hIdx] is not JsonSpanObject hookObj)
                    continue;

                bool isOwned =
                    (currentSpec != null && IsCurrentOwnedHookObject(hookObj, currentSpec, agentId, emitterScriptPath)) ||
                    IsLegacyOwnedHookObject(hookObj, eventKey, agentId, emitterScriptPath);

                if (isOwned)
                {
                    innerToRemove.Add(hIdx);
                    removedAny = true;
                }
            }

            if (innerToRemove.Count == 0)
                continue;

            bool groupBecomesEmpty = innerToRemove.Count == innerHooks.Elements.Count;
            if (groupBecomesEmpty && IsKageDeskOwnedWrapperGroup(groupObj, currentSpec?.Matcher))
            {
                groupIndicesToRemove.Add(gIdx);
            }
            else if (groupBecomesEmpty)
            {
                // User's wrapper group (extra keys or non-matching matcher): preserve wrapper with empty hooks array
                nestedEdits.Add(new ByteEdit(innerHooks.Start + 1, innerHooks.CloseBracketIndex, Array.Empty<byte>()));
            }
            else
            {
                AddRemoveArrayElementsEdits(nestedEdits, innerHooks, innerToRemove);
            }
        }

        if (!removedAny)
        {
            return false;
        }

        bool eventArrBecomesEmpty = groupIndicesToRemove.Count == eventArr.Elements.Count && eventArr.Elements.Count > 0;
        if (eventArrBecomesEmpty && canRemoveEmptyEventKey)
        {
            // Caller will remove the whole event key property from hooksObj
            return true;
        }

        if (eventArrBecomesEmpty)
        {
            // Pre-existing / unproven container: preserve empty array [] verbatim!
            edits.Add(new ByteEdit(eventArr.Start + 1, eventArr.CloseBracketIndex, Array.Empty<byte>()));
            return false;
        }

        edits.AddRange(nestedEdits);
        if (groupIndicesToRemove.Count > 0)
        {
            AddRemoveArrayElementsEdits(edits, eventArr, groupIndicesToRemove);
        }

        return false;
    }

    private static bool IsKageDeskOwnedWrapperGroup(JsonSpanObject groupObj, string? expectedMatcher)
    {
        if (groupObj.Properties.Count == 1 && HasProperty(groupObj, "hooks"))
        {
            return true;
        }

        if (groupObj.Properties.Count == 2 &&
            !string.IsNullOrEmpty(expectedMatcher) &&
            HasProperty(groupObj, "hooks") &&
            TryGetStringProperty(groupObj, "matcher", out string? matcherVal) &&
            string.Equals(matcherVal, expectedMatcher, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsCurrentOwnedHookObject(
        JsonSpanObject hookObj,
        HookEventSpec spec,
        string agentId,
        string emitterScriptPath)
    {
        return TryGetStrictKageDeskCommand(hookObj, out string? cmd) &&
               IsExactCommandMatch(cmd!, emitterScriptPath, spec.EventType, agentId, spec.Message);
    }

    private static bool IsLegacyOwnedHookObject(
        JsonSpanObject hookObj,
        string eventKey,
        string agentId,
        string emitterScriptPath)
    {
        if (!TryGetStrictKageDeskCommand(hookObj, out string? cmd))
            return false;

        foreach (var legacy in LegacyHookFingerprints)
        {
            if (string.Equals(legacy.EventKey, eventKey, StringComparison.Ordinal) &&
                IsExactCommandMatch(cmd!, emitterScriptPath, legacy.EventType, agentId, legacy.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetStrictKageDeskCommand(JsonSpanObject hookObj, out string? command)
    {
        command = null;

        // Reject customized user objects carrying unexpected extra properties
        foreach (var p in hookObj.Properties)
        {
            if (!string.Equals(p.Name, "type", StringComparison.Ordinal) &&
                !string.Equals(p.Name, "command", StringComparison.Ordinal) &&
                !string.Equals(p.Name, "async", StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!TryGetStringProperty(hookObj, "type", out string? typeStr) ||
            !string.Equals(typeStr, "command", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryGetStringProperty(hookObj, "command", out string? cmd) ||
            string.IsNullOrWhiteSpace(cmd))
        {
            return false;
        }

        command = cmd;
        return true;
    }

    private static bool IsExactCommandMatch(
        string cmd,
        string emitterScriptPath,
        string eventType,
        string agentId,
        string message)
    {
        // Exact ownership verification anchored start-to-end with the canonical emitter script path,
        // exact agentId, exact eventType, exact message, and optional valid -Port.
        string quotedEmitter = EscapeForWindowsDoubleQuotedArg(emitterScriptPath);
        string quotedMessage = EscapeForWindowsDoubleQuotedArg(message);

        string pattern =
            "^powershell -NoProfile -ExecutionPolicy Bypass -File \"" +
            Regex.Escape(quotedEmitter) +
            "\" -Type " + Regex.Escape(eventType) +
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
  event: async ({ event }) => {
    const eventType = event && typeof event.type === "string" ? event.type : "";
    switch (eventType) {
      case "permission.asked":
      case "permission.updated":
        emitEvent("approval", "Approval requested");
        break;
      case "session.idle":
        emitEvent("turn_done", "Agent turn completed");
        break;
      case "session.error":
        emitEvent("fail", "Session error");
        break;
      default:
        break;
    }
  }
});
""";
    }

    // ------------------------------------------------------------------------
    // Lossless UTF-8 JSON Span AST & Surgical Byte Splicing
    // ------------------------------------------------------------------------

    private abstract class JsonSpanNode
    {
        public int Start { get; init; }
        public int End { get; init; }
        public JsonTokenType TokenType { get; init; }
    }

    private sealed class JsonSpanPrimitive : JsonSpanNode
    {
        public string? StringValue { get; init; }
        public bool? BoolValue { get; init; }
    }

    private sealed class JsonSpanProperty
    {
        public required string Name { get; init; }
        public int NameStart { get; init; }
        public int NameEnd { get; init; }
        public required JsonSpanNode Value { get; init; }
        public int FullStart => NameStart;
        public int FullEnd => Value.End;
    }

    private sealed class JsonSpanObject : JsonSpanNode
    {
        public int CloseBraceIndex { get; init; }
        public List<JsonSpanProperty> Properties { get; } = new();
    }

    private sealed class JsonSpanArray : JsonSpanNode
    {
        public int CloseBracketIndex { get; init; }
        public List<JsonSpanNode> Elements { get; } = new();
    }

    private sealed record SpanParseResult(
        JsonSpanObject? Root,
        int BomLength,
        HookActionStatus? ErrorStatus,
        string? ErrorMessage
    );

    private readonly record struct JsonDocStyle(
        bool IsCompact,
        string NewLine,
        string IndentUnit,
        string ColonSep
    );

    private readonly record struct ByteEdit(int Start, int End, byte[] Replacement);

    private static SpanParseResult ParseStrictJsonSpans(string path, byte[] rawBytes)
    {
        int bomLen = rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF ? 3 : 0;
        ReadOnlySpan<byte> utf8Body = rawBytes.AsSpan(bomLen);

        // Validate strict UTF-8 encoding first (zero invalid sequences allowed)
        try
        {
            _ = Utf8NoBomStrict.GetCharCount(utf8Body);
        }
        catch (DecoderFallbackException)
        {
            return new SpanParseResult(null, bomLen, HookActionStatus.MalformedConfig,
                $"File '{path}' is not valid UTF-8 JSON; refused to modify.");
        }

        if (IsAsciiWhitespaceOnly(utf8Body))
        {
            return new SpanParseResult(null, bomLen, HookActionStatus.MalformedConfig,
                $"File '{path}' is empty/whitespace and not a valid JSON object; refused to modify.");
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Body, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

            if (!reader.Read())
            {
                return new SpanParseResult(null, bomLen, HookActionStatus.MalformedConfig,
                    $"File '{path}' contains malformed JSON; refused to modify.");
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                // Still consume the rest to verify well-formed JSON vs malformed
                SkipOrReadNode(ref reader, depth: 0);
                if (reader.Read())
                {
                    return new SpanParseResult(null, bomLen, HookActionStatus.MalformedConfig,
                        $"File '{path}' contains malformed JSON; refused to modify.");
                }

                return new SpanParseResult(null, bomLen, HookActionStatus.UnsupportedLayout,
                    $"Root JSON value in '{path}' is not an object; refused to modify.");
            }

            JsonSpanObject root = ReadObjectSpan(ref reader, depth: 0);

            // Reject trailing garbage after root object
            if (reader.Read())
            {
                return new SpanParseResult(null, bomLen, HookActionStatus.MalformedConfig,
                    $"File '{path}' contains trailing tokens after root JSON object; refused to modify.");
            }

            return new SpanParseResult(root, bomLen, null, null);
        }
        catch (JsonException)
        {
            // Distinguish JSONC (comments/trailing commas -> UnsupportedLayout) from genuinely malformed JSON
            try
            {
                var relaxed = new Utf8JsonReader(utf8Body, new JsonReaderOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                if (relaxed.Read())
                {
                    while (relaxed.Read()) { }
                    return new SpanParseResult(null, bomLen, HookActionStatus.UnsupportedLayout,
                        $"File '{path}' contains JSON comments or trailing commas; refused to modify to avoid losing formatting/comments.");
                }
            }
            catch (JsonException)
            {
                // Truly malformed JSON
            }

            return new SpanParseResult(null, bomLen, HookActionStatus.MalformedConfig,
                $"File '{path}' contains malformed JSON; refused to modify.");
        }
    }

    private static JsonSpanNode SkipOrReadNode(ref Utf8JsonReader reader, int depth)
    {
        int start = (int)reader.TokenStartIndex;
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return ReadObjectSpan(ref reader, depth);
            case JsonTokenType.StartArray:
                return ReadArraySpan(ref reader, depth);
            case JsonTokenType.String:
                // Decode string value only inside shallow hook structure (depth <= 6)
                string? strVal = depth <= 6 ? reader.GetString() : null;
                return new JsonSpanPrimitive
                {
                    Start = start,
                    End = (int)reader.BytesConsumed,
                    TokenType = JsonTokenType.String,
                    StringValue = strVal
                };
            case JsonTokenType.True:
                return new JsonSpanPrimitive { Start = start, End = (int)reader.BytesConsumed, TokenType = JsonTokenType.True, BoolValue = true };
            case JsonTokenType.False:
                return new JsonSpanPrimitive { Start = start, End = (int)reader.BytesConsumed, TokenType = JsonTokenType.False, BoolValue = false };
            default:
                return new JsonSpanPrimitive { Start = start, End = (int)reader.BytesConsumed, TokenType = reader.TokenType };
        }
    }

    private static JsonSpanObject ReadObjectSpan(ref Utf8JsonReader reader, int depth)
    {
        int start = (int)reader.TokenStartIndex;
        var props = new List<JsonSpanProperty>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                var obj = new JsonSpanObject
                {
                    Start = start,
                    CloseBraceIndex = (int)reader.TokenStartIndex,
                    End = (int)reader.BytesConsumed,
                    TokenType = JsonTokenType.StartObject
                };
                obj.Properties.AddRange(props);
                return obj;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            int nameStart = (int)reader.TokenStartIndex;
            int nameEnd = (int)reader.BytesConsumed;
            string name = reader.GetString()!;

            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON after property name.");
            }

            JsonSpanNode value = SkipOrReadNode(ref reader, depth + 1);
            props.Add(new JsonSpanProperty
            {
                Name = name,
                NameStart = nameStart,
                NameEnd = nameEnd,
                Value = value
            });
        }

        throw new JsonException("Unterminated JSON object.");
    }

    private static JsonSpanArray ReadArraySpan(ref Utf8JsonReader reader, int depth)
    {
        int start = (int)reader.TokenStartIndex;
        var elems = new List<JsonSpanNode>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                var arr = new JsonSpanArray
                {
                    Start = start,
                    CloseBracketIndex = (int)reader.TokenStartIndex,
                    End = (int)reader.BytesConsumed,
                    TokenType = JsonTokenType.StartArray
                };
                arr.Elements.AddRange(elems);
                return arr;
            }

            elems.Add(SkipOrReadNode(ref reader, depth + 1));
        }

        throw new JsonException("Unterminated JSON array.");
    }

    private static bool IsAsciiWhitespaceOnly(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
                return false;
        }
        return true;
    }

    private static bool TryGetUniqueProperty(JsonSpanObject obj, string name, out JsonSpanProperty? property, out bool isDuplicate)
    {
        property = null;
        isDuplicate = false;
        foreach (var p in obj.Properties)
        {
            if (string.Equals(p.Name, name, StringComparison.Ordinal))
            {
                if (property != null)
                {
                    isDuplicate = true;
                    property = null;
                    return false;
                }
                property = p;
            }
        }
        return property != null;
    }

    private static bool HasProperty(JsonSpanObject obj, string name)
    {
        foreach (var p in obj.Properties)
        {
            if (string.Equals(p.Name, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool TryGetStringProperty(JsonSpanObject obj, string name, out string? value)
    {
        value = null;
        if (TryGetUniqueProperty(obj, name, out JsonSpanProperty? prop, out _) &&
            prop?.Value is JsonSpanPrimitive prim &&
            prim.TokenType == JsonTokenType.String)
        {
            value = prim.StringValue;
            return true;
        }
        return false;
    }

    private static bool TryGetBoolProperty(JsonSpanObject obj, string name, out bool value)
    {
        value = false;
        if (TryGetUniqueProperty(obj, name, out JsonSpanProperty? prop, out _) &&
            prop?.Value is JsonSpanPrimitive prim &&
            prim.BoolValue.HasValue)
        {
            value = prim.BoolValue.Value;
            return true;
        }
        return false;
    }

    private static JsonDocStyle DetectJsonDocStyle(ReadOnlySpan<byte> body, JsonSpanObject root)
    {
        int lfIdx = body.IndexOf((byte)'\n');
        bool isCompact = lfIdx < 0;
        string newLine = "\n";
        if (!isCompact)
        {
            newLine = lfIdx > 0 && body[lfIdx - 1] == (byte)'\r' ? "\r\n" : "\n";
        }

        string indentUnit = "  ";
        if (!isCompact && root.Properties.Count > 0)
        {
            string rootIndent = GetLineIndent(body, root.Start);
            string firstPropIndent = GetLineIndent(body, root.Properties[0].NameStart);
            if (firstPropIndent.Length > rootIndent.Length && firstPropIndent.StartsWith(rootIndent, StringComparison.Ordinal))
            {
                indentUnit = firstPropIndent.Substring(rootIndent.Length);
            }
            else if (firstPropIndent.Length > 0)
            {
                indentUnit = firstPropIndent;
            }
        }

        string colonSep = isCompact ? ":" : ": ";
        if (root.Properties.Count > 0)
        {
            var p = root.Properties[0];
            if (p.Value.Start > p.NameEnd)
            {
                string between = Encoding.ASCII.GetString(body.Slice(p.NameEnd, p.Value.Start - p.NameEnd));
                if (between.Contains(':', StringComparison.Ordinal))
                {
                    colonSep = between;
                }
            }
        }

        return new JsonDocStyle(isCompact, newLine, indentUnit, colonSep);
    }

    private static string GetLineIndent(ReadOnlySpan<byte> body, int pos)
    {
        int clamped = Math.Clamp(pos, 0, body.Length);
        int lineStart = clamped;
        while (lineStart > 0 && body[lineStart - 1] != (byte)'\n')
        {
            lineStart--;
        }

        int wsEnd = lineStart;
        while (wsEnd < body.Length && (body[wsEnd] == (byte)' ' || body[wsEnd] == (byte)'\t'))
        {
            wsEnd++;
        }

        return Encoding.ASCII.GetString(body.Slice(lineStart, wsEnd - lineStart));
    }

    private static string GetChildIndentForObject(ReadOnlySpan<byte> body, JsonSpanObject obj, JsonDocStyle style)
    {
        if (style.IsCompact) return "";
        if (obj.Properties.Count > 0)
        {
            string existingIndent = GetLineIndent(body, obj.Properties[0].NameStart);
            if (existingIndent.Length > 0 || GetLineIndent(body, obj.Start).Length == 0)
            {
                // Ensure it is on its own line; if inline like {"a": 1}, fall back to parent + unit
                if (IsFirstNonWsOnLine(body, obj.Properties[0].NameStart))
                {
                    return existingIndent;
                }
            }
        }
        return GetLineIndent(body, obj.Start) + style.IndentUnit;
    }

    private static string GetChildIndentForArray(ReadOnlySpan<byte> body, JsonSpanArray arr, JsonDocStyle style)
    {
        if (style.IsCompact) return "";
        if (arr.Elements.Count > 0 && IsFirstNonWsOnLine(body, arr.Elements[0].Start))
        {
            return GetLineIndent(body, arr.Elements[0].Start);
        }
        return GetLineIndent(body, arr.Start) + style.IndentUnit;
    }

    private static bool IsFirstNonWsOnLine(ReadOnlySpan<byte> body, int pos)
    {
        int i = Math.Clamp(pos, 0, body.Length) - 1;
        while (i >= 0 && body[i] != (byte)'\n')
        {
            if (body[i] != (byte)' ' && body[i] != (byte)'\t' && body[i] != (byte)'\r')
                return false;
            i--;
        }
        return true;
    }

    private static void AddInsertPropertyIntoObjectEdit(
        List<ByteEdit> edits,
        ReadOnlySpan<byte> body,
        JsonSpanObject obj,
        string formattedProps,
        JsonDocStyle style,
        string childIndent,
        string objIndent)
    {
        if (obj.Properties.Count == 0)
        {
            string replacement = style.IsCompact
                ? formattedProps
                : style.NewLine + childIndent + formattedProps + style.NewLine + objIndent;
            edits.Add(new ByteEdit(obj.Start + 1, obj.CloseBraceIndex, Utf8NoBom.GetBytes(replacement)));
        }
        else
        {
            int insertPos = obj.Properties[^1].FullEnd;
            string prefix = style.IsCompact ? "," : "," + style.NewLine + childIndent;
            edits.Add(new ByteEdit(insertPos, insertPos, Utf8NoBom.GetBytes(prefix + formattedProps)));
        }
    }

    private static void AddInsertElementIntoArrayEdit(
        List<ByteEdit> edits,
        ReadOnlySpan<byte> body,
        JsonSpanArray arr,
        string formattedElem,
        JsonDocStyle style,
        string elemIndent,
        string arrIndent)
    {
        if (arr.Elements.Count == 0)
        {
            string replacement = style.IsCompact
                ? formattedElem
                : style.NewLine + elemIndent + formattedElem + style.NewLine + arrIndent;
            edits.Add(new ByteEdit(arr.Start + 1, arr.CloseBracketIndex, Utf8NoBom.GetBytes(replacement)));
        }
        else
        {
            int insertPos = arr.Elements[^1].End;
            string prefix = style.IsCompact ? "," : "," + style.NewLine + elemIndent;
            edits.Add(new ByteEdit(insertPos, insertPos, Utf8NoBom.GetBytes(prefix + formattedElem)));
        }
    }

    private static void AddRemoveObjectPropertiesEdits(
        List<ByteEdit> edits,
        JsonSpanObject obj,
        HashSet<int> indicesToRemove)
    {
        var kept = new List<int>(obj.Properties.Count);
        for (int i = 0; i < obj.Properties.Count; i++)
        {
            if (!indicesToRemove.Contains(i))
            {
                kept.Add(i);
            }
        }

        if (kept.Count == 0)
        {
            edits.Add(new ByteEdit(obj.Start + 1, obj.CloseBraceIndex, Array.Empty<byte>()));
            return;
        }

        // Remove leading deleted properties before first kept property
        if (kept[0] > 0)
        {
            edits.Add(new ByteEdit(obj.Properties[0].FullStart, obj.Properties[kept[0]].FullStart, Array.Empty<byte>()));
        }

        // Remove middle gaps between consecutive kept properties
        for (int k = 0; k < kept.Count - 1; k++)
        {
            int left = kept[k];
            int right = kept[k + 1];
            if (right > left + 1)
            {
                edits.Add(new ByteEdit(obj.Properties[left].FullEnd, obj.Properties[right - 1].FullEnd, Array.Empty<byte>()));
            }
        }

        // Remove trailing deleted properties after last kept property
        int lastKept = kept[^1];
        if (lastKept < obj.Properties.Count - 1)
        {
            edits.Add(new ByteEdit(obj.Properties[lastKept].FullEnd, obj.Properties[^1].FullEnd, Array.Empty<byte>()));
        }
    }

    private static void AddRemoveArrayElementsEdits(
        List<ByteEdit> edits,
        JsonSpanArray arr,
        HashSet<int> indicesToRemove)
    {
        var kept = new List<int>(arr.Elements.Count);
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            if (!indicesToRemove.Contains(i))
            {
                kept.Add(i);
            }
        }

        if (kept.Count == 0)
        {
            edits.Add(new ByteEdit(arr.Start + 1, arr.CloseBracketIndex, Array.Empty<byte>()));
            return;
        }

        if (kept[0] > 0)
        {
            edits.Add(new ByteEdit(arr.Elements[0].Start, arr.Elements[kept[0]].Start, Array.Empty<byte>()));
        }

        for (int k = 0; k < kept.Count - 1; k++)
        {
            int left = kept[k];
            int right = kept[k + 1];
            if (right > left + 1)
            {
                edits.Add(new ByteEdit(arr.Elements[left].End, arr.Elements[right - 1].End, Array.Empty<byte>()));
            }
        }

        int lastKept = kept[^1];
        if (lastKept < arr.Elements.Count - 1)
        {
            edits.Add(new ByteEdit(arr.Elements[lastKept].End, arr.Elements[^1].End, Array.Empty<byte>()));
        }
    }

    private static byte[] ApplyByteEdits(byte[] originalBytes, int bomLength, List<ByteEdit> edits)
    {
        edits.Sort((a, b) =>
        {
            int cmp = a.Start.CompareTo(b.Start);
            return cmp != 0 ? cmp : a.End.CompareTo(b.End);
        });

        int bodyLen = originalBytes.Length - bomLength;
        using var ms = new MemoryStream(originalBytes.Length + 512);

        if (bomLength > 0)
        {
            ms.Write(originalBytes, 0, bomLength);
        }

        int cursor = 0;
        foreach (var edit in edits)
        {
            if (edit.Start < cursor || edit.End < edit.Start || edit.End > bodyLen)
            {
                throw new InvalidOperationException($"Overlapping or out-of-range JSON splice edit [{edit.Start}..{edit.End}] after cursor {cursor}.");
            }

            if (edit.Start > cursor)
            {
                ms.Write(originalBytes, bomLength + cursor, edit.Start - cursor);
            }

            if (edit.Replacement.Length > 0)
            {
                ms.Write(edit.Replacement, 0, edit.Replacement.Length);
            }

            cursor = edit.End;
        }

        if (cursor < bodyLen)
        {
            ms.Write(originalBytes, bomLength + cursor, bodyLen - cursor);
        }

        return ms.ToArray();
    }

    private byte[] BuildFreshHooksJsonDocument(HookEventSpec[] specs, string agentId, string emitterScriptPath)
    {
        var style = new JsonDocStyle(IsCompact: false, Environment.NewLine, "  ", ": ");
        string hooksProp = FormatHooksProperty(specs, agentId, emitterScriptPath, style, "  ");
        string json = "{" + style.NewLine + "  " + hooksProp + style.NewLine + "}" + style.NewLine;
        return Utf8NoBom.GetBytes(json);
    }

    private string FormatHooksProperty(
        HookEventSpec[] specs,
        string agentId,
        string emitterScriptPath,
        JsonDocStyle style,
        string propIndent)
    {
        string innerIndent = style.IsCompact ? "" : propIndent + style.IndentUnit;
        var eventParts = new List<string>(specs.Length);
        foreach (var spec in specs)
        {
            string cmd = BuildHookCommand(emitterScriptPath, spec.EventType, agentId, spec.Message);
            eventParts.Add(FormatEventProperty(spec, cmd, style, innerIndent));
        }

        if (style.IsCompact)
        {
            return $"\"hooks\"{style.ColonSep}{{{string.Join(",", eventParts)}}}";
        }

        return $"\"hooks\"{style.ColonSep}{{{style.NewLine}{innerIndent}" +
               string.Join($",{style.NewLine}{innerIndent}", eventParts) +
               $"{style.NewLine}{propIndent}}}";
    }

    private static string FormatEventProperty(
        HookEventSpec spec,
        string desiredCommand,
        JsonDocStyle style,
        string propIndent)
    {
        string groupIndent = style.IsCompact ? "" : propIndent + style.IndentUnit;
        string groupJson = FormatWrapperGroup(spec, desiredCommand, style, groupIndent);
        string key = EncodeJsonString(spec.EventKey);

        if (style.IsCompact)
        {
            return $"{key}{style.ColonSep}[{groupJson}]";
        }

        return $"{key}{style.ColonSep}[{style.NewLine}{groupIndent}{groupJson}{style.NewLine}{propIndent}]";
    }

    private static string FormatWrapperGroup(
        HookEventSpec spec,
        string desiredCommand,
        JsonDocStyle style,
        string groupIndent)
    {
        string propIndent = style.IsCompact ? "" : groupIndent + style.IndentUnit;
        string hookIndent = style.IsCompact ? "" : propIndent + style.IndentUnit;
        string hookJson = FormatHookObject(desiredCommand, spec.AsyncDispatch, style, hookIndent);

        if (style.IsCompact)
        {
            if (!string.IsNullOrEmpty(spec.Matcher))
            {
                return $"{{\"matcher\"{style.ColonSep}{EncodeJsonString(spec.Matcher)},\"hooks\"{style.ColonSep}[{hookJson}]}}";
            }
            return $"{{\"hooks\"{style.ColonSep}[{hookJson}]}}";
        }

        var sb = new StringBuilder();
        sb.Append('{').Append(style.NewLine);
        if (!string.IsNullOrEmpty(spec.Matcher))
        {
            sb.Append(propIndent).Append("\"matcher\"").Append(style.ColonSep).Append(EncodeJsonString(spec.Matcher)).Append(',').Append(style.NewLine);
        }
        sb.Append(propIndent).Append("\"hooks\"").Append(style.ColonSep).Append('[').Append(style.NewLine);
        sb.Append(hookIndent).Append(hookJson).Append(style.NewLine);
        sb.Append(propIndent).Append(']').Append(style.NewLine);
        sb.Append(groupIndent).Append('}');
        return sb.ToString();
    }

    private static string FormatHookObject(
        string desiredCommand,
        bool asyncDispatch,
        JsonDocStyle style,
        string hookIndent)
    {
        string propIndent = style.IsCompact ? "" : hookIndent + style.IndentUnit;
        string encodedCmd = EncodeJsonString(desiredCommand);

        if (style.IsCompact)
        {
            return asyncDispatch
                ? $"{{\"type\"{style.ColonSep}\"command\",\"command\"{style.ColonSep}{encodedCmd},\"async\"{style.ColonSep}true}}"
                : $"{{\"type\"{style.ColonSep}\"command\",\"command\"{style.ColonSep}{encodedCmd}}}";
        }

        var sb = new StringBuilder();
        sb.Append('{').Append(style.NewLine);
        sb.Append(propIndent).Append("\"type\"").Append(style.ColonSep).Append("\"command\",").Append(style.NewLine);
        sb.Append(propIndent).Append("\"command\"").Append(style.ColonSep).Append(encodedCmd);
        if (asyncDispatch)
        {
            sb.Append(',').Append(style.NewLine);
            sb.Append(propIndent).Append("\"async\"").Append(style.ColonSep).Append("true");
        }
        sb.Append(style.NewLine).Append(hookIndent).Append('}');
        return sb.ToString();
    }

    private static string EncodeJsonString(string value)
    {
        return "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value + "\"";
    }

    // ------------------------------------------------------------------------
    // Container Provenance Store (stored in KageDesk AppData, never polluting user configs)
    // ------------------------------------------------------------------------

    private sealed record ContainerProvenance(
        bool CreatedHooksObject,
        HashSet<string> CreatedEventKeys
    )
    {
        public static ContainerProvenance Unknown => new(false, new HashSet<string>(StringComparer.Ordinal));
    }

    private ContainerProvenance LoadContainerProvenance(string targetConfigPath)
    {
        try
        {
            string storePath = ProvenanceStorePath;
            if (!File.Exists(storePath))
                return ContainerProvenance.Unknown;

            ValidateNoReparsePointsInPath(storePath);
            Dictionary<string, ContainerProvenance> map = ReadProvenanceMap(File.ReadAllBytes(storePath));
            string key = Path.GetFullPath(targetConfigPath).ToUpperInvariant();
            return map.TryGetValue(key, out ContainerProvenance? found) ? found : ContainerProvenance.Unknown;
        }
        catch
        {
            // Conservative fallback: when provenance cannot be read, treat ownership as unproven
            return ContainerProvenance.Unknown;
        }
    }

    private void SaveContainerProvenance(string targetConfigPath, ContainerProvenance provenance)
    {
        try
        {
            string storePath = ProvenanceStorePath;
            string? dir = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrEmpty(dir))
            {
                EnsureDirectorySafe(dir);
            }

            WithTargetLock(storePath, () =>
            {
                ValidateNoReparsePointsInPath(storePath);
                Dictionary<string, ContainerProvenance> map = new(StringComparer.Ordinal);
                bool existed = File.Exists(storePath);
                if (existed)
                {
                    try
                    {
                        map = ReadProvenanceMap(File.ReadAllBytes(storePath));
                    }
                    catch { }
                }

                string key = Path.GetFullPath(targetConfigPath).ToUpperInvariant();
                map[key] = provenance;

                byte[] bytes = SerializeProvenanceMap(map);
                WriteInternalBytesAtomic(storePath, bytes, fileExisted: existed);
            });
        }
        catch
        {
            // Non-fatal: provenance failure simply falls back to conservative empty-container preservation
        }
    }

    private void ClearContainerProvenance(string targetConfigPath)
    {
        try
        {
            string storePath = ProvenanceStorePath;
            if (!File.Exists(storePath)) return;

            WithTargetLock(storePath, () =>
            {
                ValidateNoReparsePointsInPath(storePath);
                if (!File.Exists(storePath)) return;

                Dictionary<string, ContainerProvenance> map = ReadProvenanceMap(File.ReadAllBytes(storePath));
                string key = Path.GetFullPath(targetConfigPath).ToUpperInvariant();
                if (!map.Remove(key))
                    return;

                byte[] bytes = SerializeProvenanceMap(map);
                WriteInternalBytesAtomic(storePath, bytes, fileExisted: true);
            });
        }
        catch
        {
            // Non-fatal
        }
    }

    private static Dictionary<string, ContainerProvenance> ReadProvenanceMap(byte[] raw)
    {
        var map = new Dictionary<string, ContainerProvenance>(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return map;

        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;

            bool createdHooks = prop.Value.TryGetProperty("createdHooksObject", out JsonElement chEl) &&
                                chEl.ValueKind == JsonValueKind.True;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (prop.Value.TryGetProperty("createdEventKeys", out JsonElement keysEl) &&
                keysEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in keysEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? s = item.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            keys.Add(s);
                        }
                    }
                }
            }

            map[prop.Name] = new ContainerProvenance(createdHooks, keys);
        }

        return map;
    }

    private static byte[] SerializeProvenanceMap(Dictionary<string, ContainerProvenance> map)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, ContainerProvenance> kvp in map.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartObject();
                writer.WriteBoolean("createdHooksObject", kvp.Value.CreatedHooksObject);
                writer.WritePropertyName("createdEventKeys");
                writer.WriteStartArray();
                foreach (string k in kvp.Value.CreatedEventKeys.OrderBy(x => x, StringComparer.Ordinal))
                {
                    writer.WriteStringValue(k);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------------
    // Path Validation, Single-Read Snapshot, Post-Flush Check & Coupled Backup Replace
    // ------------------------------------------------------------------------

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
        byte[]? Sha256,
        byte[]? OriginalBytes
    );

    /// <summary>
    /// Reads target file ONCE into OriginalBytes and computes SHA-256 over that exact buffer.
    /// Callers MUST parse and validate exclusively from OriginalBytes (never performing a second read).
    /// </summary>
    private static FileSnapshot CaptureSnapshot(string path)
    {
        if (!File.Exists(path))
        {
            return new FileSnapshot(false, 0, DateTime.MinValue, null, null);
        }

        byte[] bytes = File.ReadAllBytes(path);
        byte[] hash = SHA256.HashData(bytes);
        var fi = new FileInfo(path);
        return new FileSnapshot(true, bytes.Length, fi.LastWriteTimeUtc, hash, bytes);
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

        if (expected.OriginalBytes != null && !bytes.AsSpan().SequenceEqual(expected.OriginalBytes))
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

    /// <summary>
    /// Stages, writes, and flushes the temp file to disk FIRST, then performs the final identity/content check
    /// against <paramref name="expectedSnapshot"/> immediately before committing.
    /// Note: Windows File.Replace is not an OS-level compare-and-swap against external writers, so the pre-replace
    /// check minimizes the conflict window while the coupled unique timestamped backup preserves any replaced
    /// version for conflict recovery; metadata errors are propagated strictly (ignoreMetadataErrors: false).
    /// </summary>
    private bool CommitBytesWithPreReplaceCheck(
        string targetPath,
        byte[] bytes,
        string? backupPath,
        FileSnapshot expectedSnapshot)
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
            // 1. Stage, write, and Flush(true) temp file FIRST so I/O latency is outside the final check window
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // 2. Test seam + final identity/content check immediately before File.Replace / File.Move
            BeforeCommitHookForTest?.Invoke(targetPath);
            if (!MatchesSnapshot(targetPath, expectedSnapshot))
            {
                return false;
            }

            // 3. Commit with coupled unique backup for conflict recovery and strict metadata propagation
            if (expectedSnapshot.Exists)
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: backupPath, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(tempPath, targetPath, overwrite: false);
            }

            return true;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void WriteInternalBytesAtomic(string targetPath, byte[] bytes, bool fileExisted)
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
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
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
