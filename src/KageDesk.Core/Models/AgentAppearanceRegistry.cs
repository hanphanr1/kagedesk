using System.Text.RegularExpressions;

namespace KageDesk.Core.Models;

public readonly record struct AgentAppearance(
    string SpriteKey,
    DeskRigKind DeskRig,
    int BehaviorVariant
);

public sealed class AgentAppearanceRegistry
{
    public static readonly IReadOnlyList<string> RandomizableSprites = new[]
    {
        "dj-synth",
        "neko-dev",
        "cyber-hacker",
        "ronin-dev",
        "astro-dev",
        "wizard-dev",
        "mecha-droid",
        "street-gamer"
    };

    public static readonly IReadOnlyList<DeskRigKind> RandomizableDeskRigs = new[]
    {
        DeskRigKind.DualMonitorRig,
        DeskRigKind.RgbStreamerRig,
        DeskRigKind.CozyLofiRig,
        DeskRigKind.RetroCrtRig,
        DeskRigKind.CyberConsoleRig
    };

    private static readonly HashSet<string> RandomizableSpriteSet = new(RandomizableSprites, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CanonicalOrcaSprites = new(StringComparer.OrdinalIgnoreCase)
    {
        "shikamaru",
        "minato",
        "tsunade",
        "kakashi"
    };

    private readonly Dictionary<string, AgentAppearance> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _rng;
    private readonly object _lock = new();

    public AgentAppearanceRegistry(int? seed = null)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public static bool IsRandomizableSprite(string? spriteKey)
    {
        return !string.IsNullOrWhiteSpace(spriteKey) && RandomizableSpriteSet.Contains(spriteKey.Trim());
    }

    private static readonly Regex CanonicalPersonaRegex = new(
        @"(?<![a-z0-9])(shikamaru|kazama|minato|kakashi|tsunade)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CanonicalRoleKeywordRegex = new(
        @"(?<![a-z0-9])(team[\s_-]*leader|team[\s_-]*lead|teamlead|leader|lead|main[\s_-]*coder|coder|reviewer|tester|qa)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryMatchCanonicalPersonaName(string? text, out AgentRole role, out string spriteKey)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var match = CanonicalPersonaRegex.Match(text);
            if (match.Success)
            {
                string token = match.Groups[1].Value.ToLowerInvariant();
                switch (token)
                {
                    case "shikamaru":
                    case "kazama":
                        role = AgentRole.Leader;
                        spriteKey = "shikamaru";
                        return true;
                    case "minato":
                        role = AgentRole.Coder;
                        spriteKey = "minato";
                        return true;
                    case "kakashi":
                        role = AgentRole.Reviewer;
                        spriteKey = "kakashi";
                        return true;
                    case "tsunade":
                        role = AgentRole.Tester;
                        spriteKey = "tsunade";
                        return true;
                }
            }
        }

        role = default;
        spriteKey = string.Empty;
        return false;
    }

    public static bool TryMatchCanonicalRoleKeyword(string? text, out AgentRole role, out string spriteKey)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var match = CanonicalRoleKeywordRegex.Match(text);
            if (match.Success)
            {
                string token = match.Groups[1].Value.ToLowerInvariant();
                if (token.Contains("lead"))
                {
                    role = AgentRole.Leader;
                    spriteKey = "shikamaru";
                    return true;
                }
                if (token.Contains("coder"))
                {
                    role = AgentRole.Coder;
                    spriteKey = "minato";
                    return true;
                }
                if (token == "reviewer")
                {
                    role = AgentRole.Reviewer;
                    spriteKey = "kakashi";
                    return true;
                }
                if (token == "tester" || token == "qa")
                {
                    role = AgentRole.Tester;
                    spriteKey = "tsunade";
                    return true;
                }
            }
        }

        role = default;
        spriteKey = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves a canonical Orca bot (Shikamaru, Minato, Kakashi, Tsunade) across all metadata fields.
    /// Priority:
    /// 1) Explicit persona name on the agent/worker's own DisplayName / Name / ConversationName / AgentId
    /// 2) Explicit persona name on BotName / Bot / UnderBot / WorktreeId
    /// 3) Canonical role keyword on Role / DisplayName / Name / ConversationName / BotName / Bot / UnderBot
    /// </summary>
    public static bool TryResolveCanonicalOrcaBot(
        string? displayName,
        string? name,
        string? conversationName,
        string? agentId,
        string? botName,
        string? bot,
        string? underBot,
        string? roleText,
        string? worktreeId,
        out AgentRole role,
        out string spriteKey)
    {
        // Priority 1: Explicit persona name on worker/agent identity itself
        if (TryMatchCanonicalPersonaName(displayName, out role, out spriteKey) ||
            TryMatchCanonicalPersonaName(name, out role, out spriteKey) ||
            TryMatchCanonicalPersonaName(conversationName, out role, out spriteKey) ||
            TryMatchCanonicalPersonaName(agentId, out role, out spriteKey))
        {
            return true;
        }

        // Priority 2: Explicit persona name on owner bot / underBot / worktreeId
        if (TryMatchCanonicalPersonaName(botName, out role, out spriteKey) ||
            TryMatchCanonicalPersonaName(bot, out role, out spriteKey) ||
            TryMatchCanonicalPersonaName(underBot, out role, out spriteKey) ||
            TryMatchCanonicalPersonaName(worktreeId, out role, out spriteKey))
        {
            return true;
        }

        // Priority 3: Canonical role keywords on explicit role or agent/bot titles
        if (TryMatchCanonicalRoleKeyword(roleText, out role, out spriteKey) ||
            TryMatchCanonicalRoleKeyword(displayName, out role, out spriteKey) ||
            TryMatchCanonicalRoleKeyword(name, out role, out spriteKey) ||
            TryMatchCanonicalRoleKeyword(conversationName, out role, out spriteKey) ||
            TryMatchCanonicalRoleKeyword(botName, out role, out spriteKey) ||
            TryMatchCanonicalRoleKeyword(bot, out role, out spriteKey) ||
            TryMatchCanonicalRoleKeyword(underBot, out role, out spriteKey))
        {
            return true;
        }

        role = default;
        spriteKey = string.Empty;
        return false;
    }

    public static bool TryResolveCanonicalOrcaBot(AgentSnapshot agent, out AgentRole role, out string spriteKey)
    {
        // Standalone detected CLI terminal processes are NEVER canonical Orca bots
        if (string.Equals(agent.Source, "process", StringComparison.OrdinalIgnoreCase))
        {
            role = default;
            spriteKey = string.Empty;
            return false;
        }

        // 1. Explicit canonical persona name in Name / Id / WorktreeId (even if upstream tagged Role=Clone)
        if (TryMatchCanonicalPersonaName(agent.Name, out role, out spriteKey) ||
            TryMatchCanonicalPersonaName(agent.Id, out role, out spriteKey) ||
            TryMatchCanonicalPersonaName(agent.WorktreeId, out role, out spriteKey))
        {
            return true;
        }

        // 2. Canonical sprite key already assigned for a non-Clone/External role (or Orca source)
        if (agent.Role is AgentRole.Leader or AgentRole.Coder or AgentRole.Reviewer or AgentRole.Tester)
        {
            bool isOrcaOrDirect =
                string.Equals(agent.Source, "orca", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(agent.Source) ||
                agent.IsBotHome;

            if (isOrcaOrDirect || CanonicalOrcaSprites.Contains(agent.SpriteKey))
            {
                role = agent.Role;
                spriteKey = agent.Role switch
                {
                    AgentRole.Leader => "shikamaru",
                    AgentRole.Coder => "minato",
                    AgentRole.Reviewer => "kakashi",
                    AgentRole.Tester => "tsunade",
                    _ => agent.SpriteKey
                };
                return true;
            }
        }

        // 3. Bot-home entries with canonical role keywords in Name
        if (agent.IsBotHome && TryMatchCanonicalRoleKeyword(agent.Name, out role, out spriteKey))
        {
            return true;
        }

        role = default;
        spriteKey = string.Empty;
        return false;
    }

    public static bool IsCanonicalOrcaBot(AgentSnapshot agent)
    {
        return TryResolveCanonicalOrcaBot(agent, out _, out _);
    }

    /// <summary>
    /// Formats a clean user-facing role label for UI surfaces (never displays "Clone").
    /// </summary>
    public static string FormatRoleLabel(AgentSnapshot agent)
    {
        if (TryResolveCanonicalOrcaBot(agent, out var canonicalRole, out _))
        {
            return canonicalRole switch
            {
                AgentRole.Leader => "Leader",
                AgentRole.Coder => "Coder",
                AgentRole.Reviewer => "Reviewer",
                AgentRole.Tester => "Tester",
                _ => "Worker"
            };
        }

        return agent.Role switch
        {
            AgentRole.Leader => "Leader",
            AgentRole.Coder => "Coder",
            AgentRole.Reviewer => "Reviewer",
            AgentRole.Tester => "Tester",
            AgentRole.Clone => string.Equals(agent.Source, "process", StringComparison.OrdinalIgnoreCase)
                ? "CLI Session"
                : "Worker",
            AgentRole.External => string.Equals(agent.Source, "process", StringComparison.OrdinalIgnoreCase)
                ? "CLI Session"
                : "Session",
            _ => "Session"
        };
    }

    public static string FormatProviderDisplayName(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "Agent";
        }

        return provider.Trim().ToUpperInvariant() switch
        {
            "OMP" or "PI" => "OMP",
            "CLAUDE" or "CLAUDE-CODE" => "Claude",
            "CODEX" => "Codex",
            "AGY" or "ANTIGRAVITY" => "AGY",
            "OPENCODE" => "OpenCode",
            "GEMINI" or "GEMINI-CLI" => "Gemini",
            _ => "Agent"
        };
    }

    public static string FormatIndexedProviderName(string? provider, int zeroBasedIndex, int totalCount)
    {
        string baseName = FormatProviderDisplayName(provider);
        if (totalCount <= 1 || zeroBasedIndex <= 0)
        {
            return baseName;
        }

        return $"{baseName} {zeroBasedIndex + 1}";
    }

    public static string SanitizeDisplayName(string? rawName, string? id = null, string? source = null, AgentRole role = AgentRole.External)
    {
        string trimmed = rawName?.Trim() ?? string.Empty;

        bool isClonePlaceholder = trimmed.Contains("clone", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(trimmed) && !isClonePlaceholder)
        {
            return trimmed;
        }

        string? inferred = InferToolNameFromText(id) ?? InferToolNameFromText(trimmed);
        if (!string.IsNullOrEmpty(inferred))
        {
            return inferred;
        }

        return "Agent";
    }

    private static string? InferToolNameFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string lower = text.ToLowerInvariant();
        if (lower.Contains("omp")) return "OMP";
        if (lower.Contains("claude")) return "Claude";
        if (lower.Contains("codex")) return "Codex";
        if (lower.Contains("agy") || lower.Contains("antigravity")) return "AGY";
        if (lower.Contains("opencode")) return "OpenCode";
        if (lower.Contains("gemini")) return "Gemini";
        return null;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _assignments.Clear();
        }
    }

    public AgentAppearance GetOrAssignForNonOrca(string sessionId, IReadOnlySet<string> activeSessionIds, string? preferredSprite = null, DeskRigKind? preferredRig = null)
    {
        lock (_lock)
        {
            PruneInactiveLocked(activeSessionIds);
            return GetOrAssignLocked(sessionId, preferredSprite, preferredRig);
        }
    }

    public void ApplyToAgents(IList<AgentSnapshot> agents)
    {
        lock (_lock)
        {
            var activeIds = new HashSet<string>(agents.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < agents.Count; i++)
            {
                activeIds.Add(agents[i].Id);
            }

            PruneInactiveLocked(activeIds);

            for (int i = 0; i < agents.Count; i++)
            {
                var ag = agents[i];
                string cleanName = SanitizeDisplayName(ag.Name, ag.Id, ag.Source, ag.Role);

                if (TryResolveCanonicalOrcaBot(ag, out var canonicalRole, out var canonicalSprite))
                {
                    int canonicalVariant = ag.BehaviorVariant != 0
                        ? Math.Abs(ag.BehaviorVariant) % 3
                        : ComputeStableVariant(ag.Id);

                    if (!string.Equals(cleanName, ag.Name, StringComparison.Ordinal) ||
                        ag.Role != canonicalRole ||
                        !string.Equals(ag.SpriteKey, canonicalSprite, StringComparison.OrdinalIgnoreCase) ||
                        ag.BehaviorVariant != canonicalVariant)
                    {
                        agents[i] = ag with
                        {
                            Name = cleanName,
                            Role = canonicalRole,
                            SpriteKey = canonicalSprite,
                            BehaviorVariant = canonicalVariant
                        };
                    }
                    continue;
                }

                string? existingRandomSprite = IsRandomizableSprite(ag.SpriteKey) ? ag.SpriteKey : null;
                DeskRigKind? existingRig = ag.DeskRig != DeskRigKind.Default ? ag.DeskRig : null;

                var appearance = GetOrAssignLocked(ag.Id, existingRandomSprite, existingRig);

                agents[i] = ag with
                {
                    Name = cleanName,
                    SpriteKey = appearance.SpriteKey,
                    DeskRig = appearance.DeskRig,
                    BehaviorVariant = appearance.BehaviorVariant
                };
            }
        }
    }

    private void PruneInactiveLocked(IReadOnlySet<string> activeIds)
    {
        if (_assignments.Count == 0) return;

        List<string>? toRemove = null;
        foreach (var key in _assignments.Keys)
        {
            if (!activeIds.Contains(key))
            {
                toRemove ??= new List<string>();
                toRemove.Add(key);
            }
        }

        if (toRemove != null)
        {
            foreach (var k in toRemove)
            {
                _assignments.Remove(k);
            }
        }
    }

    private AgentAppearance GetOrAssignLocked(string sessionId, string? preferredSprite, DeskRigKind? preferredRig)
    {
        if (_assignments.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        var spriteCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rigCounts = new Dictionary<DeskRigKind, int>();
        var variantCounts = new int[3];

        foreach (var assigned in _assignments.Values)
        {
            spriteCounts[assigned.SpriteKey] = spriteCounts.TryGetValue(assigned.SpriteKey, out int sc) ? sc + 1 : 1;
            rigCounts[assigned.DeskRig] = rigCounts.TryGetValue(assigned.DeskRig, out int rc) ? rc + 1 : 1;
            variantCounts[Math.Abs(assigned.BehaviorVariant) % 3]++;
        }

        // Choose sprite: honor preferred randomizable sprite if unused, else pick randomly among least-used sprites
        string chosenSprite;
        if (!string.IsNullOrEmpty(preferredSprite) &&
            IsRandomizableSprite(preferredSprite) &&
            !spriteCounts.ContainsKey(preferredSprite))
        {
            chosenSprite = preferredSprite.ToLowerInvariant();
        }
        else
        {
            int minSpriteCount = int.MaxValue;
            for (int i = 0; i < RandomizableSprites.Count; i++)
            {
                int c = spriteCounts.TryGetValue(RandomizableSprites[i], out int val) ? val : 0;
                if (c < minSpriteCount) minSpriteCount = c;
            }

            var spritePool = new List<string>(RandomizableSprites.Count);
            for (int i = 0; i < RandomizableSprites.Count; i++)
            {
                int c = spriteCounts.TryGetValue(RandomizableSprites[i], out int val) ? val : 0;
                if (c == minSpriteCount)
                {
                    spritePool.Add(RandomizableSprites[i]);
                }
            }

            chosenSprite = spritePool[_rng.Next(spritePool.Count)];
        }

        // Choose desk rig: strong affinity for dj-synth -> RgbStreamerRig when unused, else pick randomly among least-used rigs
        DeskRigKind chosenRig;
        if (preferredRig.HasValue &&
            preferredRig.Value != DeskRigKind.Default &&
            !rigCounts.ContainsKey(preferredRig.Value))
        {
            chosenRig = preferredRig.Value;
        }
        else if (string.Equals(chosenSprite, "dj-synth", StringComparison.OrdinalIgnoreCase) &&
                 (!rigCounts.TryGetValue(DeskRigKind.RgbStreamerRig, out int djRigCount) || djRigCount == 0))
        {
            chosenRig = DeskRigKind.RgbStreamerRig;
        }
        else
        {
            int minRigCount = int.MaxValue;
            for (int i = 0; i < RandomizableDeskRigs.Count; i++)
            {
                int c = rigCounts.TryGetValue(RandomizableDeskRigs[i], out int val) ? val : 0;
                if (c < minRigCount) minRigCount = c;
            }

            var rigPool = new List<DeskRigKind>(RandomizableDeskRigs.Count);
            for (int i = 0; i < RandomizableDeskRigs.Count; i++)
            {
                int c = rigCounts.TryGetValue(RandomizableDeskRigs[i], out int val) ? val : 0;
                if (c == minRigCount)
                {
                    rigPool.Add(RandomizableDeskRigs[i]);
                }
            }

            chosenRig = rigPool[_rng.Next(rigPool.Count)];
        }

        // Choose behavior variant (0..2), preferring least-used so concurrent sessions don't animate in lockstep
        int minVarCount = Math.Min(variantCounts[0], Math.Min(variantCounts[1], variantCounts[2]));
        var varPool = new List<int>(3);
        for (int v = 0; v < 3; v++)
        {
            if (variantCounts[v] == minVarCount)
            {
                varPool.Add(v);
            }
        }
        int chosenVariant = varPool[_rng.Next(varPool.Count)];

        var appearance = new AgentAppearance(chosenSprite, chosenRig, chosenVariant);
        _assignments[sessionId] = appearance;
        return appearance;
    }

    private static int ComputeStableVariant(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        unchecked
        {
            int h = 17;
            for (int i = 0; i < id.Length; i++)
            {
                h = h * 31 + id[i];
            }
            return Math.Abs(h) % 3;
        }
    }
}
