using System.Text.RegularExpressions;

namespace PolyPilot.Models;

/// <summary>
/// Discovers bradygaster/squad team definitions from .squad/ or .ai-team/ directories.
/// Parses team.md, agent charters, routing.md, and decisions.md into GroupPreset(s).
/// Read-only: never writes to the .squad/ directory.
/// </summary>
public static class SquadDiscovery
{
    private const int MaxCharterLength = 4000;
    private const int MaxDecisionsLength = 8000;

    /// <summary>Names of agents that are infrastructure, not workers.</summary>
    private static readonly HashSet<string> InfraAgents = new(StringComparer.OrdinalIgnoreCase)
    {
        "scribe", "_scribe", "coordinator", "_coordinator", "_alumni"
    };

    /// <summary>
    /// Discover Squad team definitions from a worktree root.
    /// Returns empty list if no .squad/ or .ai-team/ directory found.
    /// </summary>
    public static List<GroupPreset> Discover(string worktreeRoot)
    {
        try
        {
            var squadDir = FindSquadDirectory(worktreeRoot);
            if (squadDir == null) return new();
            return DiscoverFromSquadDir(squadDir);
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Discover a Squad team definition from a path that is either a .squad/ directory
    /// itself or a parent directory containing .squad/ or .ai-team/.
    /// </summary>
    public static List<GroupPreset> DiscoverFromPath(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return new();

            var dirName = Path.GetFileName(path);
            // If the path IS a .squad/ or .ai-team/ directory, use it directly
            if (dirName is ".squad" or ".ai-team")
                return DiscoverFromSquadDir(path);

            // Otherwise treat it as a parent directory
            return Discover(path);
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Core discovery logic from a known .squad/ directory path.
    /// </summary>
    internal static List<GroupPreset> DiscoverFromSquadDir(string squadDir)
    {
        var teamFile = Path.Combine(squadDir, "team.md");
        if (!File.Exists(teamFile)) return new();

        var teamContent = File.ReadAllText(teamFile);
        var agents = DiscoverAgents(squadDir);

        if (agents.Count == 0) return new();

        var teamName = ParseTeamName(teamContent) ?? "Squad Team";
        var mode = ParseMode(teamContent);
        var worktreeStrategy = ParseWorktreeStrategy(teamContent);
        var decisions = ReadOptionalFile(Path.Combine(squadDir, "decisions.md"), MaxDecisionsLength);
        var routing = ReadOptionalFile(Path.Combine(squadDir, "routing.md"), MaxDecisionsLength);

        var preset = BuildPreset(teamName, agents, decisions, routing, squadDir, mode, worktreeStrategy);
        return new List<GroupPreset> { preset };
    }

    /// <summary>
    /// Find .squad/ or .ai-team/ directory. Prefers .squad/ if both exist.
    /// </summary>
    internal static string? FindSquadDirectory(string worktreeRoot)
    {
        var squadPath = Path.Combine(worktreeRoot, ".squad");
        if (Directory.Exists(squadPath)) return squadPath;

        var aiTeamPath = Path.Combine(worktreeRoot, ".ai-team");
        if (Directory.Exists(aiTeamPath)) return aiTeamPath;

        return null;
    }

    /// <summary>
    /// Discover agents from the agents/ subdirectory.
    /// Each agent has a directory with charter.md inside.
    /// Skips infrastructure agents (scribe, coordinator, _alumni).
    /// </summary>
    internal static List<SquadAgent> DiscoverAgents(string squadDir)
    {
        var agentsDir = Path.Combine(squadDir, "agents");
        if (!Directory.Exists(agentsDir)) return new();

        var agents = new List<SquadAgent>();
        foreach (var dir in Directory.GetDirectories(agentsDir))
        {
            var name = Path.GetFileName(dir);
            if (InfraAgents.Contains(name)) continue;

            var charterPath = Path.Combine(dir, "charter.md");
            string? charter = null;
            if (File.Exists(charterPath))
            {
                charter = File.ReadAllText(charterPath);
                if (charter.Length > MaxCharterLength)
                    charter = charter[..MaxCharterLength];
            }

            agents.Add(new SquadAgent(name, charter));
        }

        return agents;
    }

    /// <summary>
    /// Parse team name from team.md content.
    /// Looks for: first H1 heading, or first line that looks like a title.
    /// </summary>
    internal static string? ParseTeamName(string teamContent)
    {
        foreach (var line in teamContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# "))
                return trimmed[2..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Parse mode from team.md content.
    /// Looks for a line like "mode: orchestrator" (case-insensitive).
    /// Supports: broadcast, sequential, orchestrator, orchestrator-reflect.
    /// Defaults to OrchestratorReflect if not specified.
    /// </summary>
    internal static MultiAgentMode ParseMode(string teamContent)
    {
        foreach (var line in teamContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["mode:".Length..].Trim().ToLowerInvariant();
                return value switch
                {
                    "broadcast" => MultiAgentMode.Broadcast,
                    "sequential" => MultiAgentMode.Sequential,
                    "orchestrator" => MultiAgentMode.Orchestrator,
                    "orchestrator-reflect" or "orchestratorreflect" or "reflect" => MultiAgentMode.OrchestratorReflect,
                    _ => MultiAgentMode.OrchestratorReflect
                };
            }
        }
        return MultiAgentMode.OrchestratorReflect;
    }

    /// <summary>
    /// Parse optional worktree strategy from team.md content.
    /// Looks for: "worktrees: isolated" (case-insensitive).
    /// Supports: shared, group-shared, orchestrator-isolated, isolated/fully-isolated, selective.
    /// Returns null if not specified (caller applies default).
    /// </summary>
    internal static WorktreeStrategy? ParseWorktreeStrategy(string teamContent)
    {
        foreach (var line in teamContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("worktrees:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["worktrees:".Length..].Trim().ToLowerInvariant();
                return value switch
                {
                    "shared" => WorktreeStrategy.Shared,
                    "group-shared" or "groupshared" => WorktreeStrategy.GroupShared,
                    "orchestrator-isolated" or "orchestratorisolated" => WorktreeStrategy.OrchestratorIsolated,
                    "isolated" or "fully-isolated" or "fullyisolated" => WorktreeStrategy.FullyIsolated,
                    "selective" or "selective-isolated" or "selectiveisolated" => WorktreeStrategy.SelectiveIsolated,
                    _ => null
                };
            }
        }
        return null;
    }

    /// <summary>
    /// Parse agent roster from team.md table rows.
    /// Returns member names from the first column of markdown tables.
    /// </summary>
    internal static List<string> ParseRosterNames(string teamContent)
    {
        var names = new List<string>();
        var tableRegex = new Regex(@"^\s*\|\s*([^\|\s]+)\s*\|", RegexOptions.Multiline);
        foreach (Match m in tableRegex.Matches(teamContent))
        {
            var name = m.Groups[1].Value.Trim();
            // Skip header row markers and header labels
            if (name == "---" || name.All(c => c == '-')
                || name.Equals("Member", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                continue;
            names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Parse per-agent model assignments from team.md table rows.
    /// Looks for a 3-column table: | Member | Role | Model |
    /// Returns a dictionary mapping agent name to model string.
    /// Returns empty dict if no Model column is present.
    /// </summary>
    internal static Dictionary<string, string> ParseRosterModels(string teamContent)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tableRegex = new Regex(@"^\s*\|\s*([^\|]+?)\s*\|\s*([^\|]+?)\s*\|\s*([^\|]+?)\s*\|", RegexOptions.Multiline);
        foreach (Match m in tableRegex.Matches(teamContent))
        {
            var name = m.Groups[1].Value.Trim();
            var model = m.Groups[3].Value.Trim();
            if (name == "---" || name.All(c => c == '-')
                || name.Equals("Member", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                || model == "---" || model.All(c => c == '-')
                || model.Equals("Model", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(model))
                models[name] = model;
        }
        return models;
    }

    private static string? ReadOptionalFile(string path, int maxLength)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var content = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(content)) return null;
            return content.Length > maxLength ? content[..maxLength] : content;
        }
        catch { return null; }
    }

    /// <summary>
    /// Determine whether an agent should get its own isolated worktree based on role.
    /// Roles that don't develop (reviewer, architect, debugger, firmware-dev) share orchestrator's worktree.
    /// </summary>
    internal static bool ShouldGetOwnWorktree(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return true;
        var lower = role.ToLowerInvariant();
        return !lower.Contains("reviewer") && !lower.Contains("architect") 
            && !lower.Contains("debugger") && !lower.Contains("firmware");
    }

    /// <summary>
    /// Parse per-agent roles from team.md table rows.
    /// Looks for a 3-column table: | Member | Role | Model |
    /// Returns a dictionary mapping agent name to role string.
    /// Returns empty dict if no Role column is present.
    /// </summary>
    internal static Dictionary<string, string> ParseRosterRoles(string teamContent)
    {
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tableRegex = new Regex(@"^\s*\|\s*([^\|]+?)\s*\|\s*([^\|]+?)\s*\|\s*([^\|]+?)\s*\|", RegexOptions.Multiline);
        foreach (Match m in tableRegex.Matches(teamContent))
        {
            var name = m.Groups[1].Value.Trim();
            var role = m.Groups[2].Value.Trim();
            if (name == "---" || name.All(c => c == '-')
                || string.Equals(name, "Member", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase)
                || role == "---" || role.All(c => c == '-')
                || string.Equals(role, "Role", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(role))
                roles[name] = role;
        }
        return roles;
    }

    private static GroupPreset BuildPreset(string teamName, List<SquadAgent> agents,
        string? decisions, string? routing, string squadDir, MultiAgentMode mode,
        WorktreeStrategy? worktreeStrategy)
    {
        var defaultModel = "claude-sonnet-4.6";
        var orchestratorModel = "claude-opus-4.6";

        // Check for per-agent model assignments and roles in team.md
        var teamFile = Path.Combine(squadDir, "team.md");
        var teamContent = File.Exists(teamFile) ? File.ReadAllText(teamFile) : "";
        var rosterModels = ParseRosterModels(teamContent);
        var rosterRoles = ParseRosterRoles(teamContent);

        var workerModels = agents.Select(a =>
            rosterModels.TryGetValue(a.Name, out var m) ? m : defaultModel).ToArray();
        var systemPrompts = agents.Select(a => a.Charter).ToArray();
        var displayNames = agents.Select(a => a.Name).ToArray();
        
        // Build WorkerUseWorktree array: non-developer roles share orchestrator's worktree
        var workerUseWorktree = agents.Select(a =>
        {
            if (rosterRoles.TryGetValue(a.Name, out var role))
                return ShouldGetOwnWorktree(role);
            return true; // Default: all workers get own worktree if no role specified
        }).ToArray();

        return new GroupPreset(
            teamName,
            $"Squad team from {Path.GetFileName(Path.GetDirectoryName(squadDir) ?? squadDir)}",
            "🫡",
            mode,
            orchestratorModel,
            workerModels)
        {
            IsRepoLevel = true,
            SourcePath = squadDir,
            WorkerSystemPrompts = systemPrompts,
            WorkerDisplayNames = displayNames,
            SharedContext = decisions,
            RoutingContext = routing,
            // Honor explicit team.md setting, otherwise default to FullyIsolated
            // so parallel workers don't cause git conflicts.
            DefaultWorktreeStrategy = worktreeStrategy ?? WorktreeStrategy.FullyIsolated,
            WorkerUseWorktree = workerUseWorktree,
        };
    }

    /// <summary>Represents a discovered Squad agent with name and charter content.</summary>
    internal record SquadAgent(string Name, string? Charter);
}
