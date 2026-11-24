using System.Text.Json;
using System.Text.RegularExpressions;
using KageDesk.Core.Models;

namespace KageDesk.Core.Collectors;

public record OrcaBotInfo(
    string Id,
    string Name,
    string? Agent,
    bool Paused,
    string? Specialty,
    bool Self
);

public record BotItemInfo(
    int Number,
    string? OwnerBotId,
    string? OwnerName,
    string? Title,
    string? Status,
    string? QuestionPrompt,
    DateTimeOffset? UpdatedAt
);

public static class OrcaSnapshotParser
{
    private static readonly HashSet<string> ApprovalVerdicts = new(StringComparer.OrdinalIgnoreCase)
    {
        "waiting", "approval", "input", "needs-input", "blocked"
    };

    private static readonly HashSet<string> ApprovalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "waiting", "needs-approval"
    };

    public static IReadOnlyList<OrcaBotInfo> ParseBotList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<OrcaBotInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                return Array.Empty<OrcaBotInfo>();

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("bots", out var botsElement) ||
                botsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<OrcaBotInfo>();
            }

            var bots = new List<OrcaBotInfo>();
            foreach (var b in botsElement.EnumerateArray())
            {
                var id = b.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var name = b.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var agent = b.TryGetProperty("agent", out var agentProp) ? agentProp.GetString() : null;
                var paused = b.TryGetProperty("paused", out var pausedProp) && pausedProp.GetBoolean();
                var specialty = b.TryGetProperty("specialty", out var specProp) ? specProp.GetString() : null;
                var self = b.TryGetProperty("self", out var selfProp) && selfProp.GetBoolean();

                if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
                {
                    bots.Add(new OrcaBotInfo(id, name, agent, paused, specialty, self));
                }
            }

            return bots;
        }
        catch (JsonException)
        {
            return Array.Empty<OrcaBotInfo>();
        }
    }

    public static IReadOnlyList<AgentSnapshot> ParseWorktreePs(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<AgentSnapshot>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                return Array.Empty<AgentSnapshot>();

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return Array.Empty<AgentSnapshot>();

            var agents = new List<AgentSnapshot>();

            if (result.TryGetProperty("worktrees", out var worktrees) && worktrees.ValueKind == JsonValueKind.Array)
            {
                foreach (var wt in worktrees.EnumerateArray())
                {
                    ParseWorktreeEntry(wt, now, agents);
                }
            }

            if (result.TryGetProperty("otherBots", out var otherBots) && otherBots.ValueKind == JsonValueKind.Array)
            {
                foreach (var ob in otherBots.EnumerateArray())
                {
                    ParseWorktreeEntry(ob, now, agents);
                }
            }

            return agents;
        }
        catch (JsonException)
        {
            return Array.Empty<AgentSnapshot>();
        }
    }

    private static void ParseWorktreeEntry(JsonElement wt, DateTimeOffset now, List<AgentSnapshot> list)
    {
        var worktreeId = wt.TryGetProperty("worktreeId", out var wId) ? wId.GetString() : null;
        var repo = wt.TryGetProperty("repo", out var rProp) ? rProp.GetString() : null;
        var branch = wt.TryGetProperty("branch", out var brProp) ? brProp.GetString() : null;
        var bot = wt.TryGetProperty("bot", out var botProp) ? botProp.GetString() : null;
        var botHome = wt.TryGetProperty("botHome", out var bhProp) && bhProp.GetBoolean();
        var displayName = wt.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : null;
        var sidebarStatus = wt.TryGetProperty("sidebarStatus", out var ssProp) ? ssProp.GetString() : null;
        var terminalVerdict = wt.TryGetProperty("terminalVerdict", out var tvProp) ? tvProp.GetString() : null;
        var status = wt.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;
        var unread = wt.TryGetProperty("unread", out var unProp) && unProp.GetBoolean();

        DateTimeOffset? lastActivityAt = null;
        if (wt.TryGetProperty("lastActivityAt", out var laProp) && laProp.TryGetInt64(out var laMs) && laMs > 0)
        {
            try { lastActivityAt = DateTimeOffset.FromUnixTimeMilliseconds(laMs); } catch { }
        }

        var (role, spriteKey) = InferRoleAndSprite(bot, displayName, null, botHome, worktreeId);

        // Check if agents array is present
        if (wt.TryGetProperty("agents", out var agentsArr) && agentsArr.ValueKind == JsonValueKind.Array)
        {
            var agentElements = agentsArr.EnumerateArray().ToList();
            if (agentElements.Count > 0)
            {
                foreach (var ag in agentElements)
                {
                    var address = ag.TryGetProperty("address", out var adProp) ? adProp.GetString() : null;
                    var agentStateStr = ag.TryGetProperty("state", out var asProp) ? asProp.GetString() : null;
                    var prompt = ag.TryGetProperty("prompt", out var prProp) ? prProp.GetString() : null;
                    var taskTitle = ag.TryGetProperty("taskTitle", out var ttProp) ? ttProp.GetString() : null;
                    var convName = ag.TryGetProperty("conversationName", out var cnProp) ? cnProp.GetString() : null;
                    var lastMsg = ag.TryGetProperty("lastAssistantMessage", out var lmProp) ? lmProp.GetString() : null;
                    var toolName = ag.TryGetProperty("toolName", out var tnProp) ? tnProp.GetString() : null;

                    string? mainAgentOutcome = null;
                    if (ag.TryGetProperty("mainAgent", out var maProp) && maProp.ValueKind == JsonValueKind.Object)
                    {
                        if (maProp.TryGetProperty("outcome", out var outProp))
                            mainAgentOutcome = outProp.GetString();
                    }

                    DateTimeOffset? updatedAt = null;
                    if (ag.TryGetProperty("updatedAt", out var upProp) && upProp.TryGetInt64(out var upMs) && upMs > 0)
                    {
                        try { updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(upMs); } catch { }
                    }

                    if (ag.TryGetProperty("activity", out var actProp) && actProp.ValueKind == JsonValueKind.Object)
                    {
                        if (actProp.TryGetProperty("unread", out var actUnread) && actUnread.GetBoolean())
                            unread = true;
                        if (string.IsNullOrEmpty(taskTitle) && actProp.TryGetProperty("title", out var actTitle))
                            taskTitle = actTitle.GetString();
                    }

                    var effectiveRole = role;
                    var effectiveSprite = spriteKey;
                    if (role != AgentRole.Clone)
                    {
                        var (r, s) = InferRoleAndSprite(bot, displayName, convName, botHome, worktreeId);
                        effectiveRole = r;
                        effectiveSprite = s;
                    }

                    var agentState = ComputeState(status, sidebarStatus, terminalVerdict, agentStateStr, mainAgentOutcome, updatedAt, now);

                    var id = !string.IsNullOrEmpty(address) ? address : (worktreeId ?? bot ?? displayName ?? Guid.NewGuid().ToString());
                    var name = !string.IsNullOrEmpty(displayName) ? displayName : (bot ?? convName ?? "Orca Worker");

                    list.Add(new AgentSnapshot(
                        Id: id,
                        Name: name,
                        Role: effectiveRole,
                        SpriteKey: effectiveSprite,
                        State: agentState,
                        WorktreeId: worktreeId,
                        Repo: repo,
                        Branch: branch,
                        ToolName: toolName,
                        TaskTitle: taskTitle,
                        Prompt: prompt,
                        LastMessage: lastMsg,
                        LastActivityAt: lastActivityAt,
                        UpdatedAt: updatedAt,
                        IsBotHome: botHome,
                        Unread: unread,
                        Source: "orca"
                    ));
                }
                return;
            }
        }

        // If no agents in worktree array, treat worktree itself as an agent
        var wtState = ComputeState(status, sidebarStatus, terminalVerdict, null, null, lastActivityAt, now);
        var fallbackId = worktreeId ?? bot ?? displayName ?? Guid.NewGuid().ToString();
        var fallbackName = displayName ?? bot ?? "Orca Worker";

        list.Add(new AgentSnapshot(
            Id: fallbackId,
            Name: fallbackName,
            Role: role,
            SpriteKey: spriteKey,
            State: wtState,
            WorktreeId: worktreeId,
            Repo: repo,
            Branch: branch,
            LastActivityAt: lastActivityAt,
            UpdatedAt: lastActivityAt,
            IsBotHome: botHome,
            Unread: unread,
            Source: "orca"
        ));
    }

    public static (AgentRole Role, string SpriteKey) InferRoleAndSprite(
        string? bot,
        string? displayName,
        string? conversationName,
        bool isBotHome,
        string? worktreeId)
    {
        // If not botHome or worktreeId starts with wt: (child worker), classify as Clone
        if (!isBotHome || (worktreeId != null && worktreeId.StartsWith("wt:", StringComparison.OrdinalIgnoreCase)))
        {
            return (AgentRole.Clone, "clone");
        }

        var text = $"{bot} {displayName} {conversationName}".ToLowerInvariant();

        if (text.Contains("shikamaru") || text.Contains("leader"))
            return (AgentRole.Leader, "shikamaru");

        if (text.Contains("minato") || text.Contains("coder"))
            return (AgentRole.Coder, "minato");

        if (text.Contains("tsunade") || text.Contains("tester") || text.Contains("qa"))
            return (AgentRole.Tester, "tsunade");

        if (text.Contains("kakashi") || text.Contains("reviewer"))
            return (AgentRole.Reviewer, "kakashi");

        return (AgentRole.External, "generic");
    }

    public static AgentState ComputeState(
        string? status,
        string? sidebarStatus,
        string? terminalVerdict,
        string? agentState,
        string? mainAgentOutcome,
        DateTimeOffset? updatedAt,
        DateTimeOffset now)
    {
        // 1. Needs Approval
        if (string.Equals(status, "needs-you", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(terminalVerdict) && ApprovalVerdicts.Contains(terminalVerdict)) ||
            (!string.IsNullOrEmpty(agentState) && ApprovalStates.Contains(agentState)))
        {
            return AgentState.NeedsApproval;
        }

        // 2. Working
        if (string.Equals(agentState, "working", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sidebarStatus, "working", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "working", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "in-progress", StringComparison.OrdinalIgnoreCase))
        {
            return AgentState.Working;
        }

        // 3. Failed
        if (string.Equals(mainAgentOutcome, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mainAgentOutcome, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(agentState, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(agentState, "error", StringComparison.OrdinalIgnoreCase))
        {
            return AgentState.Failed;
        }

        // 4. Done (within last 15 seconds)
        if (string.Equals(agentState, "done", StringComparison.OrdinalIgnoreCase))
        {
            if (updatedAt.HasValue)
            {
                var diff = now - updatedAt.Value;
                if (diff.TotalSeconds >= 0 && diff.TotalSeconds <= 15)
                {
                    return AgentState.Done;
                }
            }
            else
            {
                return AgentState.Done;
            }
        }

        // 5. Idle
        return AgentState.Idle;
    }

    public static IReadOnlyList<BotItemInfo> ParseBotItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<BotItemInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                return Array.Empty<BotItemInfo>();

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("items", out var itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BotItemInfo>();
            }

            var items = new List<BotItemInfo>();
            foreach (var item in itemsElement.EnumerateArray())
            {
                var number = item.TryGetProperty("number", out var numProp) && numProp.TryGetInt32(out var n) ? n : 0;
                string? ownerBotId = null;
                string? ownerName = null;

                if (item.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
                {
                    if (owner.TryGetProperty("botId", out var bId)) ownerBotId = bId.GetString();
                    if (owner.TryGetProperty("name", out var oName)) ownerName = oName.GetString();
                }

                var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                var status = item.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;

                string? questionPrompt = null;
                if (item.TryGetProperty("question", out var qElem))
                {
                    questionPrompt = ExtractQuestionPrompt(qElem);
                }

                DateTimeOffset? updatedAt = null;
                if (item.TryGetProperty("updatedAt", out var upProp) && upProp.TryGetInt64(out var ms) && ms > 0)
                {
                    try { updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms); } catch { }
                }

                items.Add(new BotItemInfo(number, ownerBotId, ownerName, title, status, questionPrompt, updatedAt));
            }

            return items;
        }
        catch (JsonException)
        {
            return Array.Empty<BotItemInfo>();
        }
    }

    public static string? ExtractQuestionPrompt(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Object:
                // Look for common property names: prompt, text, ask, question, title
                string[] candidates = { "prompt", "text", "ask", "question", "title" };
                foreach (var prop in candidates)
                {
                    if (element.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            return s;
                    }
                }

                // Check nested questions array
                if (element.TryGetProperty("questions", out var questionsArr) && questionsArr.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var q in questionsArr.EnumerateArray())
                    {
                        var p = ExtractQuestionPrompt(q);
                        if (!string.IsNullOrWhiteSpace(p)) parts.Add(p);
                    }
                    if (parts.Count > 0) return string.Join("\n", parts);
                }

                return element.GetRawText();

            case JsonValueKind.Array:
                var arrParts = new List<string>();
                foreach (var sub in element.EnumerateArray())
                {
                    var p = ExtractQuestionPrompt(sub);
                    if (!string.IsNullOrWhiteSpace(p)) arrParts.Add(p);
                }
                return arrParts.Count > 0 ? string.Join("\n", arrParts) : null;

            default:
                return element.ToString();
        }
    }

    public static IReadOnlyList<AgentSnapshot> MergeSnapshots(
        IReadOnlyList<AgentSnapshot> agents,
        IReadOnlyList<BotItemInfo> items)
    {
        if (items.Count == 0) return agents;

        var merged = new List<AgentSnapshot>(agents.Count);
        foreach (var agent in agents)
        {
            // Find if any item is needs-you for this agent
            var item = items.FirstOrDefault(i =>
                string.Equals(i.Status, "needs-you", StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrEmpty(i.OwnerBotId) && (agent.Id.Contains(i.OwnerBotId, StringComparison.OrdinalIgnoreCase) || agent.Name.Contains(i.OwnerBotId, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrEmpty(i.OwnerName) && (agent.Name.Contains(i.OwnerName, StringComparison.OrdinalIgnoreCase) || i.OwnerName.Contains(agent.Name, StringComparison.OrdinalIgnoreCase)))));

            if (item != null)
            {
                merged.Add(agent with
                {
                    State = AgentState.NeedsApproval,
                    QuestionPrompt = item.QuestionPrompt ?? agent.QuestionPrompt,
                    TaskTitle = string.IsNullOrEmpty(agent.TaskTitle) ? item.Title : agent.TaskTitle,
                    Unread = true
                });
            }
            else
            {
                merged.Add(agent);
            }
        }

        return merged;
    }

    public static IReadOnlyList<AccountInfo> ParseAccounts(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return Array.Empty<AccountInfo>();

        var accounts = new List<AccountInfo>();
        var lines = plainText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        string? currentProvider = null;
        var providerHeaderRegex = new Regex(@"^(?:Managed\s+([A-Za-z0-9_-]+)\s+accounts|No\s+managed\s+([A-Za-z0-9_-]+)\s+accounts)", RegexOptions.IgnoreCase);

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var match = providerHeaderRegex.Match(trimmed);
            if (match.Success)
            {
                currentProvider = !string.IsNullOrEmpty(match.Groups[1].Value)
                    ? match.Groups[1].Value
                    : match.Groups[2].Value;
                continue;
            }

            if (currentProvider == null)
                continue;

            // Lines under header indented by spaces
            if (rawLine.StartsWith(" ") || rawLine.StartsWith("\t"))
            {
                // Format: <accountId> <label/email> [(active)]
                var tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 1)
                {
                    var accountId = tokens[0];
                    var label = tokens.Length > 1 ? string.Join(" ", tokens.Skip(1)) : accountId;
                    var isActive = trimmed.Contains("(active)", StringComparison.OrdinalIgnoreCase);

                    accounts.Add(new AccountInfo(
                        Provider: currentProvider,
                        AccountId: accountId,
                        Label: label,
                        IsActive: isActive
                    ));
                }
            }
        }

        return accounts;
    }
}
