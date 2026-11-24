using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KageDesk.Core.Bridge;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using Xunit;

namespace KageDesk.Core.Tests;

public class LocalEventBridgeTests
{
    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task GetHealth_ReturnsExpectedPayload()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{port}/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.Equal("shinobi-hq", root.GetProperty("activeTheme").GetString());
        Assert.True(root.GetProperty("workingSetMb").GetDouble() > 0);
        Assert.True(root.TryGetProperty("uptimeSec", out _));
        Assert.Equal(0, root.GetProperty("agentCount").GetInt32());
    }

    [Fact]
    public async Task GetState_ReturnsCurrentHqState()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("test-1", "Minato", AgentRole.Coder, "minato", AgentState.Working)
        }, Array.Empty<AccountInfo>());

        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{port}/api/state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("agents", out var agents));
        Assert.Equal(1, agents.GetArrayLength());
        Assert.Equal("Minato", agents[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task PostEvent_AgentBuildAndAccount_UpdatesAggregator()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();

        // 1. Post Agent event
        var agentJson = JsonSerializer.Serialize(new
        {
            agentId = "bridge-1",
            name = "Bridge Coder",
            role = "Coder",
            state = "Working",
            toolName = "compile",
            message = "Building project"
        });
        var agentRes = await client.PostAsync($"http://127.0.0.1:{port}/api/event",
            new StringContent(agentJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, agentRes.StatusCode);

        // 2. Post Build event
        var buildJson = JsonSerializer.Serialize(new
        {
            repo = "hanphanr1/kagedesk",
            buildStatus = "success",
            message = "Release build passed"
        });
        var buildRes = await client.PostAsync($"http://127.0.0.1:{port}/api/event",
            new StringContent(buildJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, buildRes.StatusCode);

        // 3. Post Account event
        var accountJson = JsonSerializer.Serialize(new
        {
            accountProvider = "Claude",
            accountName = "claude-pro",
            quotaPercent = 85.5
        });
        var accountRes = await client.PostAsync($"http://127.0.0.1:{port}/api/event",
            new StringContent(accountJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, accountRes.StatusCode);

        // Verify aggregator updated
        var state = aggregator.GetState();
        Assert.Contains(state.Agents, a => a.Id == "bridge-1" && a.State == AgentState.Working);
        Assert.Contains(state.Builds, b => b.Repo == "hanphanr1/kagedesk" && b.IsSuccessful);
        Assert.Contains(state.Accounts, acc => acc.Provider == "Claude" && acc.QuotaPercent == 85.5);
    }

    [Fact]
    public async Task PostDemoScenario_ValidAndInvalid_HandlesCorrectly()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();

        // Valid demo
        var validRes = await client.PostAsync($"http://127.0.0.1:{port}/api/demo/working", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, validRes.StatusCode);
        Assert.Equal("working", aggregator.GetState().DemoScenario);

        // Clear demo
        var clearRes = await client.PostAsync($"http://127.0.0.1:{port}/api/demo/clear", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, clearRes.StatusCode);
        Assert.Null(aggregator.GetState().DemoScenario);

        // Invalid demo
        var invalidRes = await client.PostAsync($"http://127.0.0.1:{port}/api/demo/unknown_mode", new StringContent(""));
        Assert.Equal(HttpStatusCode.BadRequest, invalidRes.StatusCode);
    }

    [Fact]
    public async Task PostEvent_PayloadTooLarge_Returns413()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();

        // Payload > 32KB
        var oversizedPayload = new string('x', 33 * 1024);
        var content = new StringContent(oversizedPayload, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"http://127.0.0.1:{port}/api/event", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task PostEvent_InvalidJson_Returns400BadRequest()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();
        var content = new StringContent("{ not valid json: }", Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"http://127.0.0.1:{port}/api/event", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostEvent_TrueChunkedTransfer_Over32Kb_Returns413()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();

        // Stream 35KB with chunked transfer (no Content-Length header)
        byte[] oversizedData = Encoding.UTF8.GetBytes(new string('a', 35 * 1024));
        using var stream = new MemoryStream(oversizedData);
        using var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/event")
        {
            Content = streamContent
        };
        request.Headers.TransferEncodingChunked = true;

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task TokenAuth_EnforcedOnProtectedEndpoints_AllowsHealthWithoutToken()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        const string secretToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        await using var bridge = new LocalEventBridge(aggregator, port, requiredToken: secretToken);
        bridge.Start();

        using var client = new HttpClient();

        // 1. Health probe works without token
        var healthRes = await client.GetAsync($"http://127.0.0.1:{port}/api/health");
        Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);

        // 2. State without token -> 401
        var stateNoToken = await client.GetAsync($"http://127.0.0.1:{port}/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, stateNoToken.StatusCode);

        // 3. State with wrong token -> 401
        using var reqWrong = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/state");
        reqWrong.Headers.Add("X-KageDesk-Token", "wrong-token-value");
        var stateWrong = await client.SendAsync(reqWrong);
        Assert.Equal(HttpStatusCode.Unauthorized, stateWrong.StatusCode);

        // 4. State with valid token -> 200
        using var reqValid = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/state");
        reqValid.Headers.Add("X-KageDesk-Token", secretToken);
        var stateValid = await client.SendAsync(reqValid);
        Assert.Equal(HttpStatusCode.OK, stateValid.StatusCode);

        // 5. Post event without token -> 401
        var eventNoToken = await client.PostAsync($"http://127.0.0.1:{port}/api/event",
            new StringContent("{\"agentId\":\"test\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, eventNoToken.StatusCode);

        // 6. Post event with valid token -> 200
        using var eventReqValid = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/event")
        {
            Content = new StringContent("{\"agentId\":\"test\",\"state\":\"Working\"}", Encoding.UTF8, "application/json")
        };
        eventReqValid.Headers.Add("X-KageDesk-Token", secretToken);
        var eventValid = await client.SendAsync(eventReqValid);
        Assert.Equal(HttpStatusCode.OK, eventValid.StatusCode);

        // 7. Demo endpoint with valid token -> 200
        using var demoReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/demo/working");
        demoReq.Headers.Add("X-KageDesk-Token", secretToken);
        var demoValid = await client.SendAsync(demoReq);
        Assert.Equal(HttpStatusCode.OK, demoValid.StatusCode);
    }

    [Fact]
    public async Task Health_DynamicallyReflectsLiveActiveTheme_WithoutToken()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        string currentTheme = "shinobi-hq";

        await using var bridge = new LocalEventBridge(
            aggregator,
            port,
            requiredToken: "secret-test-token",
            getActiveThemeId: () => currentTheme);
        bridge.Start();

        using var client = new HttpClient();

        var res1 = await client.GetAsync($"http://127.0.0.1:{port}/api/health");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        using var doc1 = JsonDocument.Parse(await res1.Content.ReadAsStringAsync());
        Assert.Equal("shinobi-hq", doc1.RootElement.GetProperty("activeTheme").GetString());

        // Change live theme dynamically
        currentTheme = "cyber-matrix";

        var res2 = await client.GetAsync($"http://127.0.0.1:{port}/api/health");
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        using var doc2 = JsonDocument.Parse(await res2.Content.ReadAsStringAsync());
        Assert.Equal("cyber-matrix", doc2.RootElement.GetProperty("activeTheme").GetString());
    }

    [Fact]
    public async Task GetState_OmitsRawLastMessage_ForSecurity()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("ag-secret", "Secret Agent", AgentRole.Coder, "minato", AgentState.Working,
                LastMessage: "Sensitive prompt or internal chat text")
        }, Array.Empty<AccountInfo>());

        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{port}/api/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var agents = doc.RootElement.GetProperty("agents");
        Assert.Equal(1, agents.GetArrayLength());

        var agentObj = agents[0];
        Assert.Equal("ag-secret", agentObj.GetProperty("id").GetString());
        // LastMessage must be omitted or null
        if (agentObj.TryGetProperty("lastMessage", out var lastMsg))
        {
            Assert.Equal(JsonValueKind.Null, lastMsg.ValueKind);
        }
    }

    [Theory]
    [InlineData("tool_start", AgentState.Working)]
    [InlineData("working", AgentState.Working)]
    [InlineData("start", AgentState.Working)]
    [InlineData("tool_done", AgentState.Done)]
    [InlineData("done", AgentState.Done)]
    [InlineData("pass", AgentState.Done)]
    [InlineData("notification", AgentState.NeedsApproval)]
    [InlineData("approval", AgentState.NeedsApproval)]
    [InlineData("needs_approval", AgentState.NeedsApproval)]
    [InlineData("fail", AgentState.Failed)]
    [InlineData("failed", AgentState.Failed)]
    [InlineData("idle", AgentState.Idle)]
    [InlineData("stop", AgentState.Idle)]
    public async Task PostEvent_HookTypeMapping_ResolvesAgentStateCleanly(string hookType, AgentState expectedState)
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();
        string payload = $"{{\"agentId\":\"ag-type-test\",\"type\":\"{hookType}\"}}";
        var res = await client.PostAsync($"http://127.0.0.1:{port}/api/event",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var agent = aggregator.GetState().Agents.FirstOrDefault(a => a.Id == "ag-type-test");
        Assert.NotNull(agent);
        Assert.Equal(expectedState, agent.State);
    }

    [Fact]
    public async Task PostEvent_ExplicitState_WinsOverType()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        await using var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();

        using var client = new HttpClient();
        // type is tool_start (which would be Working), but explicit state is Idle
        string payload = "{\"agentId\":\"ag-override\",\"type\":\"tool_start\",\"state\":\"Idle\"}";
        var res = await client.PostAsync($"http://127.0.0.1:{port}/api/event",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var agent = aggregator.GetState().Agents.FirstOrDefault(a => a.Id == "ag-override");
        Assert.NotNull(agent);
        Assert.Equal(AgentState.Idle, agent.State);
    }

    [Fact]
    public async Task Shutdown_CleansUpGracefully()
    {
        var port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        var bridge = new LocalEventBridge(aggregator, port);
        bridge.Start();
        Assert.True(bridge.IsRunning);

        await bridge.DisposeAsync();
        Assert.False(bridge.IsRunning);
    }
}
