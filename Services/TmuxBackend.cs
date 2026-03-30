using System.Diagnostics;
using CodeCommandCenter.Models;
using Spectre.Console;

namespace CodeCommandCenter.Services;

public class TmuxBackend : ISessionBackend
{
    private const string PoolSession = "ccc-pool";
    private const string ManagerSession = "ccc-manager";
    private const string ManagerNavPane = "ccc-manager:0.0";  // left pane
    private const string ManagerSessionPane = "ccc-manager:0.1";  // right pane

    public List<Session> ListSessions()
    {
        var output = RunTmux("list-sessions", "-F", "#{session_name}\t#{session_created}\t#{session_attached}\t#{session_windows}\t#{pane_current_path}\t#{pane_dead}");
        if (output == null)
            return [];

        var sessions = new List<Session>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;

            var session = new Session
            {
                Name = parts[0],
                IsAttached = parts[2] != "0",
                WindowCount = int.TryParse(parts[3], out var wc) ? wc : 0,
                CurrentPath = parts.Length > 4 ? parts[4] : null,
                IsDead = parts.Length > 5 && parts[5] == "1",
            };

            if (long.TryParse(parts[1], out var epoch))
                session.Created = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;

            sessions.Add(session);
        }

        foreach (var session in sessions)
            GitService.DetectGitInfo(session);

        // Filter out internal CCC sessions
        sessions.RemoveAll(s => s.Name is "ccc-pool" or "ccc-manager" or "ccc-grid");

        // Add pool sessions (pool takes precedence over standalone with same name)
        var poolSessions = ListPoolSessions();
        var poolNames = new HashSet<string>(poolSessions.Select(s => s.Name));
        sessions.RemoveAll(s => poolNames.Contains(s.Name));
        sessions.AddRange(poolSessions);

        // Re-sort
        sessions.Sort((a, b) =>
        {
            var created = Nullable.Compare(a.Created, b.Created);
            return created != 0 ? created : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return sessions;
    }

    private List<Session> ListPoolSessions()
    {
        var sessions = new List<Session>();

        var (poolExists, _) = RunTmuxWithError("has-session", "-t", $"={PoolSession}");
        if (!poolExists) return sessions;

        var output = RunTmux("list-windows", "-t", PoolSession,
            "-F", "#{window_name}\t#{window_activity}\t#{pane_current_path}\t#{pane_dead}");

        if (output == null) return sessions;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4) continue;
            if (parts[0] == "placeholder") continue;

            var name = parts[0];
            var activityEpoch = long.TryParse(parts[1], out var epoch) ? epoch : 0;
            var currentPath = parts[2];
            var isDead = parts[3] == "1";

            var session = new Session
            {
                Name = name,
                CurrentPath = currentPath,
                IsDead = isDead,
                Created = DateTimeOffset.FromUnixTimeSeconds(activityEpoch).DateTime,
                IsAttached = false,
                WindowCount = 1,
                IsPoolSession = true,
            };

            GitService.DetectGitInfo(session);
            sessions.Add(session);
        }

        return sessions;
    }

    public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null, bool dangerouslySkipPermissions = false, string? initialPrompt = null, bool shellOnly = false)
    {
        var envArgs = new List<string> { "-e", $"CCC_SESSION_NAME={name}" };
        if (!string.IsNullOrEmpty(claudeConfigDir))
        {
            envArgs.Add("-e");
            envArgs.Add($"CLAUDE_CONFIG_DIR={claudeConfigDir}");
        }

        var (cmdFile, cmdArgs) = SshService.BuildSessionCommand(remoteHost, workingDirectory, dangerouslySkipPermissions, initialPrompt, shellOnly);
        // Shell-quote any arg containing spaces or & so that tmux's command string re-parsing
        // keeps multi-word args (e.g. "claude --dangerously-skip-permissions") as a single token.
        var quotedArgs = cmdArgs.ConvertAll(a => a.Contains(' ') || a.Contains('&') ? $"\"{a}\"" : a);
        var shellCommand = $"{cmdFile} {string.Join(" ", quotedArgs)}";

        var args = new List<string> { "new-session", "-d", "-s", name, "-n", name };
        args.AddRange(envArgs);

        // For remote sessions, tmux working dir is irrelevant (cd happens on remote),
        // so use $HOME as a sane fallback
        var tmuxWorkDir = remoteHost != null
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : workingDirectory;

        args.AddRange(["-c", tmuxWorkDir, shellCommand]);

        var (success, error) = RunTmuxWithError(args.ToArray());
        if (!success)
            return error ?? "Failed to create tmux session";
        RunTmux("set-option", "-t", name, "automatic-rename", "off");
        return null;
    }

    public string? KillSession(string name)
    {
        // Try killing as a pool window first
        var (poolSuccess, _) = RunTmuxWithError("kill-window", "-t", $"{PoolSession}:{name}");
        if (poolSuccess) return null;

        // Fall back to killing as a standalone session
        var (success, error) = RunTmuxWithError("kill-session", "-t", name);
        return success ? null : error ?? "Failed to kill session";
    }

    public string? RenameSession(string oldName, string newName)
    {
        // Try renaming as a pool window first
        var (poolSuccess, _) = RunTmuxWithError("rename-window", "-t", $"{PoolSession}:{oldName}", newName);
        if (poolSuccess) return null;

        // Fall back to renaming as a standalone session
        var (success, error) = RunTmuxWithError("rename-session", "-t", oldName, newName);
        return success ? null : error ?? "Failed to rename session";
    }

    public void AttachSession(string name)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tmux",
            ArgumentList = { "attach-session", "-t", name },
            UseShellExecute = false,
        };

        try
        {
            var process = Process.Start(startInfo);
            process?.WaitForExit();
        }
        catch
        {
            // attach failed silently
        }
    }

    public void DetachSession()
    {
        // No-op — tmux handles detach via Ctrl-b d
    }

    public string? SendKeys(string sessionName, string text)
    {
        var target = ResolveTarget(sessionName);
        var (success, error) = RunTmuxWithError("send-keys", "-t", target, "-l", text);
        if (!success)
            return error ?? "Failed to send keys";

        RunTmux("send-keys", "-t", target, "Enter");
        return null;
    }

    public void ForwardKey(string sessionName, ConsoleKeyInfo key)
    {
        var target = ResolveTarget(sessionName);

        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key >= ConsoleKey.A && key.Key <= ConsoleKey.Z)
        {
            RunTmux("send-keys", "-t", target, $"C-{(char)('a' + key.Key - ConsoleKey.A)}");
            return;
        }

        var tmuxKey = key.Key switch
        {
            ConsoleKey.Enter => "Enter",
            ConsoleKey.Backspace => "BSpace",
            ConsoleKey.Delete => "DC",
            ConsoleKey.Tab => "Tab",
            ConsoleKey.Escape => "Escape",
            ConsoleKey.UpArrow => "Up",
            ConsoleKey.DownArrow => "Down",
            ConsoleKey.LeftArrow => "Left",
            ConsoleKey.RightArrow => "Right",
            ConsoleKey.Home => "Home",
            ConsoleKey.End => "End",
            ConsoleKey.PageUp => "PPage",
            ConsoleKey.PageDown => "NPage",
            ConsoleKey.Insert => "IC",
            ConsoleKey.F1 => "F1",
            ConsoleKey.F2 => "F2",
            ConsoleKey.F3 => "F3",
            ConsoleKey.F4 => "F4",
            ConsoleKey.F5 => "F5",
            ConsoleKey.F6 => "F6",
            ConsoleKey.F7 => "F7",
            ConsoleKey.F8 => "F8",
            ConsoleKey.F9 => "F9",
            ConsoleKey.F10 => "F10",
            ConsoleKey.F11 => "F11",
            ConsoleKey.F12 => "F12",
            _ => null,
        };

        if (tmuxKey != null)
        {
            RunTmux("send-keys", "-t", target, tmuxKey);
            return;
        }

        if (key.KeyChar != '\0')
            RunTmux("send-keys", "-t", target, "-l", key.KeyChar.ToString());
    }

    public void ForwardLiteralBatch(string sessionName, string text)
    {
        if (text.Length > 0)
            RunTmux("send-keys", "-t", ResolveTarget(sessionName), "-l", text);
    }

    public string? CapturePaneContent(string sessionName, int lines = 500)
    {
        // Try pool target first, then standalone
        var result = RunTmux("capture-pane", "-t", $"{PoolSession}:{sessionName}", "-p", "-e", "-S", $"-{lines}");
        return result ?? RunTmux("capture-pane", "-t", sessionName, "-p", "-e", "-S", $"-{lines}");
    }

    public void ResizeWindow(string sessionName, int width, int height)
    {
        var (poolSuccess, _) = RunTmuxWithError("resize-window", "-t", $"{PoolSession}:{sessionName}", "-x", width.ToString(), "-y", height.ToString());
        if (!poolSuccess)
            RunTmux("resize-window", "-t", sessionName, "-x", width.ToString(), "-y", height.ToString());
    }

    public void ResetWindowSize(string sessionName)
    {
        var (poolSuccess, _) = RunTmuxWithError("set-option", "-u", "-t", $"{PoolSession}:{sessionName}", "window-size");
        if (!poolSuccess)
            RunTmux("set-option", "-u", "-t", sessionName, "window-size");
    }

    public void ApplyStatusColor(string sessionName, string? spectreColor)
    {
        if (string.IsNullOrWhiteSpace(spectreColor))
            return;

        try
        {
            var color = Style.Parse(spectreColor).Foreground;
            var hex = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
            var target = ResolveTarget(sessionName);
            RunTmux("set-option", "-t", target, "status-style", $"bg={hex},fg=white");
        }
        catch
        {
            // Invalid color name — skip silently
        }
    }

    public void DetectWaitingForInputBatch(List<Session> sessions)
    {
        if (sessions.Count == 0)
            return;

        foreach (var session in sessions)
        {
            if (session.IsDead)
            {
                session.IsWaitingForInput = false;
                session.IsIdle = false;
                continue;
            }

            var hookState = HookStateService.ReadState(session.Name);
            if (hookState != null)
            {
                session.IsWaitingForInput = hookState == "waiting";
                session.IsIdle = hookState == "idle";
                continue;
            }

            // No hook state — fall back to pane content stability detection
            DetectWaitingByPaneContent(session);
        }
    }

    public bool IsAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tmux",
                Arguments = "-V",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsInsideHost() => Environment.GetEnvironmentVariable("TMUX") != null;

    public bool HasClaude() => SessionContentAnalyzer.CheckClaudeAvailable();

    public string? CreateGridSession(List<string> sessionNames)
    {
        // Clean up any leftover grid session from a previous crash
        if (GridSessionExists())
            RestoreFromGrid();

        // Create the grid session with a throwaway shell
        var (createOk, createErr) = RunTmuxWithError("new-session", "-d", "-s", "ccc-grid");
        if (!createOk)
            return createErr ?? "Failed to create grid session";

        // Record the initial empty pane's ID so we can kill it after joining session panes
        var initialPaneId = RunTmux("display-message", "-t", "ccc-grid:0", "-p", "#{pane_id}");

        // Move each session's pane into the grid window.
        // Track each pane's ID immediately after joining to build a reliable
        // paneId→sessionName mapping (independent of list-panes ordering).
        // join-pane moves the pane, leaving the source session empty (tmux auto-kills it).
        var paneMapping = new List<string>();
        foreach (var name in sessionNames)
        {
            // Capture the source pane's ID before joining (it keeps its ID after the move)
            var sourcePaneId = RunTmux("display-message", "-t", $"{name}:0.0", "-p", "#{pane_id}");
            RunTmux("join-pane", "-d", "-s", $"{name}:0.0", "-t", "ccc-grid:0");
            if (sourcePaneId != null)
                paneMapping.Add($"{sourcePaneId}={name}");
        }

        // Kill the initial empty pane that was created with new-session
        if (initialPaneId != null)
            RunTmux("kill-pane", "-t", initialPaneId);

        // Store the pane ID → session name manifest for crash recovery.
        // Format: "%5=session1,%3=session2" — order-independent.
        RunTmux("set-environment", "-t", "ccc-grid", "CCC_GRID_SESSIONS",
            string.Join(",", paneMapping));

        // Apply layout: side-by-side for 2 panes, tiled grid for 3+
        var layout = sessionNames.Count <= 3 ? "even-horizontal" : "tiled";
        RunTmux("select-layout", "-t", "ccc-grid:0", layout);

        // Bind Ctrl+G to detach from the grid session
        RunTmux("bind-key", "-T", "root", "C-g", "detach-client");

        return null;
    }

    public void RestoreFromGrid()
    {
        if (!GridSessionExists())
            return;

        var manifest = GetGridSessionManifest();

        // List remaining panes in the grid window
        var paneOutput = RunTmux("list-panes", "-t", "ccc-grid:0", "-F", "#{pane_id}\t#{pane_dead}");

        if (paneOutput != null && manifest != null)
        {
            var paneLines = paneOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in paneLines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2)
                    continue;

                var paneId = parts[0];
                var isDead = parts[1] == "1";

                if (isDead)
                    continue;

                // Look up the original session name by pane ID (order-independent)
                if (!manifest.TryGetValue(paneId, out var originalSession))
                    continue;

                // The original session was destroyed when join-pane moved its only pane out.
                // Reverse the process: create a new empty session, move the pane into it,
                // then kill the placeholder pane.
                RunTmux("new-session", "-d", "-s", originalSession);
                var placeholderPaneId = RunTmux("display-message", "-t", $"{originalSession}:0", "-p", "#{pane_id}");
                RunTmux("join-pane", "-d", "-s", paneId, "-t", $"{originalSession}:0");
                if (placeholderPaneId != null)
                    RunTmux("kill-pane", "-t", placeholderPaneId);

                // Reset window size so it adapts to the next client that attaches,
                // rather than staying stuck at the grid cell dimensions
                ResetWindowSize(originalSession);
            }
        }

        // Unbind Ctrl+G — bind-key is server-wide, not session-scoped
        RunTmux("unbind-key", "-T", "root", "C-g");

        // Kill the grid session (cleans up dead panes)
        RunTmux("kill-session", "-t", "=ccc-grid");
    }

    public bool GridSessionExists()
    {
        // Use '=' prefix for exact match — without it, tmux prefix-matches
        // and a session named "ccc-grid-something" would falsely match
        var result = RunTmux("has-session", "-t", "=ccc-grid");
        return result != null;
    }

    public Dictionary<string, string>? GetGridSessionManifest()
    {
        var output = RunTmux("show-environment", "-t", "ccc-grid", "CCC_GRID_SESSIONS");
        if (output == null)
            return null;

        // Output format: "CCC_GRID_SESSIONS=%5=session1,%3=session2"
        var eqIdx = output.IndexOf('=');
        if (eqIdx < 0)
            return null;

        var value = output[(eqIdx + 1)..];
        var result = new Dictionary<string, string>();
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = entry.IndexOf('=');
            if (sep > 0)
                result[entry[..sep]] = entry[(sep + 1)..];
        }

        return result;
    }

    public async Task SetupPool()
    {
        var (exists, _) = RunTmuxWithError("has-session", "-t", $"={PoolSession}");
        if (exists) return;
        RunTmux("new-session", "-d", "-s", PoolSession, "-n", "placeholder");
        await Task.CompletedTask;
    }

    public async Task MigrateStandaloneSessionsToPool(List<Session> standaloneSessions)
    {
        await SetupPool();

        foreach (var session in standaloneSessions)
        {
            if (session.IsPoolSession) continue;
            if (session.Name is "ccc-pool" or "ccc-manager" or "ccc-grid") continue;
            if (session.RemoteHostName != null) continue;

            // Move the standalone session's window into the pool
            var (success, _) = RunTmuxWithError("move-window",
                "-s", $"{session.Name}:0",
                "-t", $"{PoolSession}:");

            if (success)
            {
                // Rename the window to match the session name
                RunTmux("rename-window", "-t", $"{PoolSession}:{{last}}", session.Name);
                // Kill the now-empty standalone session
                RunTmux("kill-session", "-t", $"={session.Name}");
            }
        }

        // Clean up placeholder if real sessions were imported
        RunTmux("kill-window", "-t", $"{PoolSession}:placeholder");
    }

    public async Task CreateSessionInPool(string name, string dir, string? claudeConfigDir = null,
        bool dangerouslySkipPermissions = false, string? initialPrompt = null,
        bool shellOnly = false)
    {
        var (cmdFile, cmdArgs) = SshService.BuildSessionCommand(null, dir, dangerouslySkipPermissions, initialPrompt, shellOnly);
        var quotedArgs = cmdArgs.ConvertAll(a => a.Contains(' ') || a.Contains('&') ? $"\"{a}\"" : a);
        var shellCommand = $"{cmdFile} {string.Join(" ", quotedArgs)}";

        var args = new List<string> { "new-window", "-t", PoolSession, "-n", name };

        args.AddRange(["-e", $"CCC_SESSION_NAME={name}"]);
        if (!string.IsNullOrEmpty(claudeConfigDir))
            args.AddRange(["-e", $"CLAUDE_CONFIG_DIR={claudeConfigDir}"]);

        args.AddRange(["-c", dir, shellCommand]);

        RunTmux(args.ToArray());
        RunTmux("set-option", "-t", $"{PoolSession}:{name}", "automatic-rename", "off");

        // Remove the placeholder window if it still exists (first session created)
        RunTmux("kill-window", "-t", $"{PoolSession}:placeholder");

        await Task.CompletedTask;
    }

    public bool IsInsideManager()
    {
        var tmuxEnv = Environment.GetEnvironmentVariable("TMUX");
        if (string.IsNullOrEmpty(tmuxEnv)) return false;
        var sessionName = RunTmux("display-message", "-p", "#{session_name}");
        return sessionName == ManagerSession;
    }

    public bool ManagerSessionExists()
    {
        var (exists, _) = RunTmuxWithError("has-session", "-t", $"={ManagerSession}");
        return exists;
    }

    public async Task SetupManagerSession(string executablePath, string focusKeybinding = "C-Space",
        bool mouseEnabled = true)
    {
        var (exists, _) = RunTmuxWithError("has-session", "-t", $"={ManagerSession}");
        if (exists) return;

        RunTmux("new-session", "-d", "-s", ManagerSession, "-n", "main",
            "-x", Console.WindowWidth.ToString(), "-y", Console.WindowHeight.ToString(),
            executablePath);

        RunTmux("split-window", "-t", $"{ManagerSession}:0", "-h", "-l", "75%");
        RunTmux("select-pane", "-t", ManagerNavPane);

        RunTmux("bind", "-n", focusKeybinding,
            "if", "-F", $"#{{==:#{{session_name}},{ManagerSession}}}",
            "select-pane -t {last}",
            $"send-keys {focusKeybinding}");

        if (mouseEnabled)
            RunTmux("set", "-t", ManagerSession, "mouse", "on");

        RunTmux("set", "-t", ManagerSession, "status", "off");

        await Task.CompletedTask;
    }

    public void AttachManagerSession()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "tmux",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("attach-session");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(ManagerSession);

        try
        {
            var process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch { }
    }

    public async Task EmbedSession(string sessionName)
    {
        // Get current right pane ID (placeholder or welcome shell)
        var currentRight = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");

        // Join the pool window's pane into the manager
        var (success, error) = RunTmuxWithError("join-pane", "-h", "-l", "75%",
            "-s", $"{PoolSession}:{sessionName}:0.0",
            "-t", $"{ManagerSession}:0");

        if (!success) return;

        // Kill the old right pane (placeholder shell)
        if (!string.IsNullOrEmpty(currentRight))
            RunTmux("kill-pane", "-t", currentRight);

        RunTmux("select-pane", "-t", ManagerNavPane);
        await Task.CompletedTask;
    }

    public async Task UnembedSession(string sessionName)
    {
        // Create a placeholder window in pool, then move the embedded pane into it
        RunTmux("new-window", "-t", PoolSession, "-n", sessionName);

        var paneId = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");
        if (string.IsNullOrEmpty(paneId)) return;

        var placeholderPaneId = RunTmux("display-message", "-t", $"{PoolSession}:{sessionName}", "-p", "#{pane_id}");

        RunTmux("join-pane", "-s", paneId, "-t", $"{PoolSession}:{sessionName}");

        if (!string.IsNullOrEmpty(placeholderPaneId))
            RunTmux("kill-pane", "-t", placeholderPaneId);

        // Create new placeholder shell in manager right side
        RunTmux("split-window", "-t", $"{ManagerSession}:0", "-h", "-l", "75%");
        RunTmux("select-pane", "-t", ManagerNavPane);
        await Task.CompletedTask;
    }

    public async Task SwapEmbeddedSession(string oldName, string newName)
    {
        var currentPaneId = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");
        if (string.IsNullOrEmpty(currentPaneId)) return;

        // Move old session back to pool
        RunTmux("new-window", "-t", PoolSession, "-n", oldName);
        var placeholderPaneId = RunTmux("display-message", "-t", $"{PoolSession}:{oldName}", "-p", "#{pane_id}");
        RunTmux("join-pane", "-s", currentPaneId, "-t", $"{PoolSession}:{oldName}");
        if (!string.IsNullOrEmpty(placeholderPaneId))
            RunTmux("kill-pane", "-t", placeholderPaneId);

        // Move new session from pool into manager
        var shellPaneId = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");
        RunTmux("join-pane", "-h", "-l", "75%",
            "-s", $"{PoolSession}:{newName}:0.0",
            "-t", $"{ManagerSession}:0");
        if (!string.IsNullOrEmpty(shellPaneId))
            RunTmux("kill-pane", "-t", shellPaneId);

        RunTmux("select-pane", "-t", ManagerNavPane);
        await Task.CompletedTask;
    }

    public string? GetEmbeddedSessionName()
    {
        var envName = RunTmux("show-environment", "-t", ManagerSessionPane, "CCC_SESSION_NAME");
        if (envName != null && envName.StartsWith("CCC_SESSION_NAME="))
            return envName["CCC_SESSION_NAME=".Length..];
        return null;
    }

    public async Task EmbedGridSessions(List<string> sessionNames)
    {
        if (sessionNames.Count < 2 || sessionNames.Count > 6) return;

        var currentPaneId = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");

        // Move first session in
        RunTmux("join-pane", "-h", "-l", "75%",
            "-s", $"{PoolSession}:{sessionNames[0]}:0.0",
            "-t", $"{ManagerSession}:0");

        if (!string.IsNullOrEmpty(currentPaneId))
            RunTmux("kill-pane", "-t", currentPaneId);

        // Move remaining sessions in
        for (int i = 1; i < sessionNames.Count; i++)
        {
            RunTmux("join-pane", "-v",
                "-s", $"{PoolSession}:{sessionNames[i]}:0.0",
                "-t", $"{ManagerSession}:0");
        }

        RunTmux("select-layout", "-t", $"{ManagerSession}:0", "tiled");

        var manifest = string.Join(",", sessionNames);
        RunTmux("set-environment", "-t", ManagerSession, "CCC_GRID_SESSIONS", manifest);

        RunTmux("select-pane", "-t", ManagerNavPane);
        await Task.CompletedTask;
    }

    public async Task RestoreGridToSingleEmbed()
    {
        var manifestRaw = RunTmux("show-environment", "-t", ManagerSession, "CCC_GRID_SESSIONS");
        if (manifestRaw == null || !manifestRaw.StartsWith("CCC_GRID_SESSIONS=")) return;

        var sessionNames = manifestRaw["CCC_GRID_SESSIONS=".Length..].Split(',').ToList();

        var paneList = RunTmux("list-panes", "-t", $"{ManagerSession}:0",
            "-F", "#{pane_index}\t#{pane_id}");

        if (paneList == null) return;

        var panes = paneList.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2)
            .Select(parts => (Index: int.Parse(parts[0]), PaneId: parts[1]))
            .OrderByDescending(p => p.Index)
            .ToList();

        // Move all panes except nav (index 0) and first session (index 1) back to pool
        foreach (var pane in panes.Where(p => p.Index > 1))
        {
            var sessionIndex = pane.Index - 1;
            if (sessionIndex < sessionNames.Count)
            {
                var sessionName = sessionNames[sessionIndex];
                RunTmux("new-window", "-t", PoolSession, "-n", sessionName);
                var placeholder = RunTmux("display-message", "-t", $"{PoolSession}:{sessionName}", "-p", "#{pane_id}");
                RunTmux("join-pane", "-s", pane.PaneId, "-t", $"{PoolSession}:{sessionName}");
                if (!string.IsNullOrEmpty(placeholder))
                    RunTmux("kill-pane", "-t", placeholder);
            }
        }

        RunTmux("set-environment", "-u", "-t", ManagerSession, "CCC_GRID_SESSIONS");
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        // No-op — tmux sessions persist independently of CCC
    }

    /// <summary>
    /// Resolves a session name to its tmux target.
    /// Pool sessions are windows in ccc-pool, standalone sessions are tmux sessions.
    /// </summary>
    private string ResolveTarget(string sessionName)
    {
        var (poolExists, _) = RunTmuxWithError("list-windows", "-t", PoolSession, "-F", "#{window_name}", "-f", $"#{{==:#{{window_name}},{sessionName}}}");
        if (poolExists)
            return $"{PoolSession}:{sessionName}";
        return sessionName;
    }

    private void DetectWaitingByPaneContent(Session session)
    {
        var target = ResolveTarget(session.Name);
        var output = RunTmux("capture-pane", "-t", target, "-p", "-S", "-20");
        if (output == null)
        {
            session.IsWaitingForInput = true;
            return;
        }

        var content = SessionContentAnalyzer.GetContentAboveStatusBar(output);

        if (content == session.PreviousContent)
            session.StableContentCount++;
        else
        {
            session.StableContentCount = 0;
            session.PreviousContent = content;
        }

        var isStable = session.StableContentCount >= SessionContentAnalyzer.StableThreshold;
        session.IsIdle = isStable && SessionContentAnalyzer.IsIdlePrompt(content);
        session.IsWaitingForInput = isStable && !session.IsIdle;
    }

    private static (bool Success, string? Error) RunTmuxWithError(params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tmux",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            var process = Process.Start(startInfo);
            if (process == null)
                return (false, "Failed to start tmux");

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
                return (true, null);

            var error = stderr.Trim();
            return (false, string.IsNullOrEmpty(error) ? "tmux exited with an error" : error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? RunTmux(params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tmux",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch
        {
            return null;
        }
    }
}
