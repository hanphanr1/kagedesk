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
