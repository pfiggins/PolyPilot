using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for RefreshGroupFromPreset: verifies that a multi-agent group's settings
/// are updated in place when its source preset changes, without requiring a relaunch.
/// </summary>
[Collection("BaseDir")]
public class PresetRefreshTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly IServiceProvider _serviceProvider;

    public PresetRefreshTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, new RepoManager(), _serviceProvider, _demoService);

    /// <summary>
    /// Build a minimal organization state: a multi-agent group with an orchestrator and workers,
    /// linked to a preset by SourcePresetName.
    /// </summary>
    private static (SessionGroup group, string orchName, string[] workerNames) SetupGroup(
        CopilotService svc,
        string presetName,
        MultiAgentMode initialMode,
        int workerCount = 2)
    {
        var group = svc.CreateMultiAgentGroup("test-team", initialMode);
        group.SourcePresetName = presetName;

        var orchName = "test-team-orchestrator";
        svc.Organization.Sessions.Add(new SessionMeta
        {
            SessionName = orchName,
            GroupId = group.Id,
            Role = MultiAgentRole.Orchestrator,
            PreferredModel = "claude-opus-4.6"
        });

        var workerNames = new string[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workerNames[i] = $"test-team-worker-{i + 1}";
            svc.Organization.Sessions.Add(new SessionMeta
            {
                SessionName = workerNames[i],
                GroupId = group.Id,
                Role = MultiAgentRole.Worker,
                PreferredModel = $"worker-model-original",
                SystemPrompt = $"Original prompt {i + 1}"
            });
        }

        return (group, orchName, workerNames);
    }

    // --- CreateGroupFromPresetAsync sets SourcePresetName ---

    [Fact]
    public void CreateMultiAgentGroup_DoesNotSetSourcePresetName_ByDefault()
    {
        var svc = CreateService();
        var group = svc.CreateMultiAgentGroup("plain-group", MultiAgentMode.Broadcast);
        Assert.Null(group.SourcePresetName);
    }

    // --- RefreshGroupFromPreset: group-level settings ---

    [Fact]
    public void Refresh_UpdatesOrchestratorMode_WhenPresetChanged()
    {
        var svc = CreateService();
        var (group, _, _) = SetupGroup(svc, "My Preset", MultiAgentMode.Broadcast);

        // Update the preset on disk with a new mode
        var updatedPreset = new GroupPreset("My Preset", "Desc", "⭐", MultiAgentMode.OrchestratorReflect,
            "claude-opus-4.6", new[] { "claude-sonnet-4.6", "claude-sonnet-4.6" });
        UserPresets.Save(CopilotService.BaseDir, new List<GroupPreset> { updatedPreset });

        var result = svc.RefreshGroupFromPreset(group.Id);

        Assert.True(result);
        Assert.Equal(MultiAgentMode.OrchestratorReflect, group.OrchestratorMode);
    }

    [Fact]
    public void Refresh_UpdatesSharedContext_WhenPresetChanged()
    {
        var svc = CreateService();
        var (group, _, _) = SetupGroup(svc, "Context Preset", MultiAgentMode.Orchestrator);

        var updatedPreset = new GroupPreset("Context Preset", "Desc", "⭐", MultiAgentMode.Orchestrator,
            "claude-opus-4.6", new[] { "claude-sonnet-4.6" })
        {
            SharedContext = "Always prioritize security.",
            RoutingContext = "Route auth tasks to worker-1."
        };
        UserPresets.Save(CopilotService.BaseDir, new List<GroupPreset> { updatedPreset });

        svc.RefreshGroupFromPreset(group.Id);

        Assert.Equal("Always prioritize security.", group.SharedContext);
        Assert.Equal("Route auth tasks to worker-1.", group.RoutingContext);
    }

    [Fact]
    public void Refresh_UpdatesMaxReflectIterations_WhenPresetChanged()
    {
        var svc = CreateService();
        var (group, _, _) = SetupGroup(svc, "Reflect Preset", MultiAgentMode.OrchestratorReflect);

        var updatedPreset = new GroupPreset("Reflect Preset", "Desc", "⭐", MultiAgentMode.OrchestratorReflect,
            "claude-opus-4.6", new[] { "claude-sonnet-4.6" })
        {
            MaxReflectIterations = 12
        };
        UserPresets.Save(CopilotService.BaseDir, new List<GroupPreset> { updatedPreset });

        svc.RefreshGroupFromPreset(group.Id);

        Assert.Equal(12, group.MaxReflectIterations);
    }

    // --- RefreshGroupFromPreset: per-session settings ---

    [Fact]
    public void Refresh_UpdatesOrchestratorModel_WhenPresetChanged()
    {
        var svc = CreateService();
        var (group, orchName, _) = SetupGroup(svc, "Model Preset", MultiAgentMode.Orchestrator);

        var updatedPreset = new GroupPreset("Model Preset", "Desc", "⭐", MultiAgentMode.Orchestrator,
            "gpt-5.2", new[] { "claude-sonnet-4.6" });
        UserPresets.Save(CopilotService.BaseDir, new List<GroupPreset> { updatedPreset });

        svc.RefreshGroupFromPreset(group.Id);

        var orchMeta = svc.Organization.Sessions.First(s => s.SessionName == orchName);
        Assert.Equal("gpt-5.2", orchMeta.PreferredModel);
    }

    [Fact]
    public void Refresh_UpdatesWorkerModels_WhenPresetChanged()
    {
        var svc = CreateService();
        var (group, _, workerNames) = SetupGroup(svc, "Worker Model Preset", MultiAgentMode.Broadcast, workerCount: 2);

        var updatedPreset = new GroupPreset("Worker Model Preset", "Desc", "⭐", MultiAgentMode.Broadcast,
            "claude-opus-4.6", new[] { "gpt-5.2", "gemini-3-pro-preview" });
        UserPresets.Save(CopilotService.BaseDir, new List<GroupPreset> { updatedPreset });

        svc.RefreshGroupFromPreset(group.Id);

        var worker1 = svc.Organization.Sessions.First(s => s.SessionName == workerNames[0]);
        var worker2 = svc.Organization.Sessions.First(s => s.SessionName == workerNames[1]);
        Assert.Equal("gpt-5.2", worker1.PreferredModel);
        Assert.Equal("gemini-3-pro-preview", worker2.PreferredModel);
    }

    [Fact]
    public void Refresh_UpdatesWorkerSystemPrompts_WhenPresetChanged()
    {
        var svc = CreateService();
        var (group, _, workerNames) = SetupGroup(svc, "Prompt Preset", MultiAgentMode.Broadcast, workerCount: 2);

        var updatedPreset = new GroupPreset("Prompt Preset", "Desc", "⭐", MultiAgentMode.Broadcast,
            "claude-opus-4.6", new[] { "claude-sonnet-4.6", "claude-sonnet-4.6" })
        {
            WorkerSystemPrompts = new[] { "You are a security expert.", "You are a performance expert." }
        };
        UserPresets.Save(CopilotService.BaseDir, new List<GroupPreset> { updatedPreset });

        svc.RefreshGroupFromPreset(group.Id);

        var worker1 = svc.Organization.Sessions.First(s => s.SessionName == workerNames[0]);
        var worker2 = svc.Organization.Sessions.First(s => s.SessionName == workerNames[1]);
        Assert.Equal("You are a security expert.", worker1.SystemPrompt);
        Assert.Equal("You are a performance expert.", worker2.SystemPrompt);
    }

    [Fact]
    public void Refresh_OnlyUpdatesFirstNWorkers_WhenPresetHasFewerSlotsThanGroup()
    {
        var svc = CreateService();
        // Group has 3 workers, preset only defines 1
        var (group, _, workerNames) = SetupGroup(svc, "Partial Preset", MultiAgentMode.Broadcast, workerCount: 3);
        // Assign a distinct model to worker-3 so we can confirm it's not overwritten
        svc.Organization.Sessions.First(s => s.SessionName == workerNames[2]).PreferredModel = "custom-model";

        var updatedPreset = new GroupPreset("Partial Preset", "Desc", "⭐", MultiAgentMode.Broadcast,
            "claude-opus-4.6", new[] { "gpt-5.2" })
        {
            WorkerSystemPrompts = new[] { "New prompt for worker 1." }
        };
        UserPresets.Save(CopilotService.BaseDir, new List<GroupPreset> { updatedPreset });

        svc.RefreshGroupFromPreset(group.Id);

        var worker1 = svc.Organization.Sessions.First(s => s.SessionName == workerNames[0]);
        var worker3 = svc.Organization.Sessions.First(s => s.SessionName == workerNames[2]);
        Assert.Equal("gpt-5.2", worker1.PreferredModel);
        Assert.Equal("New prompt for worker 1.", worker1.SystemPrompt);
        // Worker 3 is beyond the preset's worker count — untouched
        Assert.Equal("custom-model", worker3.PreferredModel);
    }

    // --- RefreshGroupFromPreset: graceful failure cases ---

    [Fact]
    public void Refresh_ReturnsFalse_WhenGroupHasNoSourcePreset()
    {
        var svc = CreateService();
        var group = svc.CreateMultiAgentGroup("no-preset-group", MultiAgentMode.Broadcast);
        // SourcePresetName is null — no preset linked

        var result = svc.RefreshGroupFromPreset(group.Id);

        Assert.False(result);
    }

    [Fact]
    public void Refresh_ReturnsFalse_WhenPresetNotFoundOnDisk()
    {
        var svc = CreateService();
        var group = svc.CreateMultiAgentGroup("orphaned-group", MultiAgentMode.Broadcast);
        group.SourcePresetName = "Nonexistent Preset";
        // No preset saved to disk

        var result = svc.RefreshGroupFromPreset(group.Id);

        Assert.False(result);
    }

    [Fact]
    public void Refresh_ReturnsFalse_WhenGroupIdDoesNotExist()
    {
        var svc = CreateService();

        var result = svc.RefreshGroupFromPreset("nonexistent-group-id");

        Assert.False(result);
    }

    [Fact]
    public void Refresh_ReturnsFalse_WhenGroupIsNotMultiAgent()
    {
        var svc = CreateService();
        // Add a regular (non-multi-agent) group directly
        var group = new SessionGroup { Id = "regular-group", Name = "Regular", IsMultiAgent = false, SourcePresetName = "Some Preset" };
        svc.Organization.Groups.Add(group);

        var result = svc.RefreshGroupFromPreset("regular-group");

        Assert.False(result);
    }

    // --- SourcePresetName persists through JSON round-trip ---

    [Fact]
    public void SourcePresetName_RoundTripsViaJson()
    {
        var state = new OrganizationState();
        state.Groups.Add(new SessionGroup
        {
            Id = "test-group",
            Name = "My Team",
            IsMultiAgent = true,
            SourcePresetName = "PR Review Squad"
        });

        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var restored = System.Text.Json.JsonSerializer.Deserialize<OrganizationState>(json)!;

        var group = restored.Groups.First(g => g.Id == "test-group");
        Assert.Equal("PR Review Squad", group.SourcePresetName);
    }

    [Fact]
    public void SourcePresetName_IsNull_WhenMissingFromJson()
    {
        // Old organization.json files won't have SourcePresetName — should deserialize as null
        var json = """{"Groups":[{"Id":"g1","Name":"Team","IsMultiAgent":true}],"Sessions":[]}""";
        var state = System.Text.Json.JsonSerializer.Deserialize<OrganizationState>(json)!;
        Assert.Null(state.Groups[0].SourcePresetName);
    }
}
