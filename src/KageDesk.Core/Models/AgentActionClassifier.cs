using System.Text.Json.Serialization;

namespace KageDesk.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentActionKind
{
    None,
    Thinking,
    Reading,
    Searching,
    Coding,
    Executing,
    Testing,
    Building,
    Reviewing,
    GitOps,
    Approval,
    Done,
    Failed
}

public static class AgentActionClassifier
{
    private static readonly char[] Delimiters = { ' ', '_', '-', '/', '\\', '.', ':', ',', ';' };

    public static (AgentActionKind Kind, string ShortLabel) Classify(
        AgentState state,
        string? toolName,
        string? taskTitle = null)
    {
        switch (state)
        {
            case AgentState.NeedsApproval:
                return (AgentActionKind.Approval, "APPROVE");
            case AgentState.Failed:
                return (AgentActionKind.Failed, "FAIL");
            case AgentState.Done:
                return (AgentActionKind.Done, "PASS");
            case AgentState.Idle:
                return (AgentActionKind.None, "IDLE");
        }

        // When state == Working
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            var classified = MatchAction(toolName);
            if (classified.HasValue && classified.Value.Kind != AgentActionKind.Thinking)
            {
                return classified.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(taskTitle))
        {
            var classified = MatchAction(taskTitle);
            if (classified.HasValue && classified.Value.Kind != AgentActionKind.Thinking)
            {
                return classified.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            var classified = MatchAction(toolName);
            if (classified.HasValue)
            {
                return classified.Value;
            }
        }

        return (AgentActionKind.Thinking, "THINK");
    }

    private static (AgentActionKind Kind, string ShortLabel)? MatchAction(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string text = input.Trim().ToLowerInvariant();
        string[] tokens = text.Split(Delimiters, StringSplitOptions.RemoveEmptyEntries);

        // 1. Reading
        // read_file, view_file, read_resource, list_dir, list_files, list_resources, cat, read
        if (text.Contains("read_file") || text.Contains("view_file") || text.Contains("read_resource") ||
            text.Contains("list_dir") || text.Contains("list_files") || text.Contains("list_resources") ||
            HasToken(tokens, "cat") || text.Contains("read"))
        {
            return (AgentActionKind.Reading, "READ");
        }

        // 2. Searching
        // search, grep, rg, find, glob, fd, web_search, query-docs, resolve-library
        if (text.Contains("web_search") || text.Contains("query-docs") || text.Contains("resolve-library") ||
            text.Contains("search") || text.Contains("grep") || text.Contains("find") || text.Contains("glob") ||
            HasToken(tokens, "rg") || HasToken(tokens, "fd"))
        {
            return (AgentActionKind.Searching, "SCAN");
        }

        // 3. Coding
        // edit, write, patch, replace, multi_replace, create_file, code
        if (text.Contains("create_file") || text.Contains("multi_replace") || text.Contains("replace") ||
            text.Contains("patch") || text.Contains("edit") || text.Contains("write") || text.Contains("code"))
        {
            return (AgentActionKind.Coding, "CODE");
        }

        // 4. Testing
        // test, vitest, jest, pytest, vstest, xunit, nunit
        if (text.Contains("vitest") || text.Contains("pytest") || text.Contains("vstest") ||
            text.Contains("xunit") || text.Contains("nunit") || text.Contains("jest") ||
            text.Contains("test"))
        {
            return (AgentActionKind.Testing, "TEST");
        }

        // 5. Building
        // build, compile, dotnet, msbuild, tsc, webpack, vite, cargo, gradle, npm, pnpm
        if (text.Contains("compile") || text.Contains("msbuild") || text.Contains("dotnet") ||
            text.Contains("webpack") || text.Contains("gradle") || text.Contains("build") ||
            HasToken(tokens, "tsc") || HasToken(tokens, "vite") || HasToken(tokens, "cargo") ||
            HasToken(tokens, "npm") || HasToken(tokens, "pnpm"))
        {
            return (AgentActionKind.Building, "BUILD");
        }

        // 6. GitOps
        // git, gh, commit, push, merge, pr
        if (text.Contains("commit") || text.Contains("push") || text.Contains("merge") ||
            HasToken(tokens, "git") || HasToken(tokens, "gh") || HasToken(tokens, "pr"))
        {
            return (AgentActionKind.GitOps, "GIT");
        }

        // 7. Reviewing
        // review, diff, audit, lint, inspect
        if (text.Contains("review") || text.Contains("diff") || text.Contains("audit") ||
            text.Contains("lint") || text.Contains("inspect"))
        {
            return (AgentActionKind.Reviewing, "REVIEW");
        }

        // 8. Executing
        // exec, run_command, bash, pwsh, powershell, cmd, shell, terminal, run_workflow
        if (text.Contains("run_command") || text.Contains("run_workflow") || text.Contains("powershell") ||
            text.Contains("terminal") || text.Contains("shell") || text.Contains("bash") ||
            text.Contains("pwsh") || text.Contains("exec") || text.Contains("execute") ||
            HasToken(tokens, "cmd"))
        {
            return (AgentActionKind.Executing, "EXEC");
        }

        // 9. Thinking
        // think, plan, chat, stream, subagent, orchestration, ask
        if (text.Contains("think") || text.Contains("plan") || text.Contains("chat") ||
            text.Contains("stream") || text.Contains("subagent") || text.Contains("orchestration") ||
            text.Contains("ask"))
        {
            return (AgentActionKind.Thinking, "THINK");
        }

        return (AgentActionKind.Thinking, "THINK");
    }

    private static bool HasToken(string[] tokens, string target)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
