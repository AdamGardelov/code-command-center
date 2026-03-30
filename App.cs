using System.Diagnostics;
using CodeCommandCenter.Enums;
using CodeCommandCenter.Handlers;
using CodeCommandCenter.Models;
using CodeCommandCenter.Services;
using CodeCommandCenter.UI;
using Spectre.Console;

namespace CodeCommandCenter;

public class App(ISessionBackend backend, CccConfig config, string executablePath, bool mobileMode = false)
{
    private readonly AppState _state = new()
    {
        MobileMode = mobileMode
    };

    private readonly CccConfig _config = config;
    private readonly string _executablePath = executablePath;
    private FlowHelper _flow = null!;
    private DiffHandler _diffHandler = null!;
    private SettingsHandler _settingsHandler = null!;
    private SessionHandler _sessionHandler = null!;
    private GroupHandler _groupHandler = null!;
    private Dictionary<string, string> _keyMap = new();
    private string? _capturedPane;
    private DateTime _lastCapture = DateTime.MinValue;
    private string? _lastSelectedSession;
    private string? _lastSpinnerFrame;
    private bool _hasSpinningSessions;
    private bool _claudeAvailable;
    private Task<string?>? _updateCheck;
    private DateTime _lastUpdateCheck = DateTime.UtcNow;
    private static readonly TimeSpan _updateCheckInterval = TimeSpan.FromMinutes(20);
    private bool _wantsUpdate;
    private int _startupPollCount;
    private DateTime _lastSessionLoad = DateTime.MinValue;
    private DateTime _lastRemoteDetect = DateTime.MinValue;
    private DateTime _lastRemoteCapture = DateTime.MinValue;
    private Task? _pendingSessionLoad;

    public void Run()
    {
        if (!backend.IsAvailable())
        {
            AnsiConsole.MarkupLine("[red]Session backend is not available.[/]");
            return;
        }

        // Sidebar mode: if already inside manager, skip the "exit tmux" check
        if (!backend.IsInsideManager())
        {
            if (backend.IsInsideHost())
            {
                AnsiConsole.MarkupLine("[red]CodeCommandCenter should run outside the session host.[/]");
                AnsiConsole.MarkupLine("[grey]It manages sessions from the outside. Exit tmux first.[/]");
                return;
            }

            // Bootstrap: set up pool + manager and attach
            backend.SetupPool().GetAwaiter().GetResult();
            if (backend.ManagerSessionExists())
            {
                backend.AttachManagerSession();
            }
            else
            {
                backend.SetupManagerSession(_executablePath, _config.FocusKeybinding, _config.MouseEnabled)
                    .GetAwaiter().GetResult();
                backend.AttachManagerSession();
            }
            return;
        }

        // We're inside the manager — enable sidebar mode
        _state.IsSidebarMode = true;

        _flow = new FlowHelper(_config);
        _diffHandler = new DiffHandler(_state);
        _settingsHandler = new SettingsHandler(_state, _config, Render, RefreshKeybindings);
        _sessionHandler = new SessionHandler(_state, _config, _flow, backend, LoadSessions, Render, () =>
        {
            _lastSelectedSession = null;

            // Resize pane back to preview width and immediately re-capture so
            // the next render shows fresh content (not stale pre-attach data)
            var session = _state.GetSelectedSession();
            if (session != null && !_state.MobileMode)
            {
                var previewWidth = Math.Max(20, Console.WindowWidth - 35 - 8);
                backend.ResizeWindow(session.Name, previewWidth, Console.WindowHeight);
                _capturedPane = backend.CapturePaneContent(session.Name);
                _lastSelectedSession = session.Name;
            }
        });
        _groupHandler = new GroupHandler(
            _state, _config, _flow, backend, LoadSessions, Render,
            () => _lastSelectedSession = null,
            () => { });

        _claudeAvailable = backend.HasClaude();
        if (!_claudeAvailable)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: 'claude' was not found in PATH.[/]");
            AnsiConsole.MarkupLine("[grey]New sessions will fail to start. Install Claude Code: https://docs.anthropic.com/en/docs/claude-code[/]");
        }

        var bindings = KeyBindingService.Resolve(_config);
        _keyMap = KeyBindingService.BuildKeyMap(bindings);
        _state.Keybindings = bindings;

        // Recover from a previous crash that left sessions in a grid
        if (backend.GridSessionExists())
        {
            backend.RestoreFromGrid();
        }

        LoadSessions();
        _lastSessionLoad = DateTime.UtcNow;
        _updateCheck = UpdateChecker.CheckForUpdateAsync();

        // Migrate standalone sessions to pool on first run
        var standaloneSessions = _state.Sessions.Where(s => !s.IsPoolSession && s.RemoteHostName == null).ToList();
        if (standaloneSessions.Count > 0)
        {
            backend.MigrateStandaloneSessionsToPool(standaloneSessions).GetAwaiter().GetResult();
            LoadSessions();
        }

        // Auto-embed first local session if available
        var localSessions = _state.Sessions.Where(s => s.IsPoolSession && !s.IsDead).ToList();
        if (localSessions.Count > 0)
        {
            var first = localSessions[0];
            backend.EmbedSession(first.Name).GetAwaiter().GetResult();
            _state.SetEmbedded(first.Name);
        }

        try
        {
            // Try alternate screen buffer for clean TUI
            Console.Write("\e(B");      // Ensure ASCII charset before entering alternate screen
            Console.Write("\e[?1049h"); // Enter alternate screen
            Console.Write("\e(B");      // Reset charset on alternate screen too
            Console.Write("\e[0m");     // Reset all attributes
            Console.Write("\e[?1003l\e[?1006l\e[?1015l\e[?1000l"); // Disable mouse tracking
            Console.CursorVisible = false;

            MainLoop();
        }
        finally
        {
            Console.CursorVisible = true;
            Console.Write("\e[?1049l"); // Leave alternate screen
            backend.Dispose();
        }

        if (_wantsUpdate)
            RunUpdate();
    }

    private void MainLoop()
    {
        Render();

        while (_state.Running)
        {
            var hadInput = false;

            if (Console.KeyAvailable)
            {
                hadInput = true;

                // Drain all buffered keys before rendering once —
                // prevents input lag over slow SSH connections
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    HandleKey(key);
                }

                Render();
            }

            // Check if update check completed
            if (_updateCheck is { IsCompleted: true })
            {
                var latest = _updateCheck.Result;
                _updateCheck = null;
                _lastUpdateCheck = DateTime.UtcNow;
                if (latest != null)
                {
                    _state.LatestVersion = latest;
                    Render();
                }
            }

            // Periodic update check
            if (_updateCheck == null && DateTime.UtcNow - _lastUpdateCheck > _updateCheckInterval)
                _updateCheck = UpdateChecker.CheckForUpdateAsync();

            // Re-render when a status message expires
            if (_state.HasPendingStatus)
                if (_state.GetActiveStatus() == null)
                    Render();

            // Re-render when spinner frame advances (only if sessions were spinning at last poll)
            // Skip in mobile mode and grid mode — spinner updates aren't worth the render
            // cost over SSH or during active grid typing (full layout rebuild every ~80ms)
            if (!_state.MobileMode && _state.ViewMode == ViewMode.List)
            {
                var spinnerFrame = Renderer.GetSpinnerFrame();
                if (spinnerFrame != _lastSpinnerFrame)
                {
                    _lastSpinnerFrame = spinnerFrame;
                    if (_hasSpinningSessions)
                        Render();
                }
            }

            // Periodically reload session list when remote hosts are configured
            // so offline/reconnected state is detected without manual refresh.
            // Run on a background task to avoid blocking the main loop (SSH round-trips
            // to each remote host can take seconds).
            if (_config.RemoteHosts.Count > 0 && _pendingSessionLoad == null
                && (DateTime.UtcNow - _lastSessionLoad).TotalSeconds > 15)
            {
                _lastSessionLoad = DateTime.UtcNow;
                _pendingSessionLoad = Task.Run(() =>
                {
                    // Perform the expensive SSH work on a background thread.
                    // ListSessions() is the only part that contacts remotes;
                    // the rest of LoadSessions() just processes the results.
                    return backend.ListSessions();
                });
            }

            if (_pendingSessionLoad is { IsCompleted: true })
            {
                try
                {
                    // Apply results on the main thread
                    var sessions = ((Task<List<Session>>)_pendingSessionLoad).Result;
                    var previousSessions = _state.Sessions;
                    _state.Sessions = sessions;
                    _state.Config = _config;
                    _state.HasUntrackedRemoteSessions = backend.GetUntrackedRemoteSessions().Count > 0;
                    if (backend is BackendRouter routerForStatus)
                        _state.MachineOnlineStatus = routerForStatus.GetRemoteOnlineStatus();
                    ApplySessionMetadata(previousSessions);
                    LoadGroups();
                    _state.ClampCursor();
                    NotificationService.Cleanup(_state.Sessions.Select(s => s.Name));
                    HookStateService.Cleanup(_state.Sessions.Select(s => s.Name));
                    Render();
                }
                catch
                {
                    // SSH failure — ignore, will retry next interval
                }

                _pendingSessionLoad = null;
            }

            if (_state.ViewMode != ViewMode.Settings && _state.ViewMode != ViewMode.DiffOverlay
                && (DateTime.Now - _lastCapture).TotalMilliseconds > 500)
            {
                if (!_state.MobileMode)
                    ResizePreviewPane();

                if (UpdateCapturedPane())
                    Render();

                _lastCapture = DateTime.Now;
            }

            Thread.Sleep(hadInput ? 5 : 30);
        }
    }

    private void LoadSessions()
    {
        var previousSessions = _state.Sessions;
        _state.Sessions = backend.ListSessions();
        _state.Config = _config;
        _state.HasUntrackedRemoteSessions = backend.GetUntrackedRemoteSessions().Count > 0;
        if (backend is BackendRouter router)
            _state.MachineOnlineStatus = router.GetRemoteOnlineStatus();
        ApplySessionMetadata(previousSessions);

        LoadGroups();
        _state.ClampCursor();
        NotificationService.Cleanup(_state.Sessions.Select(s => s.Name));
        HookStateService.Cleanup(_state.Sessions.Select(s => s.Name));
    }

    /// <summary>
    /// Applies config metadata (descriptions, colors, git info, etc.) to the current session list.
    /// Extracted so both synchronous LoadSessions and the async background reload can share it.
    /// </summary>
    private void ApplySessionMetadata(List<Session>? previousSessions = null)
    {
        var oldSessions = (previousSessions ?? _state.Sessions).ToDictionary(s => s.Name, s => s);
        var startCommitsDirty = false;
        foreach (var s in _state.Sessions)
        {
            if (_config.SessionDescriptions.TryGetValue(s.Name, out var desc))
                s.Description = desc;
            if (_config.SessionColors.TryGetValue(s.Name, out var color))
                s.ColorTag = color;
            s.IsExcluded = _config.ExcludedSessions.Contains(s.Name);
            s.SkipPermissions = _config.SkipPermissionsSessions.Contains(s.Name);
            if (!s.IsOffline)
                backend.ApplyStatusColor(s.Name, color ?? "grey42");

            // Preserve content tracking state so sessions don't briefly flash as "working"
            if (oldSessions.TryGetValue(s.Name, out var old) && old != s)
            {
                s.PreviousContent = old.PreviousContent;
                s.StableContentCount = old.StableContentCount;
                s.IsWaitingForInput = old.IsWaitingForInput;
                s.IsIdle = old.IsIdle;
            }

            // Hydrate or snapshot StartCommitSha for diff tracking
            if (_config.SessionStartCommits.TryGetValue(s.Name, out var sha))
            {
                s.StartCommitSha = sha;
            }
            else if (s.CurrentPath != null && s.GitBranch != null)
            {
                var host = _config.RemoteHosts.FirstOrDefault(h => h.Name == s.RemoteHostName);
                var headSha = GitService.GetCurrentCommitSha(s.CurrentPath, host?.Host);
                if (headSha != null)
                {
                    s.StartCommitSha = headSha;
                    _config.SessionStartCommits[s.Name] = headSha;
                    startCommitsDirty = true;
                }
            }
        }

        // Re-detect git info for remote sessions (backend only does local detection)
        foreach (var s in _state.Sessions.Where(s => s.RemoteHostName != null && !s.IsOffline))
        {
            var host = _config.RemoteHosts.FirstOrDefault(h => h.Name == s.RemoteHostName);
            if (host != null)
                GitService.DetectGitInfo(s, host.Host);
        }

        var configDirty = startCommitsDirty;

        // Prune orphaned config entries for sessions that no longer exist
        var liveNames = new HashSet<string>(_state.Sessions.Select(s => s.Name));
        configDirty |= PruneDict(_config.SessionDescriptions, liveNames);
        configDirty |= PruneDict(_config.SessionColors, liveNames);
        configDirty |= PruneDict(_config.SessionStartCommits, liveNames);
        configDirty |= PruneSet(_config.ExcludedSessions, liveNames);
        configDirty |= PruneSet(_config.SkipPermissionsSessions, liveNames);

        if (configDirty)
            ConfigService.SaveConfig(_config);
    }

    private void LoadGroups()
    {
        var liveSessionNames = new HashSet<string>(_state.Sessions.Select(s => s.Name));

        // Clean up persisted config: remove dead sessions and empty groups
        var configChanged = false;
        var emptyGroups = new List<string>();
        foreach (var (name, group) in _config.Groups)
        {
            var removed = group.Sessions.RemoveAll(s => !liveSessionNames.Contains(s));
            if (removed > 0)
                configChanged = true;
            // Only remove groups with no sessions AND no repos (worktree groups survive with zero sessions)
            if (group.Sessions.Count == 0 && group.Repos.Count == 0)
                emptyGroups.Add(name);
        }

        foreach (var name in emptyGroups)
            _config.Groups.Remove(name);

        if (configChanged)
            ConfigService.SaveConfig(_config);

        _state.Groups = _config.Groups.Values
            .Select(g => new SessionGroup
            {
                Name = g.Name,
                Description = g.Description,
                Color = g.Color,
                WorktreePath = g.WorktreePath,
                Sessions = g.Sessions.ToList(),
                Repos = new Dictionary<string, string>(g.Repos),
            })
            .OrderBy(g => g.Name)
            .ToList();
        _state.InitExpandedGroups();
    }

    private bool UpdateCapturedPane()
    {
        // Snapshot waiting/idle state before detection
        var wasWaiting = _state.Sessions
            .Where(s => !s.IsExcluded)
            .ToDictionary(s => s.Name, s => s.IsWaitingForInput);
        var wasIdle = _state.Sessions
            .Where(s => !s.IsExcluded)
            .ToDictionary(s => s.Name, s => s.IsIdle);

        // Detect local sessions every poll (fast, no SSH).
        // Detect remote sessions on a slower 3-second interval to avoid blocking
        // the main loop with SSH round-trips on every 500ms capture cycle.
        var localSessions = _state.Sessions.Where(s => s.RemoteHostName == null).ToList();
        var remoteSessions = _state.Sessions.Where(s => s.RemoteHostName != null).ToList();

        backend.DetectWaitingForInputBatch(localSessions);

        var now = DateTime.UtcNow;
        if (remoteSessions.Count > 0 && (now - _lastRemoteDetect).TotalSeconds > 3)
        {
            _lastRemoteDetect = now;
            backend.DetectWaitingForInputBatch(remoteSessions);
        }
        _hasSpinningSessions = _state.Sessions.Any(s => !s.IsWaitingForInput && !s.IsIdle && !s.IsDead);

        // Detect false -> true transitions and notify.
        // Skip the first 6 polls (~3 seconds) so sessions have time to establish their
        // baseline waiting state — avoids a burst of notifications on startup.
        if (_startupPollCount > 5)
        {
            var selectedName = _state.GetSelectedSession()?.Name;
            var transitioned = _state.Sessions
                .Where(s => !s.IsExcluded
                            && s.IsWaitingForInput
                            && wasWaiting.TryGetValue(s.Name, out var was) && !was
                            && s.Name != selectedName)
                .ToList();

            if (transitioned.Count > 0)
            {
                var notified = NotificationService.NotifyWaiting(transitioned, _config.Notifications);
                if (notified != null)
                    _state.SetStatus($"⏳ {notified}");
            }
        }
        else
            _startupPollCount++;

        // Mobile mode doesn't show pane previews — only re-render
        // when a session's waiting status actually changed
        if (_state.MobileMode)
        {
            return _state.Sessions.Any(s =>
                wasWaiting.TryGetValue(s.Name, out var was) && was != s.IsWaitingForInput);
        }

        // Skip content capture for embedded session (it's a live tmux pane)
        // But still run waiting-for-input detection for status indicators
        if (_state.EmbeddedSessionName != null || _state.IsEmbeddedGridMode)
        {
            return _state.Sessions.Any(s =>
                !s.IsExcluded
                && ((wasWaiting.TryGetValue(s.Name, out var ww) && ww != s.IsWaitingForInput)
                    || (wasIdle.TryGetValue(s.Name, out var wi) && wi != s.IsIdle)));
        }

        var session = _state.GetSelectedSession();
        var sessionName = session?.Name;

        if (sessionName != _lastSelectedSession)
        {
            _lastSelectedSession = sessionName;
            _capturedPane = session != null ? backend.CapturePaneContent(session.Name) : null;
            return true;
        }

        // Re-render if any session's status icon changed (waiting/idle transitions)
        var statusChanged = _state.Sessions.Any(s =>
            !s.IsExcluded
            && ((wasWaiting.TryGetValue(s.Name, out var ww) && ww != s.IsWaitingForInput)
                || (wasIdle.TryGetValue(s.Name, out var wi) && wi != s.IsIdle)));

        if (session == null)
            return statusChanged;

        // For remote sessions, throttle pane capture to every 2 seconds to reduce SSH blocking.
        // Local captures are fast and happen every 500ms as before.
        var isRemote = session.RemoteHostName != null;
        if (isRemote && (DateTime.UtcNow - _lastRemoteCapture).TotalSeconds < 2)
            return statusChanged;

        var changed = statusChanged;
        var newContent = backend.CapturePaneContent(session.Name);

        if (isRemote)
            _lastRemoteCapture = DateTime.UtcNow;

        if (newContent != _capturedPane)
        {
            _capturedPane = newContent;
            changed = true;
        }

        return changed;
    }

    private void ResizePreviewPane()
    {
        // Skip resize for embedded sessions — tmux handles pane sizing
        if (_state.EmbeddedSessionName != null || _state.IsEmbeddedGridMode) return;

        if (_state.ViewMode != ViewMode.List)
            return;

        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        // Match the width calculation in Renderer.BuildPreviewPanel.
        // ResizeWindow is a no-op when the session is already at this size.
        var targetWidth = Math.Max(20, Console.WindowWidth - 35 - 8);
        backend.ResizeWindow(session.Name, targetWidth, Console.WindowHeight);
    }

    private void Render()
    {
        // Synchronized output — terminal buffers everything and flips atomically,
        // eliminating tearing/jumping when redrawing the full screen
        Console.Write("\e[?2026h");
        // Disable mouse tracking before each render — captured pane content may contain
        // sequences that re-enable mouse tracking (e.g. \e[?1003h from Claude Code output).
        // Even though AnsiParser strips these, this acts as defense-in-depth.
        Console.Write("\e[?1003l\e[?1006l\e[?1015l\e[?1000l");
        Console.SetCursorPosition(0, 0);
        if (_state.ViewMode == ViewMode.Settings)
            AnsiConsole.Write(Renderer.BuildSettingsLayout(_state, _config));
        else if (_state.ViewMode == ViewMode.DiffOverlay)
            AnsiConsole.Write(Renderer.BuildDiffOverlayLayout(_state));
        else
            AnsiConsole.Write(Renderer.BuildLayout(_state, _capturedPane));
        Console.Write("\e[?2026l");
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (_state.IsInputMode)
        {
            HandleInputKey(key);
            return;
        }

        if (_state.MobileMode)
        {
            HandleMobileKey(key);
            return;
        }

        if (_state.ViewMode == ViewMode.Settings)
        {
            _settingsHandler.HandleKey(key);
            return;
        }

        if (_state.ViewMode == ViewMode.DiffOverlay)
        {
            _diffHandler.HandleKey(key);
            return;
        }

        if (_state.HasPendingStatus)
        {
            _state.ClearStatus();
            return;
        }

        // List view arrow keys — unified tree navigation
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveCursor(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveCursor(1);
                return;
        }

        var keyId = FlowHelper.ResolveKeyId(key);
        if (_keyMap.TryGetValue(keyId, out var actionId))
            DispatchAction(actionId);
    }

    private void DispatchAction(string actionId)
    {
        // When cursor is on a group header in list view, intercept actions
        if (_state.ViewMode == ViewMode.List && _state.ActiveGroup == null)
        {
            var currentItem = _state.GetTreeItems().ElementAtOrDefault(_state.CursorIndex);
            if (currentItem is TreeItem.RepoItem ri)
            {
                if (actionId == "attach")
                {
                    var sessionName = _groupHandler.CreateRepoSession(
                        ri.GroupName, ri.RepoName, ri.RepoPath, _claudeAvailable);
                    if (sessionName != null)
                    {
                        var created = _state.Sessions.FirstOrDefault(s => s.Name == sessionName);
                        if (created != null)
                            _sessionHandler.Embed(created);
                    }
                }
                return;
            }

            if (currentItem is TreeItem.MachineHeader mh)
            {
                switch (actionId)
                {
                    case "attach":
                    case "toggle-expand":
                        _state.ToggleMachineExpanded(
                            mh.IsLocal ? AppState.LocalMachineKey : mh.HostName);
                        _state.ClampCursor();
                        return;
                    case "new-session":
                        _sessionHandler.Create(_claudeAvailable);
                        return;
                    case "toggle-grid":
                        ToggleGridView();
                        return;
                }
                // All other actions are no-ops on machine headers
                return;
            }

            if (currentItem is TreeItem.GroupHeader gh)
            {
                switch (actionId)
                {
                    case "attach":
                        // Enter on worktree group = open/embed root session
                        if (!string.IsNullOrEmpty(gh.Group.WorktreePath))
                        {
                            var rootSession = _groupHandler.OpenWorktreeSession(_claudeAvailable);
                            if (rootSession != null)
                            {
                                var ws = _state.Sessions.FirstOrDefault(s => s.Name == rootSession);
                                if (ws != null)
                                    _sessionHandler.Embed(ws);
                            }
                        }
                        else
                        {
                            // Non-worktree group: toggle expand/collapse
                            _state.ToggleGroupExpanded(gh.Group.Name);
                            _state.ClampCursor();
                        }
                        return;
                    case "toggle-expand":
                        _state.ToggleGroupExpanded(gh.Group.Name);
                        _state.ClampCursor();
                        return;
                    case "delete-session":
                        _groupHandler.Delete();
                        return;
                    case "edit-session":
                        _groupHandler.Edit();
                        return;
                    case "move-to-group":
                        return; // Not applicable for group headers
                }
            }
        }

        // When in group grid, delete removes session from group
        if (_state.ActiveGroup != null && actionId == "delete-session")
        {
            _groupHandler.DeleteSessionFromGroup();
            return;
        }

        switch (actionId)
        {
            case "navigate-up":
                MoveCursor(-1);
                break;
            case "navigate-down":
                MoveCursor(1);
                break;
            case "approve":
                _sessionHandler.SendQuickKey("y");
                break;
            case "reject":
                _sessionHandler.SendQuickKey("n");
                break;
            case "send-text":
                _sessionHandler.SendText();
                break;
            case "attach":
                _sessionHandler.Embed();
                break;
            case "toggle-diff":
                _diffHandler.Open();
                break;
            case "toggle-grid":
                ToggleGridView();
                break;
            case "new-session":
                _sessionHandler.Create(_claudeAvailable);
                break;
            case "create-background":
                _sessionHandler.Create(_claudeAvailable, embedAfterCreate: false);
                break;
            case "new-group":
                _groupHandler.CreateNew(_claudeAvailable);
                break;
            case "open-folder":
                _sessionHandler.OpenFolder();
                break;
            case "open-ide":
                _sessionHandler.OpenInIde();
                break;
            case "open-settings":
                _state.EnterSettings();
                break;
            case "delete-session":
                _sessionHandler.Delete();
                break;
            case "edit-session":
                _sessionHandler.Edit();
                break;
            case "toggle-exclude":
                _sessionHandler.ToggleExclude();
                break;
            case "adopt-remote":
                _sessionHandler.AdoptRemoteSession();
                break;
            case "move-to-group":
                _groupHandler.MoveSessionToGroup();
                break;
            case "review-pr":
                _sessionHandler.ReviewPr(_claudeAvailable);
                break;
            case "update":
                if (_state.LatestVersion != null)
                {
                    _wantsUpdate = true;
                    _state.Running = false;
                }

                break;
            case "toggle-expand":
                // Only meaningful on group headers (handled above)
                break;
            case "refresh":
                LoadSessions();
                _state.SetStatus("Refreshed");
                break;
            case "quit":
                var quitMsg = "Quit? (y/n)";
                _state.SetStatus(quitMsg);
                Render();
                var quitConfirm = Console.ReadKey(true);
                if (quitConfirm.Key == ConsoleKey.Y)
                    _state.Running = false;
                else
                    _state.SetStatus("Cancelled");
                break;
        }
    }

    private void HandleInputKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _state.IsInputMode = false;
                _state.InputBuffer = "";
                _state.SetStatus("Cancelled");
                break;

            case ConsoleKey.Enter:
                var text = _state.InputBuffer;
                var target = _state.InputTarget;
                _state.IsInputMode = false;
                _state.InputBuffer = "";

                if (text.Length > 0 && target != null)
                {
                    var sendError = backend.SendKeys(target, text);
                    if (sendError == null)
                    {
                        _state.SetStatus($"Sent to {target}");
                        _lastSelectedSession = null;
                    }
                    else
                    {
                        _state.SetStatus(sendError);
                    }
                }
                else
                {
                    _state.SetStatus("Cancelled");
                }

                break;

            case ConsoleKey.Backspace:
                if (_state.InputBuffer.Length > 0)
                    _state.InputBuffer = _state.InputBuffer[..^1];
                break;

            default:
                if (key.KeyChar >= ' ' && _state.InputBuffer.Length < 500)
                    _state.InputBuffer += key.KeyChar;
                break;
        }
    }

    private void RefreshKeybindings()
    {
        var bindings = KeyBindingService.Resolve(_config);
        _keyMap = KeyBindingService.BuildKeyMap(bindings);
        _state.Keybindings = bindings;
    }

    private void MoveCursor(int delta)
    {
        var treeItems = _state.GetTreeItems();
        if (treeItems.Count == 0)
            return;
        _state.CursorIndex = Math.Clamp(_state.CursorIndex + delta, 0, treeItems.Count - 1);
        _lastSelectedSession = null; // Force pane recapture
    }

    private void HandleMobileKey(ConsoleKeyInfo key)
    {
        if (_state.HasPendingStatus)
        {
            _state.ClearStatus();
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveMobileCursor(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveMobileCursor(1);
                return;
        }

        var keyId = FlowHelper.ResolveKeyId(key);

        if (keyId == "g")
        {
            _state.CycleGroupFilter();
            _lastSelectedSession = null;
            return;
        }

        if (_keyMap.TryGetValue(keyId, out var actionId))
            DispatchMobileAction(actionId);
    }

    private void MoveMobileCursor(int delta)
    {
        var visible = _state.GetMobileVisibleSessions();
        if (visible.Count == 0)
            return;
        _state.CursorIndex = Math.Clamp(_state.CursorIndex + delta, 0, visible.Count - 1);
        _lastSelectedSession = null;
    }

    private void DispatchMobileAction(string actionId)
    {
        switch (actionId)
        {
            case "navigate-up":
                MoveMobileCursor(-1);
                break;
            case "navigate-down":
                MoveMobileCursor(1);
                break;
            case "approve":
                _sessionHandler.SendQuickKey("y");
                break;
            case "reject":
                _sessionHandler.SendQuickKey("n");
                break;
            case "send-text":
                _sessionHandler.SendText();
                break;
            case "attach":
                _sessionHandler.Embed();
                break;
            case "refresh":
                LoadSessions();
                _state.SetStatus("Refreshed");
                break;
            case "quit":
                var mobileQuitMsg = "Quit? (y/n)";
                _state.SetStatus(mobileQuitMsg);
                Render();
                var quitConfirm2 = Console.ReadKey(true);
                if (quitConfirm2.Key == ConsoleKey.Y)
                    _state.Running = false;
                else
                    _state.SetStatus("Cancelled");
                break;
        }
    }

    private void ToggleGridView()
    {
        // Determine which sessions to grid
        List<Session> gridSessions;
        string? groupName = null;

        var treeItems = _state.GetTreeItems();
        var currentItem = treeItems.ElementAtOrDefault(_state.CursorIndex);

        if (currentItem is TreeItem.SessionItem { GroupName: not null } si)
        {
            groupName = si.GroupName;
            _state.EnterGroupGrid(groupName);
            gridSessions = _state.GetGridSessions();
        }
        else if (currentItem is TreeItem.GroupHeader gh)
        {
            groupName = gh.Group.Name;
            _state.EnterGroupGrid(groupName);
            gridSessions = _state.GetGridSessions();
        }
        else if (currentItem is TreeItem.MachineHeader mh)
        {
            // Grid sessions under this machine — local only (grid uses local tmux)
            if (!mh.IsLocal || mh.IsOffline)
            {
                _state.SetStatus("Grid view only works for local sessions");
                return;
            }
            gridSessions = _state.GetGridSessions();
        }
        else
        {
            gridSessions = _state.GetGridSessions();
        }

        // Filter out remote sessions (grid is local tmux only)
        gridSessions = gridSessions.Where(s => s.RemoteHostName == null).ToList();

        if (gridSessions.Count < 2)
        {
            if (groupName != null)
                _state.LeaveGroupGrid();
            _state.SetStatus("Need at least 2 local sessions for grid");
            return;
        }

        if (gridSessions.Count > 6)
        {
            if (groupName != null)
                _state.LeaveGroupGrid();
            _state.SetStatus("Too many sessions for grid (max 6)");
            return;
        }

        var sessionNames = gridSessions.Select(s => s.Name).ToList();

        if (_state.IsEmbeddedGridMode)
        {
            // Exit grid mode
            backend.RestoreGridToSingleEmbed().GetAwaiter().GetResult();
            _state.LeaveEmbeddedGrid();
            // Re-embed the first session
            if (gridSessions.Count > 0)
            {
                backend.EmbedSession(gridSessions[0].Name).GetAwaiter().GetResult();
                _state.SetEmbedded(gridSessions[0].Name);
            }

            if (groupName != null)
                _state.LeaveGroupGrid();

            _state.SetStatus("Exited grid view");
        }
        else
        {
            // Enter grid mode — unembed current, embed grid
            var currentEmbedded = _state.EmbeddedSessionName;
            if (currentEmbedded != null)
            {
                backend.UnembedSession(currentEmbedded).GetAwaiter().GetResult();
                _state.SetEmbedded(null);
            }

            backend.EmbedGridSessions(sessionNames).GetAwaiter().GetResult();
            _state.EnterEmbeddedGrid(sessionNames);
            _state.SetStatus($"Grid view: {sessionNames.Count} sessions");
        }

        _lastSelectedSession = null;
        LoadSessions();
        Render();
    }

    private void RunUpdate()
    {
        AnsiConsole.MarkupLine($"[yellow]Updating to v{_state.LatestVersion}...[/]\n");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            ArgumentList =
            {
                "-c",
                "curl -fsSL https://raw.githubusercontent.com/AdamGardelov/code-command-center/main/install.sh | bash"
            },
            UseShellExecute = false,
        });
        process?.WaitForExit();
    }

    private static bool PruneDict(Dictionary<string, string> dict, HashSet<string> liveNames)
    {
        var stale = dict.Keys.Where(k => !liveNames.Contains(k)).ToList();
        foreach (var key in stale)
            dict.Remove(key);
        return stale.Count > 0;
    }

    private static bool PruneSet(HashSet<string> set, HashSet<string> liveNames)
    {
        var before = set.Count;
        set.RemoveWhere(s => !liveNames.Contains(s));
        return set.Count != before;
    }
}
