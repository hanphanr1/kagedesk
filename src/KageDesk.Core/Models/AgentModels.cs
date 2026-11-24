using System.Text.Json.Serialization;

namespace KageDesk.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentState
{
    Idle,
    Working,
    NeedsApproval,
    Done,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentRole
{
    Leader,
    Coder,
    Tester,
    Reviewer,
    Clone,
    External
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WeatherMode
{
    Auto,
    Dawn,
    Day,
    Sunset,
    MidnightRain,
    StormAlert
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EffectiveWeather
{
    Dawn,
    Day,
    Sunset,
    MidnightRain,
    StormAlert
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HqMood
{
    Calm,
    Busy,
    Alert,
    Failing,
    Celebrating
}

public record AgentSnapshot(
    string Id,
    string Name,
    AgentRole Role,
    string SpriteKey,
    AgentState State,
    string? WorktreeId = null,
    string? Repo = null,
    string? Branch = null,
    string? ToolName = null,
    string? TaskTitle = null,
    string? Prompt = null,
    string? LastMessage = null,
    DateTimeOffset? LastActivityAt = null,
    DateTimeOffset? UpdatedAt = null,
    bool IsBotHome = false,
    bool Unread = false,
    string? QuestionPrompt = null,
    string? Source = null
);

public record AccountInfo(
    string Provider,
    string AccountId,
    string Label,
    bool IsActive,
    double? QuotaPercent = null,
    DateTimeOffset? QuotaResetAt = null
);

public record BuildInfo(
    string Id,
    string Repo,
    string WorkflowName,
    string Branch,
    string Title,
    string Status,
    string? Conclusion = null,
    string? Url = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? CreatedAt = null
)
{
    public bool IsFailing =>
        string.Equals(Conclusion, "failure", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Conclusion, "timed_out", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Conclusion, "action_required", StringComparison.OrdinalIgnoreCase);

    public bool IsSuccessful =>
        string.Equals(Conclusion, "success", StringComparison.OrdinalIgnoreCase);
}

public record HqEvent(
    long SequenceId,
    string Type,
    string Message,
    DateTimeOffset Timestamp,
    string? AgentId = null,
    double TtlSeconds = 10.0
);

public record HqState(
    IReadOnlyList<AgentSnapshot> Agents,
    IReadOnlyList<AccountInfo> Accounts,
    IReadOnlyList<BuildInfo> Builds,
    EffectiveWeather Weather,
    HqMood Mood,
    IReadOnlyList<HqEvent> PendingVisualEvents,
    string? DemoScenario = null,
    DateTimeOffset Timestamp = default
)
{
    public virtual bool Equals(HqState? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;

        return Weather == other.Weather &&
               Mood == other.Mood &&
               string.Equals(DemoScenario, other.DemoScenario, StringComparison.Ordinal) &&
               Agents.SequenceEqual(other.Agents) &&
               Accounts.SequenceEqual(other.Accounts) &&
               Builds.SequenceEqual(other.Builds) &&
               PendingVisualEvents.SequenceEqual(other.PendingVisualEvents);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Weather);
        hash.Add(Mood);
        hash.Add(DemoScenario);
        foreach (var a in Agents) hash.Add(a);
        foreach (var acc in Accounts) hash.Add(acc);
        foreach (var b in Builds) hash.Add(b);
        foreach (var e in PendingVisualEvents) hash.Add(e);
        return hash.ToHashCode();
    }
}
