namespace KageDesk.Core.Models;

public readonly record struct CanonicalRoleAlias(string SpriteKey, AgentRole Role);

public static class AgentAliasResolver
{
    private static readonly Dictionary<string, CanonicalRoleAlias> RoleAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Leader / Shikamaru
        ["shikamaru"] = new("shikamaru", AgentRole.Leader),
        ["leader"] = new("shikamaru", AgentRole.Leader),
        ["lead"] = new("shikamaru", AgentRole.Leader),
        ["team-leader"] = new("shikamaru", AgentRole.Leader),
        ["team_leader"] = new("shikamaru", AgentRole.Leader),
        ["team-lead"] = new("shikamaru", AgentRole.Leader),
        ["team_lead"] = new("shikamaru", AgentRole.Leader),
        ["teamlead"] = new("shikamaru", AgentRole.Leader),

        // Coder / Minato
        ["minato"] = new("minato", AgentRole.Coder),
        ["coder"] = new("minato", AgentRole.Coder),
        ["dev"] = new("minato", AgentRole.Coder),
        ["developer"] = new("minato", AgentRole.Coder),

        // Tester / Tsunade
        ["tsunade"] = new("tsunade", AgentRole.Tester),
        ["tester"] = new("tsunade", AgentRole.Tester),
        ["qa"] = new("tsunade", AgentRole.Tester),

        // Reviewer / Kakashi
        ["kakashi"] = new("kakashi", AgentRole.Reviewer),
        ["reviewer"] = new("kakashi", AgentRole.Reviewer),
        ["review"] = new("kakashi", AgentRole.Reviewer),

        // Clone
        ["clone"] = new("clone", AgentRole.Clone)
    };

    public static bool TryResolveRoleAlias(string? agentRef, out CanonicalRoleAlias canonical)
    {
        if (string.IsNullOrWhiteSpace(agentRef))
        {
            canonical = default;
            return false;
        }

        return RoleAliases.TryGetValue(agentRef.Trim(), out canonical);
    }

    public static string ResolveCanonicalAgentId(string agentRef, HqState snapshot)
    {
        if (string.IsNullOrWhiteSpace(agentRef))
        {
            return agentRef;
        }

        return ResolveCanonicalAgentId(agentRef, snapshot?.Agents);
    }

    public static string ResolveCanonicalAgentId(string agentRef, IReadOnlyList<AgentSnapshot>? agents)
    {
        if (string.IsNullOrWhiteSpace(agentRef))
        {
            return agentRef;
        }

        string trimmed = agentRef.Trim();
        return TryResolveAgentId(trimmed, agents, out string? resolvedId) && !string.IsNullOrEmpty(resolvedId)
            ? resolvedId
            : trimmed;
    }

    public static bool TryResolveAgentId(string? agentRef, IReadOnlyList<AgentSnapshot>? agents, out string? resolvedAgentId)
    {
        resolvedAgentId = null;
        if (string.IsNullOrWhiteSpace(agentRef) || agents == null || agents.Count == 0)
        {
            return false;
        }

        string trimmed = agentRef.Trim();

        // Pass 1: Strict exact OrdinalIgnoreCase match on AgentSnapshot.Id
        for (int i = 0; i < agents.Count; i++)
        {
            if (string.Equals(agents[i].Id, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                resolvedAgentId = agents[i].Id;
                return true;
            }
        }

        // Pass 2 (only if Pass 1 misses): Shared canonical alias, SpriteKey, Role, or Name match
        bool hasRoleAlias = TryResolveRoleAlias(trimmed, out var canonicalAlias);
        for (int i = 0; i < agents.Count; i++)
        {
            if (MatchesAgentAlias(agents[i], trimmed, hasRoleAlias, canonicalAlias))
            {
                resolvedAgentId = agents[i].Id;
                return true;
            }
        }

        return false;
    }

    public static bool AreSameCanonicalEndpoint(
        string fromRef,
        string toRef,
        HqState snapshot,
        out string resolvedFrom,
        out string resolvedTo)
    {
        string trimmedFrom = fromRef.Trim();
        string trimmedTo = toRef.Trim();
        var agents = snapshot?.Agents;

        bool fromMatched = TryResolveAgentId(trimmedFrom, agents, out string? matchedFrom);
        bool toMatched = TryResolveAgentId(trimmedTo, agents, out string? matchedTo);

        resolvedFrom = fromMatched && !string.IsNullOrEmpty(matchedFrom) ? matchedFrom : trimmedFrom;
        resolvedTo = toMatched && !string.IsNullOrEmpty(matchedTo) ? matchedTo : trimmedTo;

        if (string.Equals(resolvedFrom, resolvedTo, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // When neither endpoint matches a concrete agent in the snapshot, reject pairs of aliases
        // that refer to the exact same canonical role/sprite (e.g., "qa" -> "tester").
        if (!fromMatched && !toMatched &&
            TryResolveRoleAlias(resolvedFrom, out var fromAlias) &&
            TryResolveRoleAlias(resolvedTo, out var toAlias) &&
            fromAlias.Role == toAlias.Role)
        {
            return true;
        }

        return false;
    }

    private static bool MatchesAgentAlias(
        AgentSnapshot agent,
        string trimmedRef,
        bool hasRoleAlias,
        CanonicalRoleAlias canonicalAlias)
    {
        if (hasRoleAlias &&
            (string.Equals(agent.SpriteKey, canonicalAlias.SpriteKey, StringComparison.OrdinalIgnoreCase) ||
             agent.Role == canonicalAlias.Role))
        {
            return true;
        }

        return string.Equals(agent.SpriteKey, trimmedRef, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(agent.Role.ToString(), trimmedRef, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(agent.Name, trimmedRef, StringComparison.OrdinalIgnoreCase) ||
               MatchesShortenedName(agent.Name, trimmedRef);
    }

    private static bool MatchesShortenedName(string? fullName, string agentRef)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return false;
        }

        int paren = fullName.IndexOf('(');
        string baseName = (paren > 0 ? fullName.Substring(0, paren) : fullName).Trim();
        if (string.Equals(baseName, agentRef, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int space = baseName.IndexOf(' ');
        if (space > 0 && string.Equals(baseName.Substring(0, space).Trim(), agentRef, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
