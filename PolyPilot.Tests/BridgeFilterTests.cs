using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the bridge message type filtering feature.
/// Verifies that filtered message types are excluded from history sent to mobile
/// clients and that streaming events are suppressed for filtered types.
/// </summary>
public class BridgeFilterTests : IDisposable
{
    private readonly WsBridgeServer _server;
    private readonly CopilotService _copilot;
    private readonly int _port;
    private readonly string _tempSettingsPath;

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public BridgeFilterTests()
    {
        _port = GetFreePort();
        _server = new WsBridgeServer();
        _copilot = new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new StubWsBridgeClient(),
            new RepoManager(),
            new ServiceCollection().BuildServiceProvider(),
            new StubDemoService());

        // Use a temp settings file to avoid polluting real user settings
        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"polypilot-test-{Guid.NewGuid()}.json");
        ConnectionSettings.SetSettingsFilePathForTesting(_tempSettingsPath);

        _server.SetCopilotService(_copilot);
        _server.Start(_port, 0);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        ConnectionSettings.SetSettingsFilePathForTesting(null);
        try { File.Delete(_tempSettingsPath); } catch { }
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct, int pollMs = 50, int maxMs = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < maxMs)
            await Task.Delay(pollMs, ct);
        if (!condition())
            throw new TimeoutException($"WaitForAsync condition not met within {maxMs}ms");
    }

    private async Task InitDemoMode()
    {
        await _copilot.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
    }

    // ========== IsBridgeFiltered Unit Tests ==========

    [Fact]
    public void IsBridgeFiltered_DefaultFilter_ExcludesSystem()
    {
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.System));
    }

    [Fact]
    public void IsBridgeFiltered_DefaultFilter_AllowsAssistant()
    {
        Assert.False(_server.IsBridgeFiltered(ChatMessageType.Assistant));
    }

    [Fact]
    public void IsBridgeFiltered_DefaultFilter_AllowsUser()
    {
        Assert.False(_server.IsBridgeFiltered(ChatMessageType.User));
    }

    [Fact]
    public void IsBridgeFiltered_DefaultFilter_ExcludesToolCall()
    {
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.ToolCall));
    }

    [Fact]
    public void IsBridgeFiltered_DefaultFilter_ExcludesReasoning()
    {
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.Reasoning));
    }

    // ========== ReloadBridgeFilter Tests ==========

    [Fact]
    public void ReloadBridgeFilter_AddsToolCall_FiltersToolCall()
    {
        var settings = new ConnectionSettings
        {
            BridgeFilteredMessageTypes = new() { "System", "ToolCall" }
        };
        settings.Save();

        _server.ReloadBridgeFilter();

        Assert.True(_server.IsBridgeFiltered(ChatMessageType.System));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.ToolCall));
        Assert.False(_server.IsBridgeFiltered(ChatMessageType.Assistant));
    }

    [Fact]
    public void ReloadBridgeFilter_EmptyList_FiltersNothing()
    {
        var settings = new ConnectionSettings
        {
            BridgeFilteredMessageTypes = new()
        };
        settings.Save();

        _server.ReloadBridgeFilter();

        Assert.False(_server.IsBridgeFiltered(ChatMessageType.System));
        Assert.False(_server.IsBridgeFiltered(ChatMessageType.Assistant));
        Assert.False(_server.IsBridgeFiltered(ChatMessageType.ToolCall));
    }

    [Fact]
    public void ReloadBridgeFilter_AllTypes_FiltersEverything()
    {
        var settings = new ConnectionSettings
        {
            BridgeFilteredMessageTypes = new()
            {
                "System", "ToolCall", "Reasoning", "ShellOutput", "Diff", "Reflection"
            }
        };
        settings.Save();

        _server.ReloadBridgeFilter();

        Assert.True(_server.IsBridgeFiltered(ChatMessageType.System));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.ToolCall));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.Reasoning));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.ShellOutput));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.Diff));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.Reflection));
        // User and Assistant should never be filterable
        Assert.False(_server.IsBridgeFiltered(ChatMessageType.User));
        Assert.False(_server.IsBridgeFiltered(ChatMessageType.Assistant));
    }

    [Fact]
    public void ReloadBridgeFilter_InvalidTypeName_IgnoredGracefully()
    {
        var settings = new ConnectionSettings
        {
            BridgeFilteredMessageTypes = new() { "System", "NonExistentType", "ToolCall" }
        };
        settings.Save();

        _server.ReloadBridgeFilter();

        Assert.True(_server.IsBridgeFiltered(ChatMessageType.System));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.ToolCall));
        // Invalid name should just be ignored, no crash
    }

    [Fact]
    public void ReloadBridgeFilter_CaseInsensitive()
    {
        var settings = new ConnectionSettings
        {
            BridgeFilteredMessageTypes = new() { "system", "TOOLCALL", "reasoning" }
        };
        settings.Save();

        _server.ReloadBridgeFilter();

        Assert.True(_server.IsBridgeFiltered(ChatMessageType.System));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.ToolCall));
        Assert.True(_server.IsBridgeFiltered(ChatMessageType.Reasoning));
    }

    // ========== History Filtering Integration Tests ==========

    [Fact]
    public async Task SessionHistory_FiltersSystemMessages()
    {
        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Create a session and directly add mixed message types to history
        await _copilot.CreateSessionAsync("test-filter", "gpt-4.1");
        var session = _copilot.GetSession("test-filter")!;
        session.History.Add(ChatMessage.UserMessage("Hello"));
        session.History.Add(ChatMessage.AssistantMessage("Hi there"));
        session.History.Add(ChatMessage.SystemMessage("⚠️ Orchestrator status update"));
        session.History.Add(ChatMessage.AssistantMessage("Done"));

        // Connect client and switch to the test session to trigger history push
        var client = new WsBridgeClient();
        await client.ConnectAsync($"ws://localhost:{_port}/", null, cts.Token);
        await client.SwitchSessionAsync("test-filter", cts.Token);
        await WaitForAsync(() => client.SessionHistories.ContainsKey("test-filter"), cts.Token);

        // History should have User, 2 Assistant messages — System should be filtered
        var history = client.SessionHistories.GetValueOrDefault("test-filter");
        Assert.NotNull(history);
        Assert.DoesNotContain(history, m => m.MessageType == ChatMessageType.System);
        Assert.Contains(history, m => m.MessageType == ChatMessageType.User);
        Assert.Equal(2, history.Count(m => m.MessageType == ChatMessageType.Assistant));
    }

    [Fact]
    public async Task SessionHistory_NoFilter_IncludesAllTypes()
    {
        // Clear all filters
        var settings = new ConnectionSettings { BridgeFilteredMessageTypes = new() };
        settings.Save();
        _server.ReloadBridgeFilter();

        await InitDemoMode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await _copilot.CreateSessionAsync("test-no-filter", "gpt-4.1");
        var session = _copilot.GetSession("test-no-filter")!;
        session.History.Add(ChatMessage.UserMessage("Hello"));
        session.History.Add(ChatMessage.SystemMessage("System msg"));
        session.History.Add(ChatMessage.AssistantMessage("Done"));

        var client = new WsBridgeClient();
        await client.ConnectAsync($"ws://localhost:{_port}/", null, cts.Token);
        await client.SwitchSessionAsync("test-no-filter", cts.Token);
        await WaitForAsync(() => client.SessionHistories.ContainsKey("test-no-filter"), cts.Token);

        var history = client.SessionHistories.GetValueOrDefault("test-no-filter");
        Assert.NotNull(history);
        Assert.Contains(history, m => m.MessageType == ChatMessageType.System);
        Assert.Equal(3, history.Count);
    }

    // ========== Settings Persistence Tests ==========

    [Fact]
    public void BridgeFilteredMessageTypes_DefaultFiltersVerboseTypes()
    {
        var settings = new ConnectionSettings();
        Assert.Contains("System", settings.BridgeFilteredMessageTypes);
        Assert.Contains("ToolCall", settings.BridgeFilteredMessageTypes);
        Assert.Contains("Reasoning", settings.BridgeFilteredMessageTypes);
        Assert.Contains("ShellOutput", settings.BridgeFilteredMessageTypes);
        Assert.Contains("Diff", settings.BridgeFilteredMessageTypes);
        Assert.Contains("Reflection", settings.BridgeFilteredMessageTypes);
    }

    [Fact]
    public void BridgeFilteredMessageTypes_RoundTrips()
    {
        var settings = new ConnectionSettings
        {
            BridgeFilteredMessageTypes = new() { "System", "ToolCall", "Reasoning" }
        };
        settings.Save();

        var loaded = ConnectionSettings.Load();
        Assert.Equal(3, loaded.BridgeFilteredMessageTypes.Count);
        Assert.Contains("System", loaded.BridgeFilteredMessageTypes);
        Assert.Contains("ToolCall", loaded.BridgeFilteredMessageTypes);
        Assert.Contains("Reasoning", loaded.BridgeFilteredMessageTypes);
    }

    // ========== Orchestrator Content Filtering Tests ==========

    [Fact]
    public void IsOrchestratorBoilerplate_PlanningPrompt_ReturnsTrue()
    {
        var content = "You are the orchestrator of a multi-agent group. You have 5 worker agent(s) available:\n  - 'team-worker-1' (model: claude-sonnet-4.6)";
        Assert.True(WsBridgeServer.IsOrchestratorBoilerplate(content));
    }

    [Fact]
    public void IsOrchestratorBoilerplate_WorkerBlock_ReturnsTrue()
    {
        Assert.True(WsBridgeServer.IsOrchestratorBoilerplate("Here are the assignments:\n@worker:team-worker-1\nReview the PR\n@end"));
    }

    [Fact]
    public void IsOrchestratorBoilerplate_UserRequest_ReturnsTrue()
    {
        Assert.True(WsBridgeServer.IsOrchestratorBoilerplate("## User Request\nPlease fix the bug in auth.cs"));
    }

    [Fact]
    public void IsOrchestratorBoilerplate_WorkerResults_ReturnsTrue()
    {
        Assert.True(WsBridgeServer.IsOrchestratorBoilerplate("## Worker Results\nWorker 1 completed successfully."));
    }

    [Fact]
    public void IsOrchestratorBoilerplate_NormalMessage_ReturnsFalse()
    {
        Assert.False(WsBridgeServer.IsOrchestratorBoilerplate("Please review the PR and fix the auth bug"));
    }

    [Fact]
    public void IsOrchestratorBoilerplate_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(WsBridgeServer.IsOrchestratorBoilerplate(null));
        Assert.False(WsBridgeServer.IsOrchestratorBoilerplate(""));
    }

    [Fact]
    public void IsOrchestratorBoilerplate_ReflectComplete_ReturnsTrue()
    {
        Assert.True(WsBridgeServer.IsOrchestratorBoilerplate("All tasks completed. [[GROUP_REFLECT_COMPLETE]]"));
    }
}