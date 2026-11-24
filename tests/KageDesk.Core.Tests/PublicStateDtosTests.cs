using System.Text.Json;
using KageDesk.Core.Bridge;
using KageDesk.Core.Models;
using Xunit;

namespace KageDesk.Core.Tests;

public class PublicStateDtosTests
{
    [Fact]
    public void PublicHqStateDto_PropertyTypes_AndAllowlistedProjection()
    {
        // 1. Verify Property Types
        var accountsProp = typeof(PublicHqStateDto).GetProperty(nameof(PublicHqStateDto.Accounts));
        Assert.NotNull(accountsProp);
        Assert.Equal(typeof(IReadOnlyList<PublicAccountDto>), accountsProp.PropertyType);

        var recentBuildsProp = typeof(PublicHqStateDto).GetProperty(nameof(PublicHqStateDto.RecentBuilds));
        Assert.NotNull(recentBuildsProp);
        Assert.Equal(typeof(IReadOnlyList<PublicBuildDto>), recentBuildsProp.PropertyType);

        var buildsProp = typeof(PublicHqStateDto).GetProperty(nameof(PublicHqStateDto.Builds));
        Assert.NotNull(buildsProp);
        Assert.Equal(typeof(IReadOnlyList<PublicBuildDto>), buildsProp.PropertyType);

        // 2. Build sample DTO
        var dto = new PublicHqStateDto(
            Agents: new List<PublicAgentDto>
            {
                new("ag-1", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle)
            },
            Accounts: new List<PublicAccountDto>
            {
                new("Anthropic", "acc-123", "Claude Work", true, 42.5, DateTimeOffset.UtcNow.AddHours(2))
            },
            Builds: new List<PublicBuildDto>
            {
                new("b-1", "hanphanr1/kagedesk", "CI", "main", "completed", "success", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow)
            },
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<PublicVisualEventDto>(),
            DemoScenario: null,
            Timestamp: DateTimeOffset.UtcNow
        );

        Assert.Same(dto.Builds, dto.RecentBuilds);

        // 3. Serialize to JSON and verify exact allowlisted keys and zero leaked internal fields
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify accounts keys
        var accounts = root.GetProperty("accounts");
        Assert.Equal(1, accounts.GetArrayLength());
        var acc = accounts[0];
        Assert.True(acc.TryGetProperty("provider", out _));
        Assert.True(acc.TryGetProperty("accountId", out _));
        Assert.True(acc.TryGetProperty("label", out _));
        Assert.True(acc.TryGetProperty("isActive", out _));
        Assert.True(acc.TryGetProperty("quotaPercent", out _));
        Assert.True(acc.TryGetProperty("quotaResetAt", out _));

        // Verify builds keys
        var builds = root.GetProperty("builds");
        Assert.Equal(1, builds.GetArrayLength());
        var b = builds[0];
        Assert.True(b.TryGetProperty("id", out _));
        Assert.True(b.TryGetProperty("repo", out _));
        Assert.True(b.TryGetProperty("workflowName", out _));
        Assert.True(b.TryGetProperty("branch", out _));
        Assert.True(b.TryGetProperty("status", out _));
        Assert.True(b.TryGetProperty("conclusion", out _));
        Assert.True(b.TryGetProperty("createdAt", out _));
        Assert.True(b.TryGetProperty("updatedAt", out _));

        // Ensure internal fields from BuildInfo (like Title, Url, IsFailing, IsSuccessful) are NOT present in public build DTO
        Assert.False(b.TryGetProperty("title", out _));
        Assert.False(b.TryGetProperty("url", out _));
        Assert.False(b.TryGetProperty("isFailing", out _));
        Assert.False(b.TryGetProperty("isSuccessful", out _));

        // Ensure RecentBuilds is ignored in JSON serialization (does not duplicate builds array)
        Assert.False(root.TryGetProperty("recentBuilds", out _));
    }
}
