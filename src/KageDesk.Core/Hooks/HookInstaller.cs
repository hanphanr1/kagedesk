using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    public bool AllSucceeded => Results.All(r => r.Status != HookActionStatus.Failed);
    public int ChangedCount => Results.Count(r => r.Status is HookActionStatus.Installed or HookActionStatus.Uninstalled);
}

public sealed class HookInstaller
{
    private const string EmitterMarkerPs1 = "kagedesk-emit.ps1";
    private const string EmitterMarkerSh = "kagedesk-emit.sh";

    private readonly string _userProfileDir;
    private readonly string _appDataDir;
    private readonly int _bridgePort;
    private readonly Func<DateTimeOffset> _clock;

    public HookInstaller(
        string? userProfileDir = null,
        string? appDataDir = null,
        int bridgePort = 4319,
        Func<DateTimeOffset>? clock = null)
    {
        _userProfileDir = string.IsNullOrWhiteSpace(userProfileDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileDir;
        _appDataDir = string.IsNullOrWhiteSpace(appDataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KageDesk")
            : appDataDir;
        _bridgePort = bridgePort is >= 1 and <= 65535 ? bridgePort : 4319;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string DefaultEmitterScriptPath => Path.Combine(_appDataDir, "hooks", EmitterMarkerPs1);

    public HookOperationSummary InstallAll(string? explicitEmitterScriptPath = null)
    {
        string emitterPath = EnsureEmitterScript(explicitEmitterScriptPath);
        var results = new List<HookTargetResult>(3)
        {
            InstallClaudeCode(emitterPath),
            InstallCodex(emitterPath),
            InstallOpenCode(emitterPath)
        };
        return new HookOperationSummary(true, emitterPath, results);
    }

    public HookOperationSummary UninstallAll()
    {
        string emitterPath = DefaultEmitterScriptPath;
        var results = new List<HookTargetResult>(3)
        {
            UninstallClaudeCode(),
            UninstallCodex(),
            UninstallOpenCode()
        };
        return new HookOperationSummary(false, emitterPath, results);
    }

    public string EnsureEmitterScript(string? explicitEmitterScriptPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitEmitterScriptPath))
        {
            return Path.GetFullPath(explicitEmitterScriptPath);
        }

        string targetPath = DefaultEmitterScriptPath;
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Write atomically so running hooks never see a half-written script
        WriteTextAtomic(targetPath, EmbeddedEmitterScript);
        return targetPath;
    }

    public HookTargetResult InstallClaudeCode(string emitterScriptPath)
    {
        string claudeDir = Path.Combine(_userProfileDir, ".claude");
        string settingsPath = Path.Combine(claudeDir, "settings.json");

        try
        {
            Directory.CreateDirectory(claudeDir);
            return InstallHookJsonFile(
                HookTargetKind.ClaudeCode,
                settingsPath,
                agentId: "claude",
                emitterScriptPath: emitterScriptPath);
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.ClaudeCode, settingsPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult UninstallClaudeCode()
    {
        string settingsPath = Path.Combine(_userProfileDir, ".claude", "settings.json");
        return UninstallHookJsonFile(HookTargetKind.ClaudeCode, settingsPath);
    }

    public HookTargetResult InstallCodex(string emitterScriptPath)
    {
        string codexDir = Path.Combine(_userProfileDir, ".codex");
        string hooksPath = Path.Combine(codexDir, "hooks.json");

        if (!Directory.Exists(codexDir))
        {
            return new HookTargetResult(
                HookTargetKind.Codex,
                hooksPath,
                HookActionStatus.SkippedNotPresent,
                "Codex config directory not detected (~/.codex).");
        }

        try
        {
            return InstallHookJsonFile(
                HookTargetKind.Codex,
                hooksPath,
                agentId: "codex",
                emitterScriptPath: emitterScriptPath);
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.Codex, hooksPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult UninstallCodex()
    {
        string hooksPath = Path.Combine(_userProfileDir, ".codex", "hooks.json");
        return UninstallHookJsonFile(HookTargetKind.Codex, hooksPath);
    }

    public HookTargetResult InstallOpenCode(string emitterScriptPath)
    {
        string configDir = Path.Combine(_userProfileDir, ".config", "opencode");
        string legacyDir = Path.Combine(_userProfileDir, ".opencode");
        string configPath;

        if (Directory.Exists(configDir))
        {
            configPath = Path.Combine(configDir, "opencode.json");
        }
        else if (Directory.Exists(legacyDir))
        {
            configPath = Path.Combine(legacyDir, "settings.json");
        }
        else
        {
            return new HookTargetResult(
                HookTargetKind.OpenCode,
                Path.Combine(configDir, "opencode.json"),
                HookActionStatus.SkippedNotPresent,
                "OpenCode config directory not detected.");
        }

        try
        {
            return InstallHookJsonFile(
                HookTargetKind.OpenCode,
                configPath,
                agentId: "opencode",
                emitterScriptPath: emitterScriptPath);
        }
        catch (Exception ex)
        {
            return new HookTargetResult(HookTargetKind.OpenCode, configPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    public HookTargetResult UninstallOpenCode()
    {
        string primaryPath = Path.Combine(_userProfileDir, ".config", "opencode", "opencode.json");
        if (File.Exists(primaryPath))
        {
            return UninstallHookJsonFile(HookTargetKind.OpenCode, primaryPath);
        }

        string legacyPath = Path.Combine(_userProfileDir, ".opencode", "settings.json");
        if (File.Exists(legacyPath))
        {
            return UninstallHookJsonFile(HookTargetKind.OpenCode, legacyPath);
        }

        return new HookTargetResult(
            HookTargetKind.OpenCode,
            primaryPath,
            HookActionStatus.SkippedNotPresent,
            "OpenCode config file not found.");
    }

    private HookTargetResult InstallHookJsonFile(
        HookTargetKind target,
        string configPath,
        string agentId,
        string emitterScriptPath)
    {
        bool fileExisted = File.Exists(configPath);
        JsonObject root = LoadOrCreateJsonObject(configPath);

        JsonObject hooksObj;
        if (root["hooks"] is JsonObject existingHooks)
        {
            hooksObj = existingHooks;
        }
        else if (root["hooks"] == null)
        {
            hooksObj = new JsonObject();
            root["hooks"] = hooksObj;
        }
        else
        {
            return new HookTargetResult(
                target,
                configPath,
                HookActionStatus.Failed,
                "Existing 'hooks' property is not a JSON object; aborted to preserve user config.");
        }

        var specs = new (string EventKey, string EventType, string Message)[]
        {
            ("PreToolUse", "tool_start", "Tool execution started"),
            ("PostToolUse", "tool_done", "Tool execution completed"),
            ("Notification", "notification", "Notification received"),
            ("Stop", "idle", "Agent stopped")
        };

        bool modified = false;
        foreach (var (eventKey, eventType, message) in specs)
        {
            string command = BuildHookCommand(emitterScriptPath, eventType, agentId, message);
            if (UpsertHookEvent(hooksObj, eventKey, command))
            {
                modified = true;
            }
        }

        if (!modified && fileExisted)
        {
            return new HookTargetResult(
                target,
                configPath,
                HookActionStatus.AlreadyInstalled,
                "KageDesk hooks already installed and up to date.");
        }

        string? backupPath = fileExisted ? CreateBackupBeforeModification(configPath) : null;
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        WriteTextAtomic(configPath, json);

        return new HookTargetResult(
            target,
            configPath,
            HookActionStatus.Installed,
            backupPath != null
                ? $"Installed KageDesk hooks (backup saved to {Path.GetFileName(backupPath)})."
                : "Installed KageDesk hooks.",
            backupPath);
    }

    private HookTargetResult UninstallHookJsonFile(HookTargetKind target, string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new HookTargetResult(
                target,
                configPath,
                HookActionStatus.SkippedNotPresent,
                "Config file does not exist.");
        }

        try
        {
            JsonObject root = LoadOrCreateJsonObject(configPath);
            if (root["hooks"] is not JsonObject hooksObj)
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.AlreadyRemoved,
                    "No KageDesk hooks found.");
            }

            bool modified = RemoveKageDeskEntries(hooksObj);
            if (!modified)
            {
                return new HookTargetResult(
                    target,
                    configPath,
                    HookActionStatus.AlreadyRemoved,
                    "No KageDesk hooks found.");
            }

            // Clean up empty "hooks" object only if all event keys became empty
            if (hooksObj.Count == 0)
            {
                root.Remove("hooks");
            }

            string? backupPath = CreateBackupBeforeModification(configPath);
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            WriteTextAtomic(configPath, json);

            return new HookTargetResult(
                target,
                configPath,
                HookActionStatus.Uninstalled,
                backupPath != null
                    ? $"Removed KageDesk hooks (backup saved to {Path.GetFileName(backupPath)})."
                    : "Removed KageDesk hooks.",
                backupPath);
        }
        catch (Exception ex)
        {
            return new HookTargetResult(target, configPath, HookActionStatus.Failed, $"Failed: {ex.Message}");
        }
    }

    private string BuildHookCommand(string emitterScriptPath, string eventType, string agentId, string message)
    {
        string escapedScript = emitterScriptPath.Replace("\"", "\\\"");
        string portArg = _bridgePort != 4319 ? $" -Port {_bridgePort}" : "";
        return $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{escapedScript}\" -Type {eventType} -AgentId {agentId} -Message \"{message}\"{portArg}";
    }

    private static bool UpsertHookEvent(JsonObject hooksObj, string eventKey, string desiredCommand)
    {
        JsonArray eventArr;
        if (hooksObj[eventKey] is JsonArray existingArr)
        {
            eventArr = existingArr;
        }
        else if (hooksObj[eventKey] == null)
        {
            eventArr = new JsonArray();
            hooksObj[eventKey] = eventArr;
        }
        else
        {
            // Unexpected non-array format; do not clobber user config
            return false;
        }

        // Check if a KageDesk hook already exists under this eventKey
        foreach (JsonNode? entryNode in eventArr)
        {
            if (entryNode is not JsonObject groupObj) continue;

            // Format A: { "hooks": [ { "type": "command", "command": "..." } ] } (Claude Code standard)
            if (groupObj["hooks"] is JsonArray innerHooks)
            {
                foreach (JsonNode? innerNode in innerHooks)
                {
                    if (innerNode is JsonObject hookObj && IsKageDeskHookObject(hookObj))
                    {
                        string? existingCmd = hookObj["command"]?.GetValue<string>();
                        if (string.Equals(existingCmd, desiredCommand, StringComparison.Ordinal))
                        {
                            return false; // Already exact match
                        }

                        hookObj["type"] = "command";
                        hookObj["command"] = desiredCommand;
                        return true;
                    }
                }
            }
            // Format B: Direct hook object { "type": "command", "command": "..." }
            else if (IsKageDeskHookObject(groupObj))
            {
                string? existingCmd = groupObj["command"]?.GetValue<string>();
                if (string.Equals(existingCmd, desiredCommand, StringComparison.Ordinal))
                {
                    return false;
                }

                groupObj["type"] = "command";
                groupObj["command"] = desiredCommand;
                return true;
            }
        }

        // Not found — append a clean Claude Code-compatible hook group entry without extra schema fields
        var newGroup = new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = desiredCommand
                }
            }
        };
        eventArr.Add(newGroup);
        return true;
    }

    private static bool RemoveKageDeskEntries(JsonObject hooksObj)
    {
        bool modified = false;
        var emptyEventKeys = new List<string>();

        foreach (var kv in hooksObj)
        {
            if (kv.Value is not JsonArray eventArr) continue;

            for (int i = eventArr.Count - 1; i >= 0; i--)
            {
                if (eventArr[i] is not JsonObject groupObj) continue;

                if (groupObj["hooks"] is JsonArray innerHooks)
                {
                    for (int j = innerHooks.Count - 1; j >= 0; j--)
                    {
                        if (innerHooks[j] is JsonObject hookObj && IsKageDeskHookObject(hookObj))
                        {
                            innerHooks.RemoveAt(j);
                            modified = true;
                        }
                    }

                    // Remove wrapper group only if we emptied it and it has no matcher/other hooks
                    if (innerHooks.Count == 0 && groupObj.Count == 1)
                    {
                        eventArr.RemoveAt(i);
                        modified = true;
                    }
                }
                else if (IsKageDeskHookObject(groupObj))
                {
                    eventArr.RemoveAt(i);
                    modified = true;
                }
            }

            if (eventArr.Count == 0)
            {
                emptyEventKeys.Add(kv.Key);
            }
        }

        foreach (string key in emptyEventKeys)
        {
            hooksObj.Remove(key);
        }

        return modified;
    }

    private static bool IsKageDeskHookObject(JsonObject obj)
    {
        if (obj["command"] is JsonValue cmdVal && cmdVal.TryGetValue<string>(out string? cmd) && !string.IsNullOrEmpty(cmd))
        {
            return cmd.Contains(EmitterMarkerPs1, StringComparison.OrdinalIgnoreCase)
                || cmd.Contains(EmitterMarkerSh, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static JsonObject LoadOrCreateJsonObject(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        string text = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        JsonNode? parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (parsed is not JsonObject obj)
        {
            throw new InvalidOperationException($"Expected root JSON object in '{path}'.");
        }

        return obj;
    }

    private string CreateBackupBeforeModification(string targetPath)
    {
        string stamp = _clock().ToString("yyyyMMdd-HHmmss");
        string backupPath = $"{targetPath}.{stamp}.kagedesk.bak";
        int suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = $"{targetPath}.{stamp}-{suffix}.kagedesk.bak";
            suffix++;
        }

        File.Copy(targetPath, backupPath, overwrite: false);
        return backupPath;
    }

    private static void WriteTextAtomic(string targetPath, string content)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = Path.Combine(
            dir ?? ".",
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, targetPath);
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
    Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/event" -Method Post -ContentType "application/json" -Headers $headers -Body $json -TimeoutSec 2 | Out-Null
}
catch {
    # Non-blocking: never break user CLI hook execution if KageDesk is not running
}
exit 0
""";
}
