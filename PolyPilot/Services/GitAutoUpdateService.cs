using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PolyPilot.Models;

namespace PolyPilot.Services;

public class GitAutoUpdateService : IDisposable
{
    private Timer? _timer;
    private string? _projectDir;
    private string? _gitRoot;
    private bool _isAvailable;
    private int _checking;
    private string _status = "";
    private string? _lastLocalCommit;
    private readonly ILogger<GitAutoUpdateService> _logger;
    private readonly SynchronizationContext? _syncCtx;

    public bool IsAvailable => _isAvailable;
    public bool IsEnabled { get; private set; }
    public string Status => _status;
    public string? Branch { get; private set; }

    public event Action? OnStateChanged;
    /// <summary>Fired synchronously before a relaunch so subscribers can persist transient state (e.g. draft text).</summary>
    public event Action? OnBeforeRelaunch;

    public GitAutoUpdateService(ILogger<GitAutoUpdateService> logger)
    {
        _logger = logger;
        _syncCtx = SynchronizationContext.Current;
        DetectSourceEnvironment();
    }

    public void Initialize()
    {
        if (!_isAvailable) return;
        var settings = ConnectionSettings.Load();
        if (settings.AutoUpdateFromMain)
            Start();
    }

    private void DetectSourceEnvironment()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 20 && dir != null; i++)
            {
                dir = Path.GetDirectoryName(dir);
                if (dir == null) break;
                if (File.Exists(Path.Combine(dir, "PolyPilot.csproj")) &&
                    (File.Exists(Path.Combine(dir, "relaunch.sh")) ||
                     File.Exists(Path.Combine(dir, "relaunch.ps1"))))
                {
                    _projectDir = dir;
                    var gitRoot = Path.GetDirectoryName(dir);
                    if (gitRoot != null && Directory.Exists(Path.Combine(gitRoot, ".git")))
                    {
                        _gitRoot = gitRoot;
                        _isAvailable = true;
                        _logger.LogInformation("Auto-update available: project={Project}, git={Git}", _projectDir, _gitRoot);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect source environment");
        }
    }

    public void Start()
    {
        if (!_isAvailable || IsEnabled) return;
        IsEnabled = true;
        _status = "Watching main...";
        _timer = new Timer(CheckForUpdates, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        _logger.LogInformation("Auto-update watcher started");
        NotifyChanged();
    }

    public void Stop()
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        _timer?.Dispose();
        _timer = null;
        _status = "Stopped";
        _logger.LogInformation("Auto-update watcher stopped");
        NotifyChanged();
    }

    private async void CheckForUpdates(object? state)
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        try
        {
            var branch = (await RunGit("rev-parse --abbrev-ref HEAD")).Trim();
            Branch = branch;
            if (branch != "main")
            {
                _status = $"Not on main (on {branch})";
                NotifyChanged();
                return;
            }

            // Check for uncommitted changes
            var dirtyCheck = (await RunGit("status --porcelain")).Trim();
            if (!string.IsNullOrEmpty(dirtyCheck))
            {
                _status = "Working tree dirty — skipping";
                NotifyChanged();
                return;
            }

            // Determine which remote to track. If an 'upstream' remote exists,
            // this is a fork — fetch and compare against upstream directly.
            // Otherwise use origin (standard non-fork setup).
            string updateRemote;
            try
            {
                var remotes = (await RunGit("remote")).Trim();
                var hasUpstream = remotes
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(r => r.Trim().Equals("upstream", StringComparison.OrdinalIgnoreCase));
                updateRemote = hasUpstream ? "upstream" : "origin";
            }
            catch
            {
                updateRemote = "origin";
            }

            await RunGit($"fetch {updateRemote} main --quiet");

            var local = (await RunGit("rev-parse HEAD")).Trim();
            var remote = (await RunGit($"rev-parse {updateRemote}/main")).Trim();

            // Always record the current commit as the pre-update baseline.
            // If the update fails to build, we revert to this commit.
            _lastLocalCommit = local;

            if (local == remote)
            {
                _status = $"Up to date ({local[..7]})";
                NotifyChanged();
                return;
            }

            // Only update if there are new upstream commits to incorporate.
            // For forks: local may have commits beyond upstream — that's fine as long as
            // upstream/main is an ancestor of HEAD (we already have all upstream changes).
            var isLocalBehind = false;
            try
            {
                await RunGit($"merge-base --is-ancestor HEAD {updateRemote}/main");
                // HEAD is an ancestor of remote — we're strictly behind, fast-forward is possible
                isLocalBehind = true;
            }
            catch
            {
                // HEAD is NOT an ancestor of remote — check the reverse: is remote an ancestor of us?
                try
                {
                    await RunGit($"merge-base --is-ancestor {updateRemote}/main HEAD");
                    // Remote is already contained in our history — we're ahead, nothing to pull
                    _status = $"Up to date (ahead of {updateRemote}/main)";
                    NotifyChanged();
                    return;
                }
                catch
                {
                    // True divergence: both sides have unique commits. Merge (not rebase) so the
                    // result is a reversible merge commit — bad merges can be reverted with
                    // git revert -m 1 <merge-commit> without rewriting history.
                    if (updateRemote == "upstream")
                    {
                        _status = $"Merging {updateRemote}/main...";
                        NotifyChanged();
                        try
                        {
                            await RunGit($"merge {updateRemote}/main --no-edit");
                            // Push to keep fork in sync — no force needed (merge preserves history)
                            try
                            {
                                await RunGit("push origin main --quiet");
                                _logger.LogInformation("Fork merged and pushed to origin");
                            }
                            catch (Exception pushEx)
                            {
                                _logger.LogWarning(pushEx, "Merge succeeded but push to origin failed");
                            }
                            goto rebuild;
                        }
                        catch (Exception mergeEx)
                        {
                            _logger.LogWarning(mergeEx, "Merge of {Remote}/main had conflicts — attempting auto-resolution", updateRemote);
                            _status = $"Resolving merge conflicts with {updateRemote}/main...";
                            NotifyChanged();

                            if (await TryResolveConflictsAsync(updateRemote))
                            {
                                try
                                {
                                    await RunGit("push origin main --quiet");
                                    _logger.LogInformation("Fork merged (with conflict resolution) and pushed to origin");
                                }
                                catch (Exception pushEx)
                                {
                                    _logger.LogWarning(pushEx, "Merge succeeded but push to origin failed");
                                }
                                goto rebuild;
                            }

                            try { await RunGit("merge --abort"); } catch { }
                            _status = $"Merge conflict with {updateRemote}/main — auto-resolve failed, resolve manually and restart";
                            NotifyChanged();
                            return;
                        }
                    }

                    _status = $"Diverged from {updateRemote}/main — skipping";
                    NotifyChanged();
                    return;
                }
            }

            var behindCount = (await RunGit($"rev-list --count HEAD..{updateRemote}/main")).Trim();
            _status = $"Updating ({behindCount} new commit{(behindCount == "1" ? "" : "s")})...";
            NotifyChanged();

            var pullResult = await RunGit($"pull {updateRemote} main --ff-only");
            _logger.LogInformation("Pulled updates: {Result}", pullResult.Trim());

            // If pulling from upstream on a fork, also push to origin to keep fork in sync
            if (updateRemote == "upstream")
            {
                try
                {
                    await RunGit("push origin main --quiet");
                    _logger.LogDebug("Fork synced: pushed to origin after upstream pull");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to push to fork origin — fork may be out of sync");
                }
            }

            rebuild:

            // Verify the build succeeds BEFORE relaunching. If it fails, revert to
            // the pre-update commit so the next relaunch still works.
            var preUpdateCommit = _lastLocalCommit;
            var currentCommit = (await RunGit("rev-parse HEAD")).Trim();

            _status = "Building to verify update...";
            NotifyChanged();

            var buildOk = await TryBuild();
            if (!buildOk)
            {
                _logger.LogError("Build failed after update — reverting to {Commit}", preUpdateCommit ?? currentCommit);
                _status = "Build failed — reverting...";
                NotifyChanged();

                try
                {
                    if (!string.IsNullOrEmpty(preUpdateCommit))
                    {
                        await RunGit($"reset --hard {preUpdateCommit}");
                        _logger.LogInformation("Reverted to pre-update commit {Commit}", preUpdateCommit);
                    }
                    else
                    {
                        // No known good commit — reset to the previous HEAD
                        await RunGit("reset --hard HEAD@{1}");
                        _logger.LogInformation("Reverted to previous HEAD");
                    }
                }
                catch (Exception revertEx)
                {
                    _logger.LogError(revertEx, "Failed to revert after build failure");
                }

                _status = "Update reverted (build failed)";
                NotifyChanged();
                return;
            }

            _status = "Build verified — relaunching...";
            NotifyChanged();

            // Let subscribers save transient state (e.g. draft messages) before the app restarts
            try { OnBeforeRelaunch?.Invoke(); }
            catch (Exception ex2) { _logger.LogWarning(ex2, "OnBeforeRelaunch handler failed"); }

            await Relaunch();
        }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            _logger.LogError(ex, "Auto-update check failed");
            NotifyChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private async Task<bool> TryResolveConflictsAsync(string updateRemote)
    {
        try
        {
            var conflictOutput = (await RunGit("diff --name-only --diff-filter=U", throwOnError: false)).Trim();
            if (string.IsNullOrEmpty(conflictOutput))
            {
                _logger.LogWarning("Merge reported conflicts but no unmerged files found");
                return false;
            }

            var conflictedFiles = conflictOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _logger.LogInformation("Auto-resolving {Count} conflicted file(s): {Files}", conflictedFiles.Length, string.Join(", ", conflictedFiles));

            foreach (var file in conflictedFiles)
            {
                _logger.LogInformation("Resolving conflict in {File}", file);

                var localCommits = (await RunGit($"log --oneline {updateRemote}/main..HEAD -- \"{file}\"", throwOnError: false)).Trim();
                if (string.IsNullOrEmpty(localCommits))
                {
                    _logger.LogInformation("  No local commits touched {File} — accepting theirs", file);
                    await RunGit($"checkout --theirs \"{file}\"");
                    await RunGit($"add \"{file}\"");
                    continue;
                }

                _logger.LogInformation("  {File} has local modifications — resolving hunks", file);
                if (!ResolveConflictedFile(file))
                {
                    _logger.LogWarning("  Failed to auto-resolve {File}", file);
                    return false;
                }

                await RunGit($"add \"{file}\"");
            }

            await RunGit("commit --no-edit");
            _logger.LogInformation("Merge commit created after auto-resolving all conflicts");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-conflict resolution failed");
            return false;
        }
    }

    private bool ResolveConflictedFile(string relativeFilePath)
    {
        var fullPath = Path.Combine(_gitRoot!, relativeFilePath);
        try
        {
            var content = File.ReadAllText(fullPath);
            var resolved = ResolveConflictMarkers(content);
            if (resolved is null)
                return false;

            File.WriteAllText(fullPath, resolved);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading/writing conflicted file {File}", relativeFilePath);
            return false;
        }
    }

    private string? ResolveConflictMarkers(string content)
    {
        var result = new System.Text.StringBuilder(content.Length);
        var remaining = content.AsSpan();

        while (true)
        {
            var markerStart = remaining.IndexOf("<<<<<<< ");
            if (markerStart < 0)
            {
                result.Append(remaining);
                break;
            }

            result.Append(remaining[..markerStart]);
            remaining = remaining[markerStart..];

            var separatorIdx = remaining.IndexOf("\n=======\n");
            if (separatorIdx < 0)
            {
                _logger.LogWarning("Could not find ======= separator in conflict hunk");
                return null;
            }

            var endMarkerPrefix = "\n>>>>>>> ";
            var endIdx = remaining[(separatorIdx + 1)..].IndexOf(endMarkerPrefix);
            if (endIdx < 0)
            {
                _logger.LogWarning("Could not find >>>>>>> end marker in conflict hunk");
                return null;
            }
            endIdx += separatorIdx + 1;

            var endLineBreak = remaining[(endIdx + endMarkerPrefix.Length)..].IndexOf('\n');
            int hunkEnd;
            if (endLineBreak < 0)
                hunkEnd = remaining.Length;
            else
                hunkEnd = endIdx + endMarkerPrefix.Length + endLineBreak + 1;

            var firstNewline = remaining.IndexOf('\n');
            var oursBlock = remaining[(firstNewline + 1)..separatorIdx].ToString();
            var theirsBlock = remaining[(separatorIdx + "\n=======\n".Length)..endIdx].ToString();

            var oursNorm = NormalizeWhitespace(oursBlock);
            var theirsNorm = NormalizeWhitespace(theirsBlock);

            string resolved;
            if (oursNorm == theirsNorm)
            {
                _logger.LogDebug("  Hunk: identical (whitespace-normalized) — keeping theirs");
                resolved = theirsBlock;
            }
            else if (theirsNorm.Contains(oursNorm, StringComparison.Ordinal) && oursNorm.Length > 0)
            {
                _logger.LogDebug("  Hunk: ours is subset of theirs — keeping theirs");
                resolved = theirsBlock;
            }
            else if (oursNorm.Contains(theirsNorm, StringComparison.Ordinal) && theirsNorm.Length > 0)
            {
                _logger.LogDebug("  Hunk: theirs is subset of ours — keeping ours");
                resolved = oursBlock;
            }
            else
            {
                _logger.LogDebug("  Hunk: true divergence — combining theirs then ours");
                resolved = theirsBlock + "\n\n" + oursBlock;
            }

            result.Append(resolved);
            remaining = remaining[hunkEnd..];
        }

        return result.ToString();
    }

    private static string NormalizeWhitespace(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");

    private async Task<string> RunGit(string args, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _gitRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        SetPath(psi);

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && throwOnError)
            throw new InvalidOperationException($"git {args} failed: {error}");

        return output;
    }

    private Task Relaunch()
    {
        ProcessStartInfo psi;

        if (OperatingSystem.IsWindows())
        {
            // Windows: use PowerShell script or dotnet build + restart
            var ps1 = Path.Combine(_projectDir!, "relaunch.ps1");
            if (File.Exists(ps1))
            {
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -File \"{ps1}\"",
                    WorkingDirectory = _projectDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }
            else
            {
                // Fallback: rebuild and restart via dotnet run
                psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "build -f net10.0-windows10.0.19041.0",
                    WorkingDirectory = _projectDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
            }
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "relaunch.sh",
                WorkingDirectory = _projectDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        SetPath(psi);
        Process.Start(psi);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs a build-only pass (no launch) to verify the current code compiles.
    /// Returns true if the build succeeds, false otherwise.
    /// </summary>
    private async Task<bool> TryBuild()
    {
        try
        {
            string framework;
            if (OperatingSystem.IsWindows())
                framework = "net10.0-windows10.0.19041.0";
            else if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
                framework = "net10.0-maccatalyst";
            else
                framework = "net10.0-android";

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build PolyPilot.csproj -f {framework} --no-restore",
                WorkingDirectory = _projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            SetPath(psi);

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogError("Verification build failed (exit {Code}):\n{Error}", process.ExitCode, error);
                return false;
            }

            _logger.LogInformation("Verification build succeeded");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Verification build threw exception");
            return false;
        }
    }

    private static void SetPath(ProcessStartInfo psi)
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows, inherit the existing PATH — just ensure dotnet is available
            var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var dotnetPath = Path.Combine(programFiles, "dotnet");
            if (!existing.Contains(dotnetPath, StringComparison.OrdinalIgnoreCase))
                psi.Environment["PATH"] = $"{dotnetPath};{existing}";
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.Environment["PATH"] =
                $"{home}/.dotnet:/usr/local/share/dotnet:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin";
            psi.Environment["HOME"] = home;
        }
    }

    private void NotifyChanged()
    {
        if (_syncCtx != null)
            _syncCtx.Post(_ => OnStateChanged?.Invoke(), null);
        else
            OnStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
