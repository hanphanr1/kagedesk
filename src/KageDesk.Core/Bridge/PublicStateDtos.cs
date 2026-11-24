using System.Text.Json.Serialization;
using KageDesk.Core.Models;

namespace KageDesk.Core.Bridge;

public record PublicAgentDto(
    string Id,
    string Name,
    AgentRole Role,
    string SpriteKey,
    AgentState State,
    string? ToolName = null,
    string? TaskTitle = null,
    string? Repo = null,
    string? Branch = null,
    bool IsBotHome = false,
    bool Unread = false,
    string? Source = null,
    DateTimeOffset? LastActivityAt = null,
    DateTimeOffset? UpdatedAt = null
);

public record PublicVisualEventDto(
    long SequenceId,
    string Type,
    string? AgentId,
    DateTimeOffset Timestamp
);

public record PublicBuildDto(
    string Id,
    string Repo,
    string WorkflowName,
    string Branch,
    string Status,
    string? Conclusion = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null
);

public record PublicHqStateDto(
    IReadOnlyList<PublicAgentDto> Agents,
    IReadOnlyList<AccountInfo> Accounts,
    IReadOnlyList<PublicBuildDto> Builds,
    EffectiveWeather Weather,
    HqMood Mood,
    IReadOnlyList<PublicVisualEventDto> PendingVisualEvents,
    string? DemoScenario,
    DateTimeOffset Timestamp
);
