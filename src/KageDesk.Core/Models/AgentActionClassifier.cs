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

        int tokenCount = 0;
        int start = 0;
        int n = input.Length;

        bool hasTesting = false;
        bool hasReviewing = false;
        bool hasGitOps = false;
        bool hasBuilding = false;
        bool hasCoding = false;
        bool hasSearching = false;
        bool hasResolve = false;
        bool hasLibrary = false;
        bool hasQuery = false;
        bool hasDocs = false;
        bool hasWeb = false;
        bool hasReadingPrimary = false;
        bool hasList = false;
        bool hasFileDirResources = false;
        bool hasExecuting = false;
        bool hasThinking = false;

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
                    tokenCount++;
                    ReadOnlySpan<char> token = input.Slice(start, tokenEnd - start);

                    if (MatchesAny(token, TestingTokens)) hasTesting = true;
                    if (MatchesAny(token, ReviewingTokens)) hasReviewing = true;
                    if (MatchesAny(token, GitOpsTokens)) hasGitOps = true;
                    if (MatchesAny(token, BuildingTokens)) hasBuilding = true;
                    if (MatchesAny(token, CodingTokens)) hasCoding = true;
                    if (MatchesAny(token, SearchingTokens)) hasSearching = true;
                    if (token.Equals("resolve", StringComparison.OrdinalIgnoreCase)) hasResolve = true;
                    if (token.Equals("library", StringComparison.OrdinalIgnoreCase)) hasLibrary = true;
                    if (token.Equals("query", StringComparison.OrdinalIgnoreCase)) hasQuery = true;
                    if (token.Equals("docs", StringComparison.OrdinalIgnoreCase)) hasDocs = true;
                    if (token.Equals("web", StringComparison.OrdinalIgnoreCase)) hasWeb = true;
                    if (MatchesAny(token, ReadingTokens)) hasReadingPrimary = true;
                    if (token.Equals("list", StringComparison.OrdinalIgnoreCase)) hasList = true;
                    if (MatchesAny(token, FileDirResourceTokens)) hasFileDirResources = true;
                    if (MatchesAny(token, ExecutingTokens)) hasExecuting = true;
                    if (MatchesAny(token, ThinkingTokens)) hasThinking = true;
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

        // Precedence 1: Testing (test, tests, vitest, jest, pytest, vstest, xunit, nunit)
        if (hasTesting)
        {
            return (AgentActionKind.Testing, "TEST");
        }

        // Precedence 2: Reviewing (review, diff, audit, lint, inspect)
        if (hasReviewing)
        {
            return (AgentActionKind.Reviewing, "REVIEW");
        }

        // Precedence 3: GitOps (git, gh, commit, push, merge, pr)
        if (hasGitOps)
        {
            return (AgentActionKind.GitOps, "GIT");
        }

        // Precedence 4: Building (build, compile, dotnet, msbuild, tsc, webpack, vite, cargo, gradle, npm, pnpm)
        if (hasBuilding)
        {
            return (AgentActionKind.Building, "BUILD");
        }

        // Precedence 5: Coding (edit, write, patch, replace, code, create)
        if (hasCoding)
        {
            return (AgentActionKind.Coding, "CODE");
        }

        // Precedence 6: Searching (search, grep, rg, find, glob, fd, findstr, docs, web, resolve-library)
        if (hasSearching || (hasResolve && hasLibrary) || (hasQuery && hasDocs) || hasWeb)
        {
            return (AgentActionKind.Searching, "SCAN");
        }

        // Precedence 7: Reading (read, cat, view, open, dir, files, resources)
        // Note: resources / dir / files when paired with read/view/list or exact file-reading tool — NOT list_threads!
        if (hasReadingPrimary || (hasList && hasFileDirResources))
        {
            return (AgentActionKind.Reading, "READ");
        }

        // Precedence 8: Executing (exec, execute, command, bash, pwsh, powershell, cmd, shell, terminal, run)
        if (hasExecuting)
        {
            return (AgentActionKind.Executing, "EXEC");
        }

        // Precedence 9: Explicit Thinking tokens
        if (hasThinking)
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

    private static bool MatchesAny(ReadOnlySpan<char> token, string[] matches)
    {
        for (int i = 0; i < matches.Length; i++)
        {
            if (token.Equals(matches[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
