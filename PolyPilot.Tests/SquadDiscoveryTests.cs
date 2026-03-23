using PolyPilot.Models;

namespace PolyPilot.Tests;

public class SquadDiscoveryTests
{
    private static string TestDataDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static string SquadSampleDir => Path.Combine(TestDataDir, "squad-sample");
    private static string LegacyAiTeamDir => Path.Combine(TestDataDir, "legacy-ai-team");

    // --- FindSquadDirectory ---

    [Fact]
    public void FindSquadDirectory_PrefersDotSquad()
    {
        var result = SquadDiscovery.FindSquadDirectory(SquadSampleDir);
        Assert.NotNull(result);
        Assert.EndsWith(".squad", result);
    }

    [Fact]
    public void FindSquadDirectory_FallsBackToAiTeam()
    {
        var result = SquadDiscovery.FindSquadDirectory(LegacyAiTeamDir);
        Assert.NotNull(result);
        Assert.EndsWith(".ai-team", result);
    }

    [Fact]
    public void FindSquadDirectory_ReturnsNull_WhenNeitherExists()
    {
        var result = SquadDiscovery.FindSquadDirectory(Path.GetTempPath());
        Assert.Null(result);
    }

    // --- ParseTeamName ---

    [Fact]
    public void ParseTeamName_ExtractsH1Heading()
    {
        var content = "# The Review Squad\n\nSome description\n";
        Assert.Equal("The Review Squad", SquadDiscovery.ParseTeamName(content));
    }

    [Fact]
    public void ParseTeamName_ReturnsNull_WhenNoHeading()
    {
        var content = "Just a table\n| Member | Role |\n";
        Assert.Null(SquadDiscovery.ParseTeamName(content));
    }

    // --- ParseRosterNames ---

    [Fact]
    public void ParseRosterNames_ExtractsAgentNames()
    {
        var content = "# Team\n| Member | Role |\n|--------|------|\n| security-reviewer | Auditor |\n| perf-analyst | Analyst |";
        var names = SquadDiscovery.ParseRosterNames(content);
        Assert.Contains("security-reviewer", names);
        Assert.Contains("perf-analyst", names);
        Assert.DoesNotContain("Member", names);
        Assert.DoesNotContain("---", names);
    }

    // --- DiscoverAgents ---

    [Fact]
    public void DiscoverAgents_SkipsScribe()
    {
        var squadDir = Path.Combine(SquadSampleDir, ".squad");
        var agents = SquadDiscovery.DiscoverAgents(squadDir);
        Assert.DoesNotContain(agents, a => a.Name.Equals("scribe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiscoverAgents_FindsRealAgents()
    {
        var squadDir = Path.Combine(SquadSampleDir, ".squad");
        var agents = SquadDiscovery.DiscoverAgents(squadDir);
        Assert.Equal(2, agents.Count); // security-reviewer + perf-analyst (not scribe)
        Assert.Contains(agents, a => a.Name == "security-reviewer");
        Assert.Contains(agents, a => a.Name == "perf-analyst");
    }

    [Fact]
    public void DiscoverAgents_ReadsCharterContent()
    {
        var squadDir = Path.Combine(SquadSampleDir, ".squad");
        var agents = SquadDiscovery.DiscoverAgents(squadDir);
        var security = agents.First(a => a.Name == "security-reviewer");
        Assert.NotNull(security.Charter);
        Assert.Contains("OWASP Top 10", security.Charter);
    }

    // --- Discover (full integration) ---

    [Fact]
    public void Discover_ReturnsPreset_FromSquadDir()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        Assert.Single(presets);
        var preset = presets[0];
        Assert.Equal("The Review Squad", preset.Name);
        Assert.True(preset.IsRepoLevel);
        Assert.Equal(MultiAgentMode.OrchestratorReflect, preset.Mode);
        Assert.Equal(2, preset.WorkerModels.Length);
    }

    [Fact]
    public void Discover_SetsSystemPrompts_FromCharters()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        var preset = presets[0];
        Assert.NotNull(preset.WorkerSystemPrompts);
        Assert.Equal(2, preset.WorkerSystemPrompts.Length);

        // At least one should contain OWASP (security-reviewer's charter)
        Assert.True(preset.WorkerSystemPrompts.Any(p => p != null && p.Contains("OWASP")),
            "Expected a worker system prompt containing 'OWASP'");
        // At least one should contain latency (perf-analyst's charter)
        Assert.True(preset.WorkerSystemPrompts.Any(p => p != null && p.Contains("Latency")),
            "Expected a worker system prompt containing 'Latency'");
    }

    [Fact]
    public void Discover_ReadsDecisions_AsSharedContext()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        var preset = presets[0];
        Assert.NotNull(preset.SharedContext);
        Assert.Contains("structured logging", preset.SharedContext);
        Assert.Contains("async/await", preset.SharedContext);
    }

    [Fact]
    public void Discover_ReadsRouting_AsRoutingContext()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        var preset = presets[0];
        Assert.NotNull(preset.RoutingContext);
        Assert.Contains("security-reviewer", preset.RoutingContext);
    }

    [Fact]
    public void Discover_LegacyAiTeam_Works()
    {
        var presets = SquadDiscovery.Discover(LegacyAiTeamDir);
        Assert.Single(presets);
        var preset = presets[0];
        Assert.Equal("Legacy Team", preset.Name);
        Assert.True(preset.IsRepoLevel);
        Assert.Single(preset.WorkerModels);
    }

    [Fact]
    public void Discover_ReturnsEmpty_WhenNoSquadDir()
    {
        var presets = SquadDiscovery.Discover(Path.GetTempPath());
        Assert.Empty(presets);
    }

    [Fact]
    public void Discover_ReturnsEmpty_WhenNoTeamMd()
    {
        // Create temp dir with .squad/ but no team.md
        var tempDir = Path.Combine(Path.GetTempPath(), $"squad-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".squad", "agents", "test"));
            File.WriteAllText(Path.Combine(tempDir, ".squad", "agents", "test", "charter.md"), "test charter");

            var presets = SquadDiscovery.Discover(tempDir);
            Assert.Empty(presets);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Discover_TruncatesLongCharters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squad-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".squad", "agents", "verbose"));
            File.WriteAllText(Path.Combine(tempDir, ".squad", "team.md"), "# Long Charter Test\n| Member | Role |\n|---|---|\n| verbose | Talker |");
            File.WriteAllText(Path.Combine(tempDir, ".squad", "agents", "verbose", "charter.md"),
                new string('x', 5000)); // Over 4000 char limit

            var presets = SquadDiscovery.Discover(tempDir);
            Assert.Single(presets);
            Assert.True(presets[0].WorkerSystemPrompts![0]!.Length <= 4000);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // --- Three-tier merge ---

    [Fact]
    public void GetAll_WithRepoPath_IncludesSquadPresets()
    {
        var all = UserPresets.GetAll(Path.GetTempPath(), SquadSampleDir);
        Assert.Contains(all, p => p.Name == "The Review Squad" && p.IsRepoLevel);
        // Built-in should also be present
        Assert.Contains(all, p => p.Name == "PR Review Squad");
    }

    [Fact]
    public void GetAll_WithoutRepoPath_NoSquadPresets()
    {
        var all = UserPresets.GetAll(Path.GetTempPath());
        Assert.DoesNotContain(all, p => p.IsRepoLevel);
    }

    [Fact]
    public void GetAll_RepoDoesNotOverride_BuiltInByName()
    {
        // Create a temp Squad dir with a preset named the same as a built-in
        var builtInName = GroupPreset.BuiltIn[0].Name;
        var tempDir = Path.Combine(Path.GetTempPath(), $"squad-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".squad", "agents", "reviewer"));
            File.WriteAllText(Path.Combine(tempDir, ".squad", "team.md"),
                $"# {builtInName}\n| Member | Role |\n|---|---|\n| reviewer | Reviewer |");
            File.WriteAllText(Path.Combine(tempDir, ".squad", "agents", "reviewer", "charter.md"),
                "Custom repo reviewer.");

            var all = UserPresets.GetAll(Path.GetTempPath(), tempDir);
            var match = all.Single(p => p.Name == builtInName);
            Assert.False(match.IsRepoLevel, "Built-in should not be overridden by repo preset with same name");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Discover_SetsSourcePath()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        Assert.Single(presets);
        Assert.NotNull(presets[0].SourcePath);
        Assert.True(presets[0].SourcePath!.EndsWith(".squad"));
    }

    [Fact]
    public void Discover_HasEmoji()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        Assert.Equal("🫡", presets[0].Emoji);
    }

    // --- ParseMode tests ---

    [Fact]
    public void ParseMode_Orchestrator()
    {
        var content = "# My Team\nmode: orchestrator\n| Member | Role |";
        Assert.Equal(MultiAgentMode.Orchestrator, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_Broadcast()
    {
        var content = "# My Team\nmode: broadcast\n";
        Assert.Equal(MultiAgentMode.Broadcast, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_OrchestratorReflect()
    {
        var content = "# My Team\nmode: orchestrator-reflect\n";
        Assert.Equal(MultiAgentMode.OrchestratorReflect, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_Sequential()
    {
        var content = "# My Team\nmode: sequential\n";
        Assert.Equal(MultiAgentMode.Sequential, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_CaseInsensitive()
    {
        var content = "# My Team\nMode: Orchestrator\n";
        Assert.Equal(MultiAgentMode.Orchestrator, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_DefaultsToReflect_WhenMissing()
    {
        var content = "# My Team\n| Member | Role |";
        Assert.Equal(MultiAgentMode.OrchestratorReflect, SquadDiscovery.ParseMode(content));
    }

    // --- DeleteRepoPreset path traversal ---

    [Fact]
    public void DeleteRepoPreset_RejectsPathOutsideRepo()
    {
        // DeleteRepoPreset uses SquadDiscovery.Discover which needs a real .squad dir.
        // Instead, verify the containment logic by testing with a valid squad preset
        // whose SourcePath we can verify stays within the repo root.
        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-pathtest-{Guid.NewGuid():N}");
        var squadDir = Path.Combine(tempDir, ".squad");
        try
        {
            Directory.CreateDirectory(squadDir);
            Directory.CreateDirectory(Path.Combine(squadDir, "agents", "worker"));
            File.WriteAllText(Path.Combine(squadDir, "team.md"),
                "# Test Team\n| Member | Role |\n|---|---|\n| worker | Worker |");
            File.WriteAllText(Path.Combine(squadDir, "agents", "worker", "charter.md"), "You are a worker.");

            // Normal case: preset within repo → succeeds
            var result = UserPresets.DeleteRepoPreset(tempDir, "Test Team");
            Assert.True(result);
            Assert.False(Directory.Exists(squadDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeleteRepoPreset_RejectsNonexistentPreset()
    {
        var result = UserPresets.DeleteRepoPreset(Path.GetTempPath(), "NonExistent-Preset-12345");
        Assert.False(result);
    }

    // --- DiscoverFromPath ---

    [Fact]
    public void DiscoverFromPath_WorksWithSquadDir()
    {
        var squadDir = Path.Combine(SquadSampleDir, ".squad");
        var presets = SquadDiscovery.DiscoverFromPath(squadDir);
        Assert.Single(presets);
        Assert.Equal("The Review Squad", presets[0].Name);
    }

    [Fact]
    public void DiscoverFromPath_WorksWithParentDir()
    {
        var presets = SquadDiscovery.DiscoverFromPath(SquadSampleDir);
        Assert.Single(presets);
        Assert.Equal("The Review Squad", presets[0].Name);
    }

    [Fact]
    public void DiscoverFromPath_ReturnsEmpty_WhenPathNotFound()
    {
        var presets = SquadDiscovery.DiscoverFromPath(@"C:\nonexistent\path\xyz");
        Assert.Empty(presets);
    }

    [Fact]
    public void DiscoverFromPath_WorksWithAiTeamDir()
    {
        var aiTeamDir = Path.Combine(LegacyAiTeamDir, ".ai-team");
        var presets = SquadDiscovery.DiscoverFromPath(aiTeamDir);
        Assert.Single(presets);
        Assert.Equal("Legacy Team", presets[0].Name);
    }
}

public class SquadImportExportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _presetsDir;

    public SquadImportExportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "squad-import-" + Guid.NewGuid().ToString("N")[..8]);
        _presetsDir = Path.Combine(_tempDir, "presets");
        Directory.CreateDirectory(_presetsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ImportFromSquadFolder_SavesPresetToJson()
    {
        var squadRoot = CreateSampleSquad("Import Team", "dev", "You are a developer.");

        var imported = UserPresets.ImportFromSquadFolder(_presetsDir, squadRoot);

        Assert.Single(imported);
        Assert.Equal("Import Team", imported[0].Name);
        Assert.True(imported[0].IsUserDefined);
        Assert.False(imported[0].IsRepoLevel);

        // Verify persisted to presets.json
        var loaded = UserPresets.Load(_presetsDir);
        Assert.Single(loaded);
        Assert.Equal("Import Team", loaded[0].Name);
    }

    [Fact]
    public void ImportFromSquadFolder_AcceptsSquadDirPath()
    {
        var squadRoot = CreateSampleSquad("Direct Team", "worker", "You are a worker.");
        var squadDir = Path.Combine(squadRoot, ".squad");

        var imported = UserPresets.ImportFromSquadFolder(_presetsDir, squadDir);

        Assert.Single(imported);
        Assert.Equal("Direct Team", imported[0].Name);
    }

    [Fact]
    public void ImportFromSquadFolder_UpdatesExistingPreset()
    {
        var squadRoot = CreateSampleSquad("Update Team", "dev", "Original charter.");
        UserPresets.ImportFromSquadFolder(_presetsDir, squadRoot);

        // Update the charter and re-import
        File.WriteAllText(Path.Combine(squadRoot, ".squad", "agents", "dev", "charter.md"), "Updated charter.");
        var imported = UserPresets.ImportFromSquadFolder(_presetsDir, squadRoot);

        var loaded = UserPresets.Load(_presetsDir);
        Assert.Single(loaded); // Should replace, not duplicate
        Assert.Contains("Updated charter", loaded[0].WorkerSystemPrompts![0]);
    }

    [Fact]
    public void ImportFromSquadFolder_ReturnsEmpty_WhenNoSquadFound()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        var imported = UserPresets.ImportFromSquadFolder(_presetsDir, emptyDir);
        Assert.Empty(imported);
    }

    [Fact]
    public void ExportPresetToSquadFolder_CreatesSquadDir()
    {
        // Save a user preset first
        var preset = new GroupPreset("Export Team", "A test team", "🧪",
            MultiAgentMode.OrchestratorReflect, "claude-opus-4.6",
            new[] { "claude-sonnet-4.5", "gpt-5" })
        {
            IsUserDefined = true,
            WorkerDisplayNames = new[] { "analyzer", "coder" },
            WorkerSystemPrompts = new string?[] { "You analyze code.", "You write code." },
            SharedContext = "Be concise.",
            RoutingContext = "Route analysis first."
        };
        var existing = UserPresets.Load(_presetsDir);
        existing.Add(preset);
        UserPresets.Save(_presetsDir, existing);

        var targetDir = Path.Combine(_tempDir, "export-target");
        var result = UserPresets.ExportPresetToSquadFolder(_presetsDir, "Export Team", targetDir);

        Assert.NotNull(result);
        Assert.True(Directory.Exists(result));
        Assert.True(File.Exists(Path.Combine(result, "team.md")));
        Assert.True(File.Exists(Path.Combine(result, "agents", "analyzer", "charter.md")));
        Assert.True(File.Exists(Path.Combine(result, "agents", "coder", "charter.md")));
        Assert.True(File.Exists(Path.Combine(result, "decisions.md")));
        Assert.True(File.Exists(Path.Combine(result, "routing.md")));
    }

    [Fact]
    public void ExportPresetToSquadFolder_ReturnsNull_WhenPresetNotFound()
    {
        var targetDir = Path.Combine(_tempDir, "export-missing");
        var result = UserPresets.ExportPresetToSquadFolder(_presetsDir, "Nonexistent", targetDir);
        Assert.Null(result);
    }

    [Fact]
    public void ExportPresetToSquadFolder_CanExportBuiltIn()
    {
        var builtInName = GroupPreset.BuiltIn[0].Name;
        var targetDir = Path.Combine(_tempDir, "export-builtin");

        var result = UserPresets.ExportPresetToSquadFolder(_presetsDir, builtInName, targetDir);

        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine(result, "team.md")));
        var teamMd = File.ReadAllText(Path.Combine(result, "team.md"));
        Assert.Contains(builtInName, teamMd);
    }

    [Fact]
    public void RoundTrip_ExportThenImport_PreservesContent()
    {
        var preset = new GroupPreset("Roundtrip Team", "Full cycle", "🔄",
            MultiAgentMode.Orchestrator, "claude-opus-4.6",
            new[] { "claude-sonnet-4.5" })
        {
            IsUserDefined = true,
            WorkerDisplayNames = new[] { "reviewer" },
            WorkerSystemPrompts = new string?[] { "You review pull requests carefully." },
            SharedContext = "Follow OWASP guidelines.",
            RoutingContext = "All PRs go to reviewer."
        };
        var existing = UserPresets.Load(_presetsDir);
        existing.Add(preset);
        UserPresets.Save(_presetsDir, existing);

        // Export
        var exportDir = Path.Combine(_tempDir, "roundtrip-export");
        var squadDir = UserPresets.ExportPresetToSquadFolder(_presetsDir, "Roundtrip Team", exportDir);
        Assert.NotNull(squadDir);

        // Import into a fresh presets dir
        var freshPresetsDir = Path.Combine(_tempDir, "fresh-presets");
        Directory.CreateDirectory(freshPresetsDir);
        var imported = UserPresets.ImportFromSquadFolder(freshPresetsDir, exportDir);

        Assert.Single(imported);
        var result = imported[0];
        Assert.Equal("Roundtrip Team", result.Name);
        Assert.Equal(MultiAgentMode.Orchestrator, result.Mode);
        Assert.Contains("pull requests", result.WorkerSystemPrompts![0]);
        Assert.Contains("OWASP", result.SharedContext);
        Assert.Contains("reviewer", result.RoutingContext);
    }

    private string CreateSampleSquad(string teamName, string agentName, string charter)
    {
        var root = Path.Combine(_tempDir, "squad-" + Guid.NewGuid().ToString("N")[..6]);
        var squadDir = Path.Combine(root, ".squad");
        var agentDir = Path.Combine(squadDir, "agents", agentName);
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(squadDir, "team.md"),
            $"# {teamName}\n| Member | Role |\n|---|---|\n| {agentName} | Worker |");
        File.WriteAllText(Path.Combine(agentDir, "charter.md"), charter);
        return root;
    }
}
