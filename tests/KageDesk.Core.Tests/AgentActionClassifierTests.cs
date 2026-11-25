using KageDesk.Core.Models;
using Xunit;

namespace KageDesk.Core.Tests;

public class AgentActionClassifierTests
{
    [Theory]
    [InlineData("read_file", AgentActionKind.Reading, "READ")]
    [InlineData("view_file", AgentActionKind.Reading, "READ")]
    [InlineData("read_resource", AgentActionKind.Reading, "READ")]
    [InlineData("list_dir", AgentActionKind.Reading, "READ")]
    [InlineData("cat", AgentActionKind.Reading, "READ")]
    [InlineData("search_web", AgentActionKind.Searching, "SCAN")]
    [InlineData("query-docs", AgentActionKind.Searching, "SCAN")]
    [InlineData("resolve-library-id", AgentActionKind.Searching, "SCAN")]
    [InlineData("rg", AgentActionKind.Searching, "SCAN")]
    [InlineData("grep", AgentActionKind.Searching, "SCAN")]
    [InlineData("edit_file", AgentActionKind.Coding, "CODE")]
    [InlineData("patch", AgentActionKind.Coding, "CODE")]
    [InlineData("replace", AgentActionKind.Coding, "CODE")]
    [InlineData("create_file", AgentActionKind.Coding, "CODE")]
    [InlineData("execute", AgentActionKind.Executing, "EXEC")]
    [InlineData("run_command", AgentActionKind.Executing, "EXEC")]
    [InlineData("bash", AgentActionKind.Executing, "EXEC")]
    [InlineData("pwsh", AgentActionKind.Executing, "EXEC")]
    [InlineData("test", AgentActionKind.Testing, "TEST")]
    [InlineData("pytest", AgentActionKind.Testing, "TEST")]
    [InlineData("vitest", AgentActionKind.Testing, "TEST")]
    [InlineData("dotnet", AgentActionKind.Building, "BUILD")]
    [InlineData("build", AgentActionKind.Building, "BUILD")]
    [InlineData("compile", AgentActionKind.Building, "BUILD")]
    [InlineData("msbuild", AgentActionKind.Building, "BUILD")]
    [InlineData("cargo", AgentActionKind.Building, "BUILD")]
    [InlineData("git", AgentActionKind.GitOps, "GIT")]
    [InlineData("gh", AgentActionKind.GitOps, "GIT")]
    [InlineData("commit", AgentActionKind.GitOps, "GIT")]
    [InlineData("push", AgentActionKind.GitOps, "GIT")]
    [InlineData("review", AgentActionKind.Reviewing, "REVIEW")]
    [InlineData("diff", AgentActionKind.Reviewing, "REVIEW")]
    [InlineData("lint", AgentActionKind.Reviewing, "REVIEW")]
    [InlineData("credit_lookup", AgentActionKind.Thinking, "THINK")]
    [InlineData("list_threads", AgentActionKind.Thinking, "THINK")]
    [InlineData("editFile", AgentActionKind.Coding, "CODE")]
    [InlineData("list_resources", AgentActionKind.Reading, "READ")]
    [InlineData("runCommand", AgentActionKind.Executing, "EXEC")]
    [InlineData("think", AgentActionKind.Thinking, "THINK")]
    [InlineData("plan", AgentActionKind.Thinking, "THINK")]
    [InlineData("unknown_tool", AgentActionKind.Thinking, "THINK")]
    [InlineData("", AgentActionKind.Thinking, "THINK")]
    [InlineData(null, AgentActionKind.Thinking, "THINK")]
    public void Classify_WorkingTools_MapsCorrectlyAndNeverChopsToReadFil(
        string? toolName,
        AgentActionKind expectedKind,
        string expectedLabel)
    {
        var (kind, label) = AgentActionClassifier.Classify(AgentState.Working, toolName);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedLabel, label);
        Assert.NotEqual("READ_FIL", label);
    }

    [Theory]
    [InlineData(AgentState.NeedsApproval, AgentActionKind.Approval, "APPROVE")]
    [InlineData(AgentState.Failed, AgentActionKind.Failed, "FAIL")]
    [InlineData(AgentState.Done, AgentActionKind.Done, "PASS")]
    [InlineData(AgentState.Idle, AgentActionKind.None, "IDLE")]
    public void Classify_StateOverrides_HavePriorityOverToolName(
        AgentState state,
        AgentActionKind expectedKind,
        string expectedLabel)
    {
        var (kind, label) = AgentActionClassifier.Classify(state, "read_file", "Important task");
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedLabel, label);
    }

    [Fact]
    public void Classify_FallbackToTaskTitle_WhenToolNameEmpty()
    {
        var (kind, label) = AgentActionClassifier.Classify(AgentState.Working, null, "Building the frontend release");
        Assert.Equal(AgentActionKind.Building, kind);
        Assert.Equal("BUILD", label);
    }
}
