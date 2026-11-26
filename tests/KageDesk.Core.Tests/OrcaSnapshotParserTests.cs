using System.Text.Json;
using KageDesk.Core.Collectors;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using Xunit;

namespace KageDesk.Core.Tests;

public class OrcaSnapshotParserTests
{
    private const string RealBotListFixture = """
    {
      "ok": true,
      "result": {
        "bots": [
          {
            "id": "3e562a34-a1b2-4c3d-8e4f-5a6b7c8d9e0f",
            "name": "Shikamaru (Team Leader)",
            "agent": "antigravity",
            "paused": false,
            "specialty": "Orchestration & Planning",
            "self": true
          }
        ]
      }
    }
    """;

    private const string RealWorktreePsFixture = """
    {
      "ok": true,
      "result": {
        "worktrees": [
          {
            "worktreeId": "folder:kazama-team-leader",
            "repo": "Bots",
            "path": "C:\\Users\\DELL\\.orca\\bots\\kazama-team-leader",
            "branch": "",
            "bot": "Shikamaru (Team Leader)",
            "botHome": true,
            "displayName": "Shikamaru (Team Leader)",
            "workspaceStatus": "in-progress",
            "sidebarStatus": "working",
            "terminalVerdict": "none",
            "status": "working",
            "unread": false,
            "lastActivityAt": 1795544908922,
            "agents": [
              {
                "address": "orca_session_id:shikamaru-session",
                "state": "working",
                "agentType": "antigravity",
                "prompt": "Coordinate squad",
                "taskTitle": "Sprint Planning",
                "conversationName": "Shikamaru (Team Leader)",
                "lastAssistantMessage": null,
                "toolName": "execute",
                "toolInput": "git status",
                "interrupted": false,
                "mainAgent": {
                  "state": "working",
                  "outcome": "success",
                  "stateStartedAt": 1795545516240
                },
                "stateStartedAt": 1795545516240,
                "updatedAt": 1795545570739,
                "activity": {
                  "status": "working",
                  "unread": false,
                  "title": "Executing commands"
                }
              }
            ]
          },
          {
            "worktreeId": "wt:worker-task-1",
            "repo": "kagedesk",
            "path": "C:\\Users\\DELL\\.orca\\worktrees\\worker-task-1",
            "branch": "feature/scaffold",
            "bot": null,
            "botHome": false,
            "displayName": "Generic Worker #1",
            "workspaceStatus": "in-progress",
            "sidebarStatus": "working",
            "terminalVerdict": "none",
            "status": "working",
            "unread": false,
            "lastActivityAt": 1795544908922,
            "agents": [
              {
                "address": "orca_session_id:clone-worker-1",
                "state": "working",
                "agentType": "antigravity",
                "prompt": "Implement core state",
                "taskTitle": null,
                "conversationName": "Worker 1",
                "updatedAt": 1795545570739
              }
            ]
          }
        ],
        "otherBots": []
      }
    }
    """;

    private const string RealAccountListFixture = """
    Managed Claude accounts (1):
      system  System default (active)
      cf913782-b1c9-43ed-90c7-0fe3eac161e2  martitaeugdc@hotmail.com

    No managed Codex accounts.
      system  System default (active)

    Managed Antigravity accounts (1):
      5ee618f2-92c8-4916-a546-227a347fb79c  fakehanphan@gmail.com (active)
    """;

    [Fact]
    public void ParseBotList_ValidFixture_ReturnsExpectedBots()
    {
        var bots = OrcaSnapshotParser.ParseBotList(RealBotListFixture);
        Assert.Single(bots);
        Assert.Equal("3e562a34-a1b2-4c3d-8e4f-5a6b7c8d9e0f", bots[0].Id);
        Assert.Equal("Shikamaru (Team Leader)", bots[0].Name);
        Assert.Equal("antigravity", bots[0].Agent);
        Assert.False(bots[0].Paused);
        Assert.True(bots[0].Self);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ \"ok\": false }")]
    [InlineData("not json")]
    public void ParseBotList_EdgeAndMalformed_ReturnsEmptyList(string? input)
    {
        var bots = OrcaSnapshotParser.ParseBotList(input);
        Assert.Empty(bots);
    }

    [Fact]
    public void ParseWorktreePs_ValidFixture_DifferentiatesBotHomeAndNonCanonicalWorker()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1795545570739 + 1000); // 1 sec after updatedAt
        var agents = OrcaSnapshotParser.ParseWorktreePs(RealWorktreePsFixture, now);

        Assert.Equal(2, agents.Count);

        // Leader in bot home
        var leader = agents[0];
        Assert.Equal("orca_session_id:shikamaru-session", leader.Id);
        Assert.Equal(AgentRole.Leader, leader.Role);
        Assert.Equal("shikamaru", leader.SpriteKey);
        Assert.Equal(AgentState.Working, leader.State);
        Assert.True(leader.IsBotHome);

        // Non-canonical child worker (no canonical bot persona in DisplayName/Bot/UnderBot)
        var worker = agents[1];
        Assert.Equal("orca_session_id:clone-worker-1", worker.Id);
        Assert.Equal(AgentRole.Clone, worker.Role);
        Assert.Equal("clone", worker.SpriteKey);
        Assert.Equal(AgentState.Working, worker.State);
        Assert.False(worker.IsBotHome);
    }

    [Theory]
    [InlineData("Minato (Coder)", "minato", AgentRole.Coder, "minato")]
    [InlineData("Senior coder bot", "coder", AgentRole.Coder, "minato")]
    [InlineData("Tsunade (QA)", "tsunade", AgentRole.Tester, "tsunade")]
    [InlineData("Tester specialist", "tester", AgentRole.Tester, "tsunade")]
    [InlineData("Kakashi (Reviewer)", "kakashi", AgentRole.Reviewer, "kakashi")]
    [InlineData("Code reviewer", "reviewer", AgentRole.Reviewer, "kakashi")]
    [InlineData("Kazama (Team Lead)", "kazama", AgentRole.Leader, "shikamaru")]
    [InlineData("Generic assistant", "helper", AgentRole.External, "generic")]
    public void InferRoleAndSprite_BotHomePersonas_MappedCorrectly(string name, string conv, AgentRole expectedRole, string expectedSprite)
    {
        var (role, sprite) = OrcaSnapshotParser.InferRoleAndSprite(name, name, conv, isBotHome: true, worktreeId: "folder:home");
        Assert.Equal(expectedRole, role);
        Assert.Equal(expectedSprite, sprite);
    }

    [Fact]
    public void InferRoleAndSprite_CanonicalBotsInNonBotHomeWorktrees_ResolveCanonicalRoleAndFixedSprite()
    {
        // Regression: "Minato · KageDesk v2.1" dispatched under Shikamaru MUST resolve to Minato (Coder), NOT Clone or Shikamaru
        var (role1, sprite1) = OrcaSnapshotParser.InferRoleAndSprite(
            bot: "Shikamaru (Team Leader)",
            displayName: "Minato \u00B7 KageDesk v2.1 (Mixi + Variety + OMP Fix)",
            conversationName: "KageDesk v2.1",
            isBotHome: false,
            worktreeId: "wt:kagedesk-v21");
        Assert.Equal(AgentRole.Coder, role1);
        Assert.Equal("minato", sprite1);

        // Explicit Kazama / Shikamaru worker -> Leader / shikamaru
        var (role2, sprite2) = OrcaSnapshotParser.InferRoleAndSprite(
            bot: null,
            displayName: "Kazama \u00B7 Sprint Planning",
            conversationName: null,
            isBotHome: false,
            worktreeId: "wt:plan");
        Assert.Equal(AgentRole.Leader, role2);
        Assert.Equal("shikamaru", sprite2);

        // Explicit Kakashi worker under Shikamaru -> Reviewer / kakashi
        var (role3, sprite3) = OrcaSnapshotParser.InferRoleAndSprite(
            bot: "Shikamaru (Team Leader)",
            displayName: "Kakashi \u00B7 Code Review",
            conversationName: null,
            isBotHome: false,
            worktreeId: "wt:review");
        Assert.Equal(AgentRole.Reviewer, role3);
        Assert.Equal("kakashi", sprite3);

        // Explicit Tsunade worker under Shikamaru -> Tester / tsunade
        var (role4, sprite4) = OrcaSnapshotParser.InferRoleAndSprite(
            bot: "Shikamaru (Team Leader)",
            displayName: "Tsunade \u00B7 Final QA",
            conversationName: null,
            isBotHome: false,
            worktreeId: "wt:qa");
        Assert.Equal(AgentRole.Tester, role4);
        Assert.Equal("tsunade", sprite4);

        // Unnamed worker under Minato's UnderBot -> resolves via UnderBot to Minato (Coder)
        var (role5, sprite5) = OrcaSnapshotParser.InferRoleAndSprite(
            bot: null,
            displayName: "kagedesk-v20-fixes",
            conversationName: null,
            isBotHome: false,
            worktreeId: "wt:v20-fixes",
            underBot: "Minato (Main Coder)");
        Assert.Equal(AgentRole.Coder, role5);
        Assert.Equal("minato", sprite5);

        // Truly non-canonical child worker (no canonical bot metadata anywhere) -> falls back to Clone
        var (role6, sprite6) = OrcaSnapshotParser.InferRoleAndSprite(
            bot: null,
            displayName: "feature-scratch-branch",
            conversationName: "Temp Session",
            isBotHome: false,
            worktreeId: "wt:scratch");
        Assert.Equal(AgentRole.Clone, role6);
        Assert.Equal("clone", sprite6);
    }

    [Fact]
    public void ParseWorktreePs_MinatoAndAllCanonicalWorkersInFeatureWorktrees_NeverMarkedClone()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1795545571739);
        const string json = """
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "57e97e35::C:/workspaces/kagedesk-v21",
                "repo": "kagedesk",
                "branch": "hanphanr1/kagedesk-v21",
                "bot": "Shikamaru (Team Leader)",
                "botHome": false,
                "displayName": "Minato \u00B7 KageDesk v2.1 (Mixi + Variety)",
                "sidebarStatus": "working",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:minato-v21",
                    "state": "working",
                    "displayName": "New messages",
                    "conversationName": "KageDesk v2.1",
                    "updatedAt": 1795545570739
                  }
                ]
              },
              {
                "worktreeId": "wt:review-pr",
                "repo": "kagedesk",
                "bot": "Shikamaru (Team Leader)",
                "botHome": false,
                "displayName": "Kakashi \u00B7 PR Sign-Off",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:kakashi-pr",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              },
              {
                "worktreeId": "wt:qa-run",
                "repo": "kagedesk",
                "underBot": "Tsunade (Tester)",
                "botHome": false,
                "displayName": "kagedesk-v21-qa-check",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:tsunade-qa",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              },
              {
                "worktreeId": "folder:kazama-team-leader",
                "repo": "Bots",
                "botName": "Shikamaru (Team Leader)",
                "botHome": true,
                "displayName": "Shikamaru (Team Leader)",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:shikamaru-home",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var agents = OrcaSnapshotParser.ParseWorktreePs(json, now);
        Assert.Equal(4, agents.Count);

        var minato = Assert.Single(agents, a => a.Id == "orca_session_id:minato-v21");
        Assert.Equal(AgentRole.Coder, minato.Role);
        Assert.Equal("minato", minato.SpriteKey);
        Assert.Contains("Minato", minato.Name);

        var kakashi = Assert.Single(agents, a => a.Id == "orca_session_id:kakashi-pr");
        Assert.Equal(AgentRole.Reviewer, kakashi.Role);
        Assert.Equal("kakashi", kakashi.SpriteKey);

        var tsunade = Assert.Single(agents, a => a.Id == "orca_session_id:tsunade-qa");
        Assert.Equal(AgentRole.Tester, tsunade.Role);
        Assert.Equal("tsunade", tsunade.SpriteKey);

        var shikamaru = Assert.Single(agents, a => a.Id == "orca_session_id:shikamaru-home");
        Assert.Equal(AgentRole.Leader, shikamaru.Role);
        Assert.Equal("shikamaru", shikamaru.SpriteKey);
    }

    [Fact]
    public void ComputeState_PriorityRules_EvaluatesCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var tenSecAgo = now.AddSeconds(-10);
        var twentySecAgo = now.AddSeconds(-20);

        // Needs approval overrides working
        Assert.Equal(AgentState.NeedsApproval,
            OrcaSnapshotParser.ComputeState(status: "needs-you", sidebarStatus: "working", terminalVerdict: "none", agentState: "working", mainAgentOutcome: "success", updatedAt: now, now: now));

        // Waiting verdict -> NeedsApproval
        Assert.Equal(AgentState.NeedsApproval,
            OrcaSnapshotParser.ComputeState(status: "working", sidebarStatus: "working", terminalVerdict: "approval", agentState: "working", mainAgentOutcome: "success", updatedAt: now, now: now));

        // Working
        Assert.Equal(AgentState.Working,
            OrcaSnapshotParser.ComputeState(status: "working", sidebarStatus: "working", terminalVerdict: "none", agentState: "working", mainAgentOutcome: "success", updatedAt: now, now: now));

        // Failed
        Assert.Equal(AgentState.Failed,
            OrcaSnapshotParser.ComputeState(status: "idle", sidebarStatus: "idle", terminalVerdict: "none", agentState: "failed", mainAgentOutcome: "error", updatedAt: now, now: now));

        // Done within 15 seconds -> Done
        Assert.Equal(AgentState.Done,
            OrcaSnapshotParser.ComputeState(status: "idle", sidebarStatus: "idle", terminalVerdict: "none", agentState: "done", mainAgentOutcome: "success", updatedAt: tenSecAgo, now: now));

        // Done older than 15 seconds -> Idle
        Assert.Equal(AgentState.Idle,
            OrcaSnapshotParser.ComputeState(status: "idle", sidebarStatus: "idle", terminalVerdict: "none", agentState: "done", mainAgentOutcome: "success", updatedAt: twentySecAgo, now: now));
    }

    [Fact]
    public void ParseBotItems_QuestionField_HandlesVariousJsonShapesDefensively()
    {
        var json = """
        {
          "ok": true,
          "result": {
            "items": [
              {
                "number": 1,
                "owner": { "botId": "b1", "name": "Shikamaru" },
                "title": "Question as string",
                "status": "needs-you",
                "question": "Should we proceed with migration?"
              },
              {
                "number": 2,
                "owner": { "botId": "b2", "name": "Minato" },
                "title": "Question as object",
                "status": "needs-you",
                "question": { "prompt": "Select target branch" }
              },
              {
                "number": 3,
                "owner": { "botId": "b3", "name": "Tsunade" },
                "title": "Question as object with questions array",
                "status": "needs-you",
                "question": { "questions": [ { "prompt": "Step 1 OK?" }, { "prompt": "Step 2 OK?" } ] }
              },
              {
                "number": 4,
                "owner": { "botId": "b4", "name": "Kakashi" },
                "title": "Question as array",
                "status": "needs-you",
                "question": [ "Confirm deploy?", "Are you sure?" ]
              },
              {
                "number": 5,
                "owner": { "botId": "b5", "name": "Clone" },
                "title": "Null question",
                "status": "waiting",
                "question": null
              }
            ]
          }
        }
        """;

        var items = OrcaSnapshotParser.ParseBotItems(json);
        Assert.Equal(5, items.Count);

        Assert.Equal("Should we proceed with migration?", items[0].QuestionPrompt);
        Assert.Equal("Select target branch", items[1].QuestionPrompt);
        Assert.Contains("Step 1 OK?", items[2].QuestionPrompt);
        Assert.Contains("Confirm deploy?", items[3].QuestionPrompt);
        Assert.Null(items[4].QuestionPrompt);
    }

    [Fact]
    public void MergeSnapshots_UpdatesMatchingAgentToNeedsApproval()
    {
        var agents = new List<AgentSnapshot>
        {
            new("b1", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Working, TaskTitle: "Initial task")
        };

        var items = new List<BotItemInfo>
        {
            new(10, "b1", "Shikamaru (Team Leader)", "Deploy approval", "needs-you", "Approve release?", DateTimeOffset.UtcNow)
        };

        var merged = OrcaSnapshotParser.MergeSnapshots(agents, items);
        Assert.Single(merged);
        Assert.Equal(AgentState.NeedsApproval, merged[0].State);
        Assert.Equal("Approve release?", merged[0].QuestionPrompt);
        Assert.True(merged[0].Unread);
    }

    [Fact]
    public void ParseAccounts_ValidFixture_ParsesProvidersAndActiveFlags()
    {
        var accounts = OrcaSnapshotParser.ParseAccounts(RealAccountListFixture);

        Assert.Equal(4, accounts.Count);

        // Claude accounts
        var claudeActive = accounts.FirstOrDefault(a => a.Provider == "Claude" && a.AccountId == "system");
        Assert.NotNull(claudeActive);
        Assert.True(claudeActive.IsActive);

        var claudeGuid = accounts.FirstOrDefault(a => a.Provider == "Claude" && a.AccountId == "cf913782-b1c9-43ed-90c7-0fe3eac161e2");
        Assert.NotNull(claudeGuid);
        Assert.Equal("martitaeugdc@hotmail.com", claudeGuid.Label);
        Assert.False(claudeGuid.IsActive);

        // Codex account
        var codexSystem = accounts.FirstOrDefault(a => a.Provider == "Codex" && a.AccountId == "system");
        Assert.NotNull(codexSystem);
        Assert.True(codexSystem.IsActive);

        // Antigravity account
        var antigravity = accounts.FirstOrDefault(a => a.Provider == "Antigravity");
        Assert.NotNull(antigravity);
        Assert.Equal("5ee618f2-92c8-4916-a546-227a347fb79c", antigravity.AccountId);
        Assert.True(antigravity.IsActive);
        Assert.Contains("fakehanphan@gmail.com", antigravity.Label);
    }

    [Fact]
    public void GitHubCiCollector_ParseRunList_ParsesFieldsCorrectly()
    {
        var json = """
        [
          {
            "databaseId": 1234567,
            "status": "completed",
            "conclusion": "failure",
            "workflowName": "Build & Test",
            "headBranch": "main",
            "displayTitle": "Scaffold state engine",
            "url": "https://github.com/hanphanr1/kagedesk/actions/runs/1234567",
            "updatedAt": "2026-11-25T01:00:00Z"
          }
        ]
        """;

        var builds = GitHubCiCollector.ParseRunList(json, "hanphanr1/kagedesk");
        Assert.Single(builds);
        var b = builds[0];
        Assert.Equal("hanphanr1/kagedesk:1234567", b.Id);
        Assert.Equal("hanphanr1/kagedesk", b.Repo);
        Assert.Equal("Build & Test", b.WorkflowName);
        Assert.Equal("main", b.Branch);
        Assert.Equal("Scaffold state engine", b.Title);
        Assert.True(b.IsFailing);
        Assert.False(b.IsSuccessful);
    }

    [Fact]
    public void ProcessAgentCollector_WhenDisabled_ReturnsEmpty()
    {
        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: false);
        var detected = collector.DetectProcesses();
        Assert.Empty(detected);
    }

    [Fact]
    public void ParseWorktreePs_HandlesStringAndNullStateStartedAt_WithoutThrowing()
    {
        var now = DateTimeOffset.UtcNow;
        var json = """
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "wt:string-time",
                "displayName": "Coder",
                "status": "working",
                "stateStartedAt": "1795545516240",
                "agents": [
                  {
                    "address": "agent-1",
                    "state": "working",
                    "stateStartedAt": null,
                    "updatedAt": "1795545570739"
                  },
                  {
                    "address": "agent-2",
                    "state": "working",
                    "stateStartedAt": "invalid-not-a-number",
                    "updatedAt": null
                  }
                ]
              }
            ]
          }
        }
        """;

        var agents = OrcaSnapshotParser.ParseWorktreePs(json, now);
        Assert.Equal(2, agents.Count);
        Assert.Equal("agent-1", agents[0].Id);
        Assert.Equal("agent-2", agents[1].Id);
    }

    [Fact]
    public void ParseWorktreePs_ProjectAndBranchSlugs_MinatoMigrationAndDocsReviewer_DoNotResolveToCanonicalBots()
    {
        var now = DateTimeOffset.UtcNow;
        const string json = """
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "wt:minato-migration",
                "repo": "minato-migration",
                "branch": "feat/minato-migration",
                "displayName": "minato-migration",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:slug-minato",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              },
              {
                "worktreeId": "wt:docs-reviewer",
                "repo": "docs-reviewer",
                "branch": "chore/docs-reviewer",
                "displayName": "docs-reviewer",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:slug-reviewer",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var agents = OrcaSnapshotParser.ParseWorktreePs(json, now);
        Assert.Equal(2, agents.Count);

        var slugMinato = Assert.Single(agents, a => a.Id == "orca_session_id:slug-minato");
        Assert.Equal(AgentRole.Clone, slugMinato.Role);
        Assert.NotEqual("minato", slugMinato.SpriteKey);

        var slugReviewer = Assert.Single(agents, a => a.Id == "orca_session_id:slug-reviewer");
        Assert.Equal(AgentRole.Clone, slugReviewer.Role);
        Assert.NotEqual("kakashi", slugReviewer.SpriteKey);
    }

    [Fact]
    public void ParseWorktreePs_ExplicitRoleKakashi_ResolvesToKakashiReviewer()
    {
        var now = DateTimeOffset.UtcNow;
        const string json = """
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "wt:custom-task",
                "repo": "kagedesk",
                "displayName": "custom-task-branch",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:explicit-kakashi",
                    "role": "Kakashi",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var agents = OrcaSnapshotParser.ParseWorktreePs(json, now);
        var kakashi = Assert.Single(agents);
        Assert.Equal(AgentRole.Reviewer, kakashi.Role);
        Assert.Equal("kakashi", kakashi.SpriteKey);
    }

    [Fact]
    public void ParseWorktreePs_ExplicitAgentName_BeatsWorktreeDisplayName()
    {
        var now = DateTimeOffset.UtcNow;
        const string json = """
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "wt:minato-worktree",
                "repo": "kagedesk",
                "displayName": "Minato \u00B7 Main Implementation",
                "underBot": "Minato (Coder)",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:tsunade-helper",
                    "name": "Tsunade",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var agents = OrcaSnapshotParser.ParseWorktreePs(json, now);
        var tsunade = Assert.Single(agents);
        Assert.Equal(AgentRole.Tester, tsunade.Role);
        Assert.Equal("tsunade", tsunade.SpriteKey);
        Assert.Equal("Tsunade", tsunade.Name);
    }

    [Theory]
    [InlineData("starting", AgentState.Working)]
    [InlineData("in-progress", AgentState.Working)]
    [InlineData("busy", AgentState.Working)]
    [InlineData("streaming", AgentState.Working)]
    [InlineData("waiting-for-input", AgentState.NeedsApproval)]
    [InlineData("waiting_for_input", AgentState.NeedsApproval)]
    [InlineData("needs-you", AgentState.NeedsApproval)]
    [InlineData("needs_you", AgentState.NeedsApproval)]
    [InlineData("needs-approval", AgentState.NeedsApproval)]
    [InlineData("blocked", AgentState.NeedsApproval)]
    public void ParseWorktreePs_UpstreamOrcaLifecycleStates_DoNotMapToIdle(string rawState, AgentState expected)
    {
        var now = DateTimeOffset.UtcNow;
        string json = $$"""
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "wt:lifecycle",
                "repo": "kagedesk",
                "displayName": "Minato \u00B7 Lifecycle",
                "status": "{{rawState}}",
                "sidebarStatus": "{{rawState}}",
                "agents": [
                  {
                    "address": "orca_session_id:lifecycle-1",
                    "state": "{{rawState}}",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var agents = OrcaSnapshotParser.ParseWorktreePs(json, now);
        var ag = Assert.Single(agents);
        Assert.Equal(expected, ag.State);
        Assert.NotEqual(AgentState.Idle, ag.State);
    }

    [Theory]
    [InlineData("agentId", "Kakashi", AgentRole.Reviewer, "kakashi")]
    [InlineData("agentId", "kakashi-reviewer", AgentRole.Reviewer, "kakashi")]
    [InlineData("agentId", "bot:Kakashi", AgentRole.Reviewer, "kakashi")]
    [InlineData("agentId", "bot:Kakashi-review", AgentRole.Reviewer, "kakashi")]
    [InlineData("worktreeId", "folder:kakashi", AgentRole.Reviewer, "kakashi")]
    [InlineData("worktreeId", "bot:kakashi", AgentRole.Reviewer, "kakashi")]
    [InlineData("conversationName", "Kakashi (Reviewer)", AgentRole.Reviewer, "kakashi")]
    [InlineData("conversationName", "Minato (Main Coder)", AgentRole.Coder, "minato")]
    [InlineData("conversationName", "Minato \u00B7 Sprint Delivery", AgentRole.Coder, "minato")]
    public void ParseWorktreePs_AndAggregator_OneFieldOnlyNonHomeRows_ResolveCanonicalIdentity(
        string fieldName,
        string fieldValue,
        AgentRole expectedRole,
        string expectedSprite)
    {
        var now = DateTimeOffset.UtcNow;
        string worktreeId = fieldName == "worktreeId" ? fieldValue : "wt:generic-task";
        string agentIdProp = fieldName == "agentId" ? $""" "agentId": "{fieldValue}", """ : "";
        string convNameProp = fieldName == "conversationName" ? $""" "conversationName": "{fieldValue}", """ : "";

        string json = $$"""
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "{{worktreeId}}",
                "repo": "kagedesk",
                "botHome": false,
                "displayName": "generic-task-branch",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:one-field-test",
                    {{agentIdProp}}
                    {{convNameProp}}
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var parsed = OrcaSnapshotParser.ParseWorktreePs(json, now);
        var parsedAgent = Assert.Single(parsed);
        Assert.Equal(expectedRole, parsedAgent.Role);
        Assert.Equal(expectedSprite, parsedAgent.SpriteKey);

        var aggregator = new HqStateAggregator(timeProvider: () => now);
        aggregator.UpdateOrcaSnapshot(parsed, Array.Empty<AccountInfo>());
        var aggAgent = Assert.Single(aggregator.GetState().Agents);
        Assert.Equal(expectedRole, aggAgent.Role);
        Assert.Equal(expectedSprite, aggAgent.SpriteKey);
    }

    [Theory]
    [InlineData("name", "Kakashi")]
    [InlineData("botName", "Kakashi (Reviewer)")]
    [InlineData("role", "Kakashi")]
    [InlineData("agentId", "Kakashi")]
    [InlineData("agentId", "kakashi-reviewer")]
    [InlineData("agentId", "bot:Kakashi")]
    [InlineData("conversationName", "Kakashi (Reviewer)")]
    public void ParseWorktreePs_AndAggregator_ConflictingOwnerVsAgent_AgentOwnedKakashiBeatsParentShikamaru(
        string agentOwnedField,
        string agentOwnedValue)
    {
        var now = DateTimeOffset.UtcNow;
        string json = $$"""
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "wt:review-task",
                "repo": "kagedesk",
                "botHome": false,
                "bot": "Shikamaru (Team Leader)",
                "botName": "Shikamaru (Team Leader)",
                "underBot": "Shikamaru (Team Leader)",
                "displayName": "Shikamaru \u00B7 Delegated Review",
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:conflict-test",
                    "underBot": "Shikamaru (Team Leader)",
                    "{{agentOwnedField}}": "{{agentOwnedValue}}",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var parsed = OrcaSnapshotParser.ParseWorktreePs(json, now);
        var parsedAgent = Assert.Single(parsed);
        Assert.Equal(AgentRole.Reviewer, parsedAgent.Role);
        Assert.Equal("kakashi", parsedAgent.SpriteKey);

        var aggregator = new HqStateAggregator(timeProvider: () => now);
        aggregator.UpdateOrcaSnapshot(parsed, Array.Empty<AccountInfo>());
        var aggAgent = Assert.Single(aggregator.GetState().Agents);
        Assert.Equal(AgentRole.Reviewer, aggAgent.Role);
        Assert.Equal("kakashi", aggAgent.SpriteKey);
    }

    [Theory]
    [InlineData("Migration for Minato", "wt:migration-for-minato")]
    [InlineData("minato-migration", "wt:minato-migration")]
    [InlineData("minato-migration", "folder:minato-migration")]
    [InlineData("docs-reviewer", "wt:docs-reviewer")]
    [InlineData("docs-reviewer", "folder:docs-reviewer")]
    public void ParseWorktreePs_AndAggregator_FalsePositiveProseAndProjectTitles_NeverMatchCanonicalBots(
        string misleadingTitle,
        string worktreeId)
    {
        var now = DateTimeOffset.UtcNow;
        string json = $$"""
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "{{worktreeId}}",
                "repo": "{{misleadingTitle}}",
                "name": "{{misleadingTitle}}",
                "displayName": "{{misleadingTitle}}",
                "botHome": false,
                "status": "working",
                "agents": [
                  {
                    "address": "orca_session_id:fp-test",
                    "agentId": "{{misleadingTitle}}",
                    "name": "{{misleadingTitle}}",
                    "displayName": "{{misleadingTitle}}",
                    "conversationName": "{{misleadingTitle}}",
                    "state": "working",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        var parsed = OrcaSnapshotParser.ParseWorktreePs(json, now);
        var parsedAgent = Assert.Single(parsed);
        Assert.Equal(AgentRole.Clone, parsedAgent.Role);
        Assert.NotEqual("minato", parsedAgent.SpriteKey);
        Assert.NotEqual("kakashi", parsedAgent.SpriteKey);

        var aggregator = new HqStateAggregator(timeProvider: () => now);
        aggregator.UpdateOrcaSnapshot(parsed, Array.Empty<AccountInfo>());
        var aggAgent = Assert.Single(aggregator.GetState().Agents);
        Assert.Equal(AgentRole.Clone, aggAgent.Role);
        Assert.NotEqual("minato", aggAgent.SpriteKey);
        Assert.NotEqual("kakashi", aggAgent.SpriteKey);
    }

    [Fact]
    public async Task MergeSnapshots_AndCollector_ExternalWaitingItemStaysIdle_WhileNeedsYouTriggersNeedsApproval()
    {
        var now = DateTimeOffset.UtcNow;
        var idleBot = new AgentSnapshot(
            Id: "b1",
            Name: "Shikamaru (Team Leader)",
            Role: AgentRole.Leader,
            SpriteKey: "shikamaru",
            State: AgentState.Idle,
            IsBotHome: true,
            Source: "orca");

        // Negative control: external "waiting" bot item (e.g. waiting on CI/deploy) MUST NOT trigger NeedsApproval
        var waitingItems = new List<BotItemInfo>
        {
            new(42, "b1", "Shikamaru (Team Leader)", "Waiting for CI pipeline", "waiting", null, now)
        };

        var mergedWaiting = OrcaSnapshotParser.MergeSnapshots(new[] { idleBot }, waitingItems);
        var afterWaiting = Assert.Single(mergedWaiting);
        Assert.Equal(AgentState.Idle, afterWaiting.State);
        Assert.Null(afterWaiting.QuestionPrompt);
        Assert.False(afterWaiting.Unread);

        // Positive control: "needs-you" and "needs_you" bot items DO trigger NeedsApproval
        foreach (string approvalStatus in new[] { "needs-you", "needs_you" })
        {
            var needsYouItems = new List<BotItemInfo>
            {
                new(43, "b1", "Shikamaru (Team Leader)", "Deploy sign-off", approvalStatus, "Approve prod deploy?", now)
            };

            var mergedApproval = OrcaSnapshotParser.MergeSnapshots(new[] { idleBot }, needsYouItems);
            var afterApproval = Assert.Single(mergedApproval);
            Assert.Equal(AgentState.NeedsApproval, afterApproval.State);
            Assert.Equal("Approve prod deploy?", afterApproval.QuestionPrompt);
            Assert.True(afterApproval.Unread);
        }

        // End-to-end OrcaCliCollector check: bot item query requests ONLY needs-you (never waiting)
        // and even if a waiting item is present in output, the idle bot remains Idle in HqStateAggregator
        string[]? capturedItemArgs = null;
        const string idleWorktreeJson = """
        {
          "ok": true,
          "result": {
            "worktrees": [
              {
                "worktreeId": "folder:kazama-team-leader",
                "bot": "Shikamaru (Team Leader)",
                "botHome": true,
                "displayName": "Shikamaru (Team Leader)",
                "status": "idle",
                "sidebarStatus": "idle",
                "agents": [
                  {
                    "address": "b1",
                    "state": "idle",
                    "updatedAt": 1795545570739
                  }
                ]
              }
            ]
          }
        }
        """;

        const string waitingItemJson = """
        {
          "ok": true,
          "result": {
            "items": [
              {
                "number": 42,
                "owner": { "botId": "b1", "name": "Shikamaru (Team Leader)" },
                "title": "Waiting on external CI",
                "status": "waiting",
                "question": null
              }
            ]
          }
        }
        """;

        var aggregator = new HqStateAggregator(timeProvider: () => now);
        var collector = new OrcaCliCollector(
            aggregator,
            timeProvider: () => now,
            commandRunner: (args, _, _) =>
            {
                if (args.Length >= 2 && args[0] == "bot" && args[1] == "item")
                {
                    capturedItemArgs = args;
                    return Task.FromResult(new CliResult(0, waitingItemJson, ""));
                }
                if (args.Length >= 1 && args[0] == "worktree")
                {
                    return Task.FromResult(new CliResult(0, idleWorktreeJson, ""));
                }
                return Task.FromResult(new CliResult(0, """{ "ok": true, "result": { "bots": [] } }""", ""));
            });

        await collector.CollectOnceAsync();

        Assert.NotNull(capturedItemArgs);
        int statusIdx = Array.IndexOf(capturedItemArgs!, "--status");
        Assert.True(statusIdx >= 0 && statusIdx + 1 < capturedItemArgs!.Length);
        Assert.Equal("needs-you", capturedItemArgs![statusIdx + 1]);

        var state = aggregator.GetState();
        var bot = Assert.Single(state.Agents);
        Assert.Equal(AgentState.Idle, bot.State);
        Assert.Equal(HqMood.Calm, state.Mood);
    }
}
