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
            var classified = MatchAction(toolName.AsSpan());
            if (classified.HasValue && classified.Value.Kind != AgentActionKind.Thinking)
            {
                return classified.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(taskTitle))
        {
            var classified = MatchAction(taskTitle.AsSpan());
            if (classified.HasValue && classified.Value.Kind != AgentActionKind.Thinking)
            {
                return classified.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            var classified = MatchAction(toolName.AsSpan());
            if (classified.HasValue)
            {
                return classified.Value;
            }
        }

        return (AgentActionKind.Thinking, "THINK");
    }

    public static (AgentActionKind Kind, string ShortLabel) Classify(
        AgentState state,
        ReadOnlySpan<char> toolName,
        ReadOnlySpan<char> taskTitle = default)
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

        if (!toolName.IsEmpty && !toolName.IsWhiteSpace())
        {
            var classified = MatchAction(toolName);
            if (classified.HasValue && classified.Value.Kind != AgentActionKind.Thinking)
            {
                return classified.Value;
            }
        }

        if (!taskTitle.IsEmpty && !taskTitle.IsWhiteSpace())
        {
            var classified = MatchAction(taskTitle);
            if (classified.HasValue && classified.Value.Kind != AgentActionKind.Thinking)
            {
                return classified.Value;
            }
        }

        if (!toolName.IsEmpty && !toolName.IsWhiteSpace())
        {
            var classified = MatchAction(toolName);
            if (classified.HasValue)
            {
                return classified.Value;
            }
        }

        return (AgentActionKind.Thinking, "THINK");
    }

    private static bool IsDelimiter(char c) =>
        c is ' ' or '_' or '-' or '/' or '\\' or '.' or ':' or ',' or ';' or '\t' or '\r' or '\n';

    private static (AgentActionKind Kind, string ShortLabel)? MatchAction(ReadOnlySpan<char> input)
    {
        input = input.Trim();
        if (input.IsEmpty)
            return null;

        Span<Range> tokens = stackalloc Range[32];
        int tokenCount = 0;
        int start = 0;
        int n = input.Length;

        for (int i = 0; i <= n; i++)
        {
            bool isEnd = i == n;
            bool isDelim = !isEnd && IsDelimiter(input[i]);
            bool isCamelTransition = !isEnd && i > start && char.IsLower(input[i - 1]) && char.IsUpper(input[i]);
            bool isAcronymTransition = !isEnd && i >= start + 2 && char.IsUpper(input[i - 2]) && char.IsUpper(input[i - 1]) && char.IsLower(input[i]);

            if (isEnd || isDelim || isCamelTransition || isAcronymTransition)
            {
                int tokenEnd = isAcronymTransition ? i - 1 : i;
                if (tokenEnd > start)
                {
                    if (tokenCount < tokens.Length)
                    {
                        tokens[tokenCount++] = new Range(start, tokenEnd);
                    }
                }

                if (isAcronymTransition)
                {
                    start = i - 1;
                }
                else if (isDelim)
                {
                    start = i + 1;
                }
                else // isCamelTransition
                {
                    start = i;
                }
            }
        }

        if (tokenCount == 0)
            return null;

        ReadOnlySpan<Range> validTokens = tokens.Slice(0, tokenCount);

        // Precedence 1: Testing (test, tests, vitest, jest, pytest, vstest, xunit, nunit)
        if (HasAnyToken(input, validTokens, TestingTokens))
        {
            return (AgentActionKind.Testing, "TEST");
        }

        // Precedence 2: Reviewing (review, diff, audit, lint, inspect)
        if (HasAnyToken(input, validTokens, ReviewingTokens))
        {
            return (AgentActionKind.Reviewing, "REVIEW");
        }

        // Precedence 3: GitOps (git, gh, commit, push, merge, pr)
        if (HasAnyToken(input, validTokens, GitOpsTokens))
        {
            return (AgentActionKind.GitOps, "GIT");
        }

        // Precedence 4: Building (build, compile, dotnet, msbuild, tsc, webpack, vite, cargo, gradle, npm, pnpm)
        if (HasAnyToken(input, validTokens, BuildingTokens))
        {
            return (AgentActionKind.Building, "BUILD");
        }

        // Precedence 5: Coding (edit, write, patch, replace, code, create)
        if (HasAnyToken(input, validTokens, CodingTokens))
        {
            return (AgentActionKind.Coding, "CODE");
        }

        // Precedence 6: Searching (search, grep, rg, find, glob, fd, findstr, docs, web, resolve-library)
        if (HasAnyToken(input, validTokens, SearchingTokens) ||
            (HasToken(input, validTokens, "resolve") && HasToken(input, validTokens, "library")) ||
            (HasToken(input, validTokens, "query") && HasToken(input, validTokens, "docs")) ||
            HasToken(input, validTokens, "web"))
        {
            return (AgentActionKind.Searching, "SCAN");
        }

        // Precedence 7: Reading (read, cat, view, open, dir, files, resources)
        // Note: resources / dir / files when paired with read/view/list or exact file-reading tool — NOT list_threads!
        bool hasReadingPrimary = HasAnyToken(input, validTokens, ReadingTokens);
        bool hasList = HasToken(input, validTokens, "list");
        bool hasFileDirResources = HasAnyToken(input, validTokens, FileDirResourceTokens);

        if (hasReadingPrimary || (hasList && hasFileDirResources))
        {
            return (AgentActionKind.Reading, "READ");
        }

        // Precedence 8: Executing (exec, execute, command, bash, pwsh, powershell, cmd, shell, terminal, run)
        if (HasAnyToken(input, validTokens, ExecutingTokens))
        {
            return (AgentActionKind.Executing, "EXEC");
        }

        // Precedence 9: Explicit Thinking tokens
        if (HasAnyToken(input, validTokens, ThinkingTokens))
        {
            return (AgentActionKind.Thinking, "THINK");
        }

        return (AgentActionKind.Thinking, "THINK");
    }

    private static readonly string[] TestingTokens = { "test", "tests", "testing", "vitest", "jest", "pytest", "vstest", "xunit", "nunit" };
    private static readonly string[] ReviewingTokens = { "review", "reviewing", "reviews", "diff", "audit", "lint", "linting", "inspect", "inspecting" };
    private static readonly string[] GitOpsTokens = { "git", "gh", "commit", "commits", "committing", "push", "pushes", "pushing", "merge", "merges", "merging", "pr" };
    private static readonly string[] BuildingTokens = { "build", "building", "builds", "compile", "compiling", "dotnet", "msbuild", "tsc", "webpack", "vite", "cargo", "gradle", "npm", "pnpm" };
    private static readonly string[] CodingTokens = { "edit", "editing", "edits", "write", "writing", "writes", "patch", "patching", "replace", "replacing", "code", "coding", "create", "creating" };
    private static readonly string[] SearchingTokens = { "search", "searching", "grep", "rg", "find", "finding", "glob", "fd", "findstr", "docs" };
    private static readonly string[] ReadingTokens = { "read", "reading", "reads", "cat", "view", "viewing", "open" };
    private static readonly string[] FileDirResourceTokens = { "dir", "files", "resources", "resource", "file" };
    private static readonly string[] ExecutingTokens = { "exec", "execute", "executing", "execution", "command", "bash", "pwsh", "powershell", "cmd", "shell", "terminal", "run", "running" };
    private static readonly string[] ThinkingTokens = { "think", "thinking", "plan", "planning", "chat", "stream", "subagent", "orchestration", "ask" };

    private static bool HasToken(ReadOnlySpan<char> span, ReadOnlySpan<Range> tokens, string match)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (span[tokens[i]].Equals(match, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasAnyToken(ReadOnlySpan<char> span, ReadOnlySpan<Range> tokens, string[] matches)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            ReadOnlySpan<char> token = span[tokens[i]];
            for (int m = 0; m < matches.Length; m++)
            {
                if (token.Equals(matches[m], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
