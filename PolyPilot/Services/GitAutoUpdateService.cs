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

            if (local == remote)
            {
                _status = $"Up to date ({local[..7]})";
                if (_lastLocalCommit != local)
                {
                    _lastLocalCommit = local;
                    NotifyChanged();
                }
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
                    // True divergence: both sides have unique commits. Try rebase for forks.
                    if (updateRemote == "upstream")
                    {
                        _status = $"Rebasing onto {updateRemote}/main...";
                        NotifyChanged();
                        try
                        {
                            await RunGit($"rebase {updateRemote}/main");
                            // Rebase succeeded — force-push to keep fork in sync
                            try
                            {
                                await RunGit("push origin main --force-with-lease --quiet");
                                _logger.LogInformation("Fork rebased and force-pushed to origin");
                            }
                            catch (Exception pushEx)
                            {
                                _logger.LogWarning(pushEx, "Rebase succeeded but force-push to origin failed");
                            }
                            // Continue to rebuild below
                            goto rebuild;
                        }
                        catch (Exception rebaseEx)
                        {
                            // Rebase had conflicts — attempt auto-resolution preferring upstream
                            _logger.LogWarning(rebaseEx, "Rebase onto upstream/main had conflicts — attempting auto-resolve");
                            if (await TryAutoResolveRebaseConflicts())
                            {
                                // Auto-resolve succeeded — force-push to keep fork in sync
                                try
                                {
                                    await RunGit("push origin main --force-with-lease --quiet");
                                    _logger.LogInformation("Fork rebased (auto-resolved) and force-pushed to origin");
                                }
                                catch (Exception pushEx)
                                {
                                    _logger.LogWarning(pushEx, "Rebase succeeded but force-push to origin failed");
                                }
                                goto rebuild;
                            }
                            // Auto-resolve failed — already aborted inside TryAutoResolveRebaseConflicts
                            _status = $"Rebase conflict with {updateRemote}/main — auto-resolve failed, skipping";
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

            _status = "Rebuilding & relaunching...";
            NotifyChanged();

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

    /// <summary>
    /// Attempts to auto-resolve rebase conflicts by preferring upstream's version.
    /// During a rebase, "ours" is the upstream base (the branch we're rebasing onto)
    /// and "theirs" is our local commit being replayed. To prefer upstream, we use --ours.
    /// Loops through each conflicted commit until the rebase completes or a non-resolvable
    /// error occurs (max 50 iterations as safety bound).
    /// </summary>
    private async Task<bool> TryAutoResolveRebaseConflicts()
    {
        const int maxSteps = 50;
        for (int step = 0; step < maxSteps; step++)
        {
            try
            {
                // Get list of conflicted files
                var status = await RunGit("diff --name-only --diff-filter=U", throwOnError: false);
                var conflicted = status.Trim()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .Where(f => !string.IsNullOrEmpty(f))
                    .ToArray();

                if (conflicted.Length == 0)
                {
                    // No conflicts — try to continue (may be an empty commit or rebase finished)
                    try
                    {
                        await RunGit("-c core.editor=true rebase --continue");
                    }
                    catch
                    {
                        // rebase --continue can fail if rebase already finished
                    }
                    if (!IsRebaseInProgress()) return true;
                    continue;
                }

                _status = $"Auto-resolving {conflicted.Length} conflict{(conflicted.Length == 1 ? "" : "s")} (step {step + 1})...";
                NotifyChanged();

                // Resolve each file preferring upstream (--ours during rebase = upstream base)
                foreach (var file in conflicted)
                {
                    _logger.LogInformation("Auto-resolving conflict in {File} (preferring upstream)", file);
                    await RunGit($"checkout --ours -- \"{file}\"");
                    await RunGit($"add -- \"{file}\"");
                }

                // Continue the rebase with the resolved files
                try
                {
                    await RunGit("-c core.editor=true rebase --continue");
                }
                catch
                {
                    // More conflicts on the next commit — loop will handle them
                    if (!IsRebaseInProgress()) return true;
                    continue;
                }

                if (!IsRebaseInProgress()) return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-resolve step {Step} failed — aborting rebase", step);
                try { await RunGit("rebase --abort"); } catch { }
                return false;
            }
        }

        // Exceeded max steps
        if (!IsRebaseInProgress()) return true;
        _logger.LogWarning("Auto-resolve exceeded {MaxSteps} steps — aborting rebase", maxSteps);
        try { await RunGit("rebase --abort"); } catch { }
        return false;
    }

    private bool IsRebaseInProgress()
    {
        if (_gitRoot == null) return false;
        return Directory.Exists(Path.Combine(_gitRoot, ".git", "rebase-merge"))
            || Directory.Exists(Path.Combine(_gitRoot, ".git", "rebase-apply"));
    }

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
