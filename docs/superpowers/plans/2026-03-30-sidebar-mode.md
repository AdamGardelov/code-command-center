# Sidebar Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace CCC's blocking attach/detach model with a persistent sidebar that always shows navigation while sessions are interactive tmux panes.

**Architecture:** Two tmux sessions — `ccc-pool` holds all session panes as windows, `ccc-manager` is user-facing with CCC nav in left pane and the active session's pane moved in from the pool on the right. Switching sessions moves panes between pool and manager via `tmux move-pane`. CCC's render loop never blocks.

**Tech Stack:** .NET 10 / C# / Spectre.Console / tmux

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `Services/ISessionBackend.cs` | Modify | Add pool/manager/embed interface methods |
| `Services/TmuxBackend.cs` | Modify | Implement pool setup, manager setup, embed/unembed, grid-in-manager |
| `Services/BackendRouter.cs` | Modify | Route new methods to local backend (pool is local-only) |
| `App.cs` | Modify | Bootstrap logic, replace attach with embed, remove preview capture for embedded sessions |
| `Handlers/SessionHandler.cs` | Modify | Replace AttachSession with EmbedSession, add background create |
| `Handlers/GroupHandler.cs` | Modify | Update grid flow to use embed-based grid |
| `UI/AppState.cs` | Modify | Add EmbeddedSessionName, IsGridMode tracking |
| `UI/Renderer.cs` | Modify | Sidebar-only layout (no preview panel for local embedded sessions) |
| `Models/CccConfig.cs` | Modify | Add focusKeybinding, mouseEnabled fields |
| `Program.cs` | Modify | Pass executable path for re-exec |

---

### Task 1: Extend ISessionBackend with Pool/Manager/Embed Methods

**Files:**
- Modify: `Services/ISessionBackend.cs`

- [ ] **Step 1: Add pool management methods to ISessionBackend**

Add these default interface methods after the existing `IsInsideHost()` method (line 36):

```csharp
// Pool model — sidebar mode
Task SetupPool() => Task.CompletedTask;
Task CreateSessionInPool(string name, string dir, string? claudeConfigDir = null,
    bool dangerouslySkipPermissions = false, string? initialPrompt = null,
    bool shellOnly = false) => Task.CompletedTask;

// Manager lifecycle
Task SetupManagerSession(string executablePath, string focusKeybinding = "C-Space",
    bool mouseEnabled = true) => Task.CompletedTask;
void AttachManagerSession();
bool IsInsideManager() => false;

// Embed/unembed
Task EmbedSession(string sessionName) => Task.CompletedTask;
Task UnembedSession(string sessionName) => Task.CompletedTask;
Task SwapEmbeddedSession(string oldName, string newName) => Task.CompletedTask;
string? GetEmbeddedSessionName() => null;

// Grid in manager
Task EmbedGridSessions(List<string> sessionNames) => Task.CompletedTask;
Task RestoreGridToSingleEmbed() => Task.CompletedTask;
```

- [ ] **Step 2: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded. 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Services/ISessionBackend.cs
git commit -m "feat: add pool/manager/embed methods to ISessionBackend"
```

---

### Task 2: Add Config Fields for Sidebar Mode

**Files:**
- Modify: `Models/CccConfig.cs`

- [ ] **Step 1: Add focusKeybinding and mouseEnabled to CccConfig**

Add after the `PrIncludeDrafts` property (line 24 in CccConfig.cs):

```csharp
public string FocusKeybinding { get; set; } = "C-Space";
public bool MouseEnabled { get; set; } = true;
```

- [ ] **Step 2: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Models/CccConfig.cs
git commit -m "feat: add sidebar mode config fields (focusKeybinding, mouseEnabled)"
```

---

### Task 3: Add EmbeddedSessionName and IsGridMode to AppState

**Files:**
- Modify: `UI/AppState.cs`

- [ ] **Step 1: Add sidebar mode state properties**

Add to AppState class properties, near `ActiveGroup` (around line 20):

```csharp
public string? EmbeddedSessionName { get; set; }
public bool IsEmbeddedGridMode { get; set; }
public List<string> GridEmbeddedSessionNames { get; set; } = [];
```

- [ ] **Step 2: Add helper methods for embed state**

Add after the existing `LeaveGroupGrid()` method:

```csharp
public void SetEmbedded(string? sessionName)
{
    EmbeddedSessionName = sessionName;
}

public void EnterEmbeddedGrid(List<string> sessionNames)
{
    IsEmbeddedGridMode = true;
    GridEmbeddedSessionNames = sessionNames;
}

public void LeaveEmbeddedGrid()
{
    IsEmbeddedGridMode = false;
    GridEmbeddedSessionNames.Clear();
}
```

- [ ] **Step 3: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add UI/AppState.cs
git commit -m "feat: add embedded session state tracking to AppState"
```

---

### Task 4: Implement Pool and Manager Setup in TmuxBackend

This is the core tmux plumbing. All new pool/manager methods go in TmuxBackend.

**Files:**
- Modify: `Services/TmuxBackend.cs`

- [ ] **Step 1: Add constants for pool and manager session names**

Add as private constants at the top of the TmuxBackend class:

```csharp
private const string PoolSession = "ccc-pool";
private const string ManagerSession = "ccc-manager";
private const string ManagerNavPane = "ccc-manager:0.0";  // left pane
private const string ManagerSessionPane = "ccc-manager:0.1";  // right pane
```

- [ ] **Step 2: Implement SetupPool()**

Add this method to TmuxBackend:

```csharp
public async Task SetupPool()
{
    // Check if pool already exists
    var (exists, _) = RunTmuxWithError("has-session", "-t", $"={PoolSession}");
    if (exists) return;

    // Create pool session in detached mode with a placeholder window
    RunTmux("new-session", "-d", "-s", PoolSession, "-n", "placeholder");
    await Task.CompletedTask;
}
```

- [ ] **Step 3: Implement CreateSessionInPool()**

```csharp
public async Task CreateSessionInPool(string name, string dir, string? claudeConfigDir = null,
    bool dangerouslySkipPermissions = false, string? initialPrompt = null,
    bool shellOnly = false)
{
    var command = SshService.BuildSessionCommand(null, dir, dangerouslySkipPermissions, initialPrompt, shellOnly);

    var args = new List<string> { "new-window", "-t", PoolSession, "-n", name };

    args.AddRange(["-e", $"CCC_SESSION_NAME={name}"]);
    if (!string.IsNullOrEmpty(claudeConfigDir))
        args.AddRange(["-e", $"CLAUDE_CONFIG_DIR={claudeConfigDir}"]);

    args.AddRange(["-c", dir, command]);

    RunTmux(args.ToArray());
    RunTmux("set-option", "-t", $"{PoolSession}:{name}", "automatic-rename", "off");

    // Remove the placeholder window if it still exists (first session created)
    RunTmux("kill-window", "-t", $"{PoolSession}:placeholder");

    await Task.CompletedTask;
}
```

- [ ] **Step 4: Implement IsInsideManager()**

```csharp
public bool IsInsideManager()
{
    var tmuxEnv = Environment.GetEnvironmentVariable("TMUX");
    if (string.IsNullOrEmpty(tmuxEnv)) return false;

    // Check if current session is ccc-manager
    var sessionName = RunTmux("display-message", "-p", "#{session_name}");
    return sessionName == ManagerSession;
}
```

- [ ] **Step 5: Implement SetupManagerSession()**

```csharp
public async Task SetupManagerSession(string executablePath, string focusKeybinding = "C-Space",
    bool mouseEnabled = true)
{
    // Check if manager already exists
    var (exists, _) = RunTmuxWithError("has-session", "-t", $"={ManagerSession}");
    if (exists) return;

    // Create manager session running CCC in the left pane
    RunTmux("new-session", "-d", "-s", ManagerSession, "-n", "main",
        "-x", Console.WindowWidth.ToString(), "-y", Console.WindowHeight.ToString(),
        executablePath);

    // Split to create right pane (session area) — starts with a placeholder shell
    RunTmux("split-window", "-t", $"{ManagerSession}:0", "-h", "-l", "75%");

    // Focus left pane (CCC nav) by default
    RunTmux("select-pane", "-t", ManagerNavPane);

    // Register focus toggle keybinding (session-scoped via conditional)
    RunTmux("bind", "-n", focusKeybinding,
        "if", "-F", $"#{{==:#{{session_name}},{ManagerSession}}}",
        "select-pane -t {last}",
        $"send-keys {focusKeybinding}");

    // Enable mouse if configured
    if (mouseEnabled)
        RunTmux("set", "-t", ManagerSession, "mouse", "on");

    // Hide tmux status bar in manager (CCC has its own)
    RunTmux("set", "-t", ManagerSession, "status", "off");

    await Task.CompletedTask;
}
```

- [ ] **Step 6: Implement AttachManagerSession()**

```csharp
public new void AttachManagerSession()
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
    catch { /* silently handle attach failures */ }
}
```

- [ ] **Step 7: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add Services/TmuxBackend.cs
git commit -m "feat: implement pool and manager setup in TmuxBackend"
```

---

### Task 5: Implement Embed/Unembed/Swap in TmuxBackend

**Files:**
- Modify: `Services/TmuxBackend.cs`

- [ ] **Step 1: Implement EmbedSession()**

```csharp
public async Task EmbedSession(string sessionName)
{
    // Move the session's pane from pool into manager's right pane position
    // First, kill the placeholder pane in manager position 1 (if it's a shell, not a session)
    var currentRight = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");

    // Join the pool window's pane into the manager
    var (success, error) = RunTmuxWithError("join-pane", "-h", "-l", "75%",
        "-s", $"{PoolSession}:{sessionName}:0.0",
        "-t", $"{ManagerSession}:0");

    if (!success) return;

    // Kill the old right pane (placeholder or previous welcome shell)
    if (!string.IsNullOrEmpty(currentRight))
        RunTmux("kill-pane", "-t", currentRight);

    // Make sure nav pane stays focused
    RunTmux("select-pane", "-t", ManagerNavPane);

    await Task.CompletedTask;
}
```

- [ ] **Step 2: Implement UnembedSession()**

```csharp
public async Task UnembedSession(string sessionName)
{
    // Move the session pane from manager back to a new window in the pool
    // The pane is currently at ManagerSessionPane

    // Create a placeholder window in pool first, then join the pane into it
    RunTmux("new-window", "-t", PoolSession, "-n", sessionName);

    // Get the pane ID of the embedded session before moving
    var paneId = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");
    if (string.IsNullOrEmpty(paneId)) return;

    // Get placeholder pane ID
    var placeholderPaneId = RunTmux("display-message", "-t", $"{PoolSession}:{sessionName}", "-p", "#{pane_id}");

    // Move the embedded pane back to pool
    RunTmux("join-pane", "-s", paneId, "-t", $"{PoolSession}:{sessionName}");

    // Kill the placeholder pane
    if (!string.IsNullOrEmpty(placeholderPaneId))
        RunTmux("kill-pane", "-t", placeholderPaneId);

    // Create a new placeholder shell in manager's right side
    RunTmux("split-window", "-t", $"{ManagerSession}:0", "-h", "-l", "75%");
    RunTmux("select-pane", "-t", ManagerNavPane);

    await Task.CompletedTask;
}
```

- [ ] **Step 3: Implement SwapEmbeddedSession()**

```csharp
public async Task SwapEmbeddedSession(string oldName, string newName)
{
    // Get current right pane ID
    var currentPaneId = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");
    if (string.IsNullOrEmpty(currentPaneId)) return;

    // Create a placeholder window in pool for the old session
    RunTmux("new-window", "-t", PoolSession, "-n", oldName);
    var placeholderPaneId = RunTmux("display-message", "-t", $"{PoolSession}:{oldName}", "-p", "#{pane_id}");

    // Move old session pane back to pool
    RunTmux("join-pane", "-s", currentPaneId, "-t", $"{PoolSession}:{oldName}");

    // Kill placeholder
    if (!string.IsNullOrEmpty(placeholderPaneId))
        RunTmux("kill-pane", "-t", placeholderPaneId);

    // Now move new session into manager — get current right pane (it's the shell that was created)
    var shellPaneId = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");

    // Join new session pane from pool
    RunTmux("join-pane", "-h", "-l", "75%",
        "-s", $"{PoolSession}:{newName}:0.0",
        "-t", $"{ManagerSession}:0");

    // Kill the shell pane that tmux created when the old one left
    if (!string.IsNullOrEmpty(shellPaneId))
        RunTmux("kill-pane", "-t", shellPaneId);

    RunTmux("select-pane", "-t", ManagerNavPane);

    await Task.CompletedTask;
}
```

- [ ] **Step 4: Implement GetEmbeddedSessionName()**

```csharp
public string? GetEmbeddedSessionName()
{
    // Check if the right pane has a CCC_SESSION_NAME env var
    var name = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_title}");
    // Fallback: check the environment variable we set at session creation
    var envName = RunTmux("show-environment", "-t", ManagerSessionPane, "CCC_SESSION_NAME");
    if (envName != null && envName.StartsWith("CCC_SESSION_NAME="))
        return envName["CCC_SESSION_NAME=".Length..];
    return null;
}
```

- [ ] **Step 5: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Services/TmuxBackend.cs
git commit -m "feat: implement embed/unembed/swap pane operations in TmuxBackend"
```

---

### Task 6: Implement Grid-in-Manager in TmuxBackend

**Files:**
- Modify: `Services/TmuxBackend.cs`

- [ ] **Step 1: Implement EmbedGridSessions()**

```csharp
public async Task EmbedGridSessions(List<string> sessionNames)
{
    if (sessionNames.Count < 2 || sessionNames.Count > 6) return;

    // Kill current right pane content (placeholder or single embedded session)
    var currentPaneId = RunTmux("display-message", "-t", ManagerSessionPane, "-p", "#{pane_id}");

    // Move first session in
    RunTmux("join-pane", "-h", "-l", "75%",
        "-s", $"{PoolSession}:{sessionNames[0]}:0.0",
        "-t", $"{ManagerSession}:0");

    // Kill old right pane
    if (!string.IsNullOrEmpty(currentPaneId))
        RunTmux("kill-pane", "-t", currentPaneId);

    // Move remaining sessions in, splitting from the right side
    for (int i = 1; i < sessionNames.Count; i++)
    {
        // Get the last pane in the manager window to split from
        var targetPane = RunTmux("display-message", "-t", $"{ManagerSession}:0.{{last}}", "-p", "#{pane_id}");

        RunTmux("join-pane", "-v",
            "-s", $"{PoolSession}:{sessionNames[i]}:0.0",
            "-t", $"{ManagerSession}:0");
    }

    // Apply tiled layout to all panes except the first (nav)
    // We need to select the layout for the right-side panes only
    // tmux applies layout to the whole window, so we use tiled and accept nav getting tiled too
    // Better approach: manually tile only the right panes
    RunTmux("select-layout", "-t", $"{ManagerSession}:0", "tiled");

    // Store manifest for crash recovery
    var manifest = string.Join(",", sessionNames.Select((name, i) => name));
    RunTmux("set-environment", "-t", ManagerSession, "CCC_GRID_SESSIONS", manifest);

    RunTmux("select-pane", "-t", ManagerNavPane);

    await Task.CompletedTask;
}
```

- [ ] **Step 2: Implement RestoreGridToSingleEmbed()**

```csharp
public async Task RestoreGridToSingleEmbed()
{
    // Read manifest
    var manifestRaw = RunTmux("show-environment", "-t", ManagerSession, "CCC_GRID_SESSIONS");
    if (manifestRaw == null || !manifestRaw.StartsWith("CCC_GRID_SESSIONS=")) return;

    var sessionNames = manifestRaw["CCC_GRID_SESSIONS=".Length..].Split(',').ToList();

    // Move all grid sessions except the first back to pool
    // List all panes in manager (skip pane 0 which is nav, keep pane 1 as the single embed)
    var paneList = RunTmux("list-panes", "-t", $"{ManagerSession}:0",
        "-F", "#{pane_index}\t#{pane_id}");

    if (paneList == null) return;

    var panes = paneList.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('\t'))
        .Where(parts => parts.Length == 2)
        .Select(parts => (Index: int.Parse(parts[0]), PaneId: parts[1]))
        .OrderByDescending(p => p.Index)  // Remove from end to avoid index shifting
        .ToList();

    // Move all panes except nav (index 0) and first session (index 1) back to pool
    foreach (var pane in panes.Where(p => p.Index > 1))
    {
        // Find which session this pane belongs to based on order
        var sessionIndex = pane.Index - 1;  // -1 for nav pane
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

    // Clean up manifest
    RunTmux("set-environment", "-u", "-t", ManagerSession, "CCC_GRID_SESSIONS");

    await Task.CompletedTask;
}
```

- [ ] **Step 3: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Services/TmuxBackend.cs
git commit -m "feat: implement grid-in-manager with pool pane management"
```

---

### Task 7: Route New Methods Through BackendRouter

**Files:**
- Modify: `Services/BackendRouter.cs`

- [ ] **Step 1: Read BackendRouter.cs to understand current routing pattern**

Read the file to see how existing methods delegate to local vs remote backends.

- [ ] **Step 2: Add pool/manager method routing**

All pool/manager methods route to the local backend only (pool is local). Add these methods to BackendRouter:

```csharp
public Task SetupPool() => _localBackend.SetupPool();

public Task CreateSessionInPool(string name, string dir, string? claudeConfigDir = null,
    bool dangerouslySkipPermissions = false, string? initialPrompt = null,
    bool shellOnly = false)
    => _localBackend.CreateSessionInPool(name, dir, claudeConfigDir,
        dangerouslySkipPermissions, initialPrompt, shellOnly);

public Task SetupManagerSession(string executablePath, string focusKeybinding = "C-Space",
    bool mouseEnabled = true)
    => _localBackend.SetupManagerSession(executablePath, focusKeybinding, mouseEnabled);

public void AttachManagerSession() => _localBackend.AttachManagerSession();

public bool IsInsideManager() => _localBackend.IsInsideManager();

public Task EmbedSession(string sessionName) => _localBackend.EmbedSession(sessionName);
public Task UnembedSession(string sessionName) => _localBackend.UnembedSession(sessionName);
public Task SwapEmbeddedSession(string oldName, string newName)
    => _localBackend.SwapEmbeddedSession(oldName, newName);
public string? GetEmbeddedSessionName() => _localBackend.GetEmbeddedSessionName();

public Task EmbedGridSessions(List<string> sessionNames)
    => _localBackend.EmbedGridSessions(sessionNames);
public Task RestoreGridToSingleEmbed() => _localBackend.RestoreGridToSingleEmbed();
```

Note: `_localBackend` is the field name for the TmuxBackend instance — verify the actual field name when reading the file.

- [ ] **Step 3: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Services/BackendRouter.cs
git commit -m "feat: route pool/manager/embed methods through BackendRouter"
```

---

### Task 8: Modify ListSessions to Query Pool Windows

Currently `ListSessions()` in TmuxBackend queries standalone tmux sessions. With the pool model, sessions live as windows in `ccc-pool`. We need to update discovery to find them there while still supporting standalone sessions during migration.

**Files:**
- Modify: `Services/TmuxBackend.cs`

- [ ] **Step 1: Read the current ListSessions() implementation**

Read `Services/TmuxBackend.cs` lines 9-41 to understand the current format string and parsing.

- [ ] **Step 2: Add a pool-aware session listing method**

Add a method that lists windows in `ccc-pool` alongside standalone sessions:

```csharp
private List<Session> ListPoolSessions()
{
    var sessions = new List<Session>();

    // Check if pool exists
    var (poolExists, _) = RunTmuxWithError("has-session", "-t", $"={PoolSession}");
    if (!poolExists) return sessions;

    var output = RunTmux("list-windows", "-t", PoolSession,
        "-F", "#{window_name}\t#{window_activity}\t#{pane_current_path}\t#{pane_dead}");

    if (output == null) return sessions;

    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = line.Split('\t');
        if (parts.Length < 4) continue;
        if (parts[0] == "placeholder") continue;  // Skip pool placeholder window

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
            IsAttached = false,  // Pool sessions are never directly attached
            WindowCount = 1,
            IsPoolSession = true,  // New flag — see Task 8 Step 3
        };

        GitService.DetectGitInfo(session);
        sessions.Add(session);
    }

    return sessions;
}
```

- [ ] **Step 3: Add IsPoolSession flag to Session model**

Read `Models/Session.cs` and add:

```csharp
public bool IsPoolSession { get; set; }
```

- [ ] **Step 4: Update ListSessions() to merge pool and standalone sessions**

Modify the existing `ListSessions()` to include pool sessions. Standalone sessions named `ccc-pool` and `ccc-manager` should be excluded from the list:

```csharp
// At the end of ListSessions(), before the return:
// Filter out internal CCC sessions
sessions.RemoveAll(s => s.Name is "ccc-pool" or "ccc-manager" or "ccc-grid");

// Add pool sessions (these take precedence — if a name exists in both, pool wins)
var poolSessions = ListPoolSessions();
var poolNames = new HashSet<string>(poolSessions.Select(s => s.Name));
sessions.RemoveAll(s => poolNames.Contains(s.Name));
sessions.AddRange(poolSessions);

// Re-sort
sessions.Sort((a, b) =>
{
    var created = a.Created.CompareTo(b.Created);
    return created != 0 ? created : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
});
```

- [ ] **Step 5: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Services/TmuxBackend.cs Models/Session.cs
git commit -m "feat: list sessions from ccc-pool windows alongside standalone sessions"
```

---

### Task 9: Modify App.cs Bootstrap to Auto-Start Manager

**Files:**
- Modify: `App.cs`
- Modify: `Program.cs`

- [ ] **Step 1: Read Program.cs and App.cs Run() to understand current bootstrap**

Read `Program.cs` (lines 1-39) and `App.cs` Run() (lines 41-121).

- [ ] **Step 2: Pass executable path to App**

In `Program.cs`, resolve the executable path and pass it to App. Add before the `new App(...)` call:

```csharp
var executablePath = Environment.ProcessPath ?? "ccc";
```

Update the `App` constructor to accept and store this:

```csharp
// Add field to App class
private readonly string _executablePath;

// Add parameter to constructor
public App(ISessionBackend backend, CccConfig config, bool mobile, string executablePath)
{
    // ... existing init ...
    _executablePath = executablePath;
}
```

Update the call in Program.cs:

```csharp
var app = new App(routedBackend, config, mobile, executablePath);
```

- [ ] **Step 3: Add bootstrap logic to App.Run()**

At the beginning of `Run()`, after the `IsAvailable()` and `IsInsideHost()` checks but before handler initialization, add:

```csharp
// Sidebar mode bootstrap: if not inside manager, set up and attach
if (!_backend.IsInsideManager())
{
    await _backend.SetupPool();
    await _backend.SetupManagerSession(_executablePath, _config.FocusKeybinding, _config.MouseEnabled);
    _backend.AttachManagerSession();
    return;  // Original process exits after user detaches from manager
}
```

Note: `Run()` is currently `void`. It needs to become `async Task` (or the async calls need `.GetAwaiter().GetResult()`). Check the existing pattern — if the codebase doesn't use async, use the synchronous pattern:

```csharp
if (!_backend.IsInsideManager())
{
    _backend.SetupPool().GetAwaiter().GetResult();
    _backend.SetupManagerSession(_executablePath, _config.FocusKeybinding, _config.MouseEnabled)
        .GetAwaiter().GetResult();
    _backend.AttachManagerSession();
    return;
}
```

- [ ] **Step 4: Auto-embed first session after MainLoop starts**

In `Run()`, after `MainLoop()` setup but before the loop begins (or at the start of `MainLoop()`), add auto-embed logic:

```csharp
// Auto-embed first local session if available
var localSessions = _state.Sessions.Where(s => s.IsPoolSession && !s.IsDead).ToList();
if (localSessions.Count > 0)
{
    var first = localSessions[0];
    _backend.EmbedSession(first.Name).GetAwaiter().GetResult();
    _state.SetEmbedded(first.Name);
}
```

- [ ] **Step 5: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add App.cs Program.cs
git commit -m "feat: auto-bootstrap manager session on startup"
```

---

### Task 10: Replace AttachSession with EmbedSession in SessionHandler

**Files:**
- Modify: `Handlers/SessionHandler.cs`

- [ ] **Step 1: Read current AttachSession and Create methods**

Read `Handlers/SessionHandler.cs` lines 18-151 (Create) and lines 337-374 (AttachSession).

- [ ] **Step 2: Add Embed method alongside AttachSession**

Add a new `Embed` method that replaces the blocking attach for local pool sessions:

```csharp
public void Embed(Session session)
{
    if (session.RemoteHostName != null || session.IsOffline)
    {
        // Remote/offline sessions fall back to blocking attach
        Attach(session);
        return;
    }

    var currentEmbedded = _state.EmbeddedSessionName;

    if (currentEmbedded == session.Name)
    {
        // Already embedded — just set status
        _state.SetStatus($"Already viewing {session.Name}");
        return;
    }

    if (currentEmbedded != null)
    {
        _backend.SwapEmbeddedSession(currentEmbedded, session.Name).GetAwaiter().GetResult();
    }
    else
    {
        _backend.EmbedSession(session.Name).GetAwaiter().GetResult();
    }

    _state.SetEmbedded(session.Name);
    _state.SetStatus($"Switched to {session.Name}");
    _loadSessions();
}
```

- [ ] **Step 3: Update Create to use pool and optionally embed**

Modify the session creation flow. After the session is created, instead of `backend.AttachSession(name)`, do:

```csharp
// Replace the existing attach call for local sessions:
// OLD: _backend.AttachSession(name);
// NEW:
if (remoteHost == null)
{
    _backend.CreateSessionInPool(name, dir, claudeConfigDir,
        effectiveSkip, initialPrompt: null, shellOnly: shellOnly).GetAwaiter().GetResult();

    if (embedAfterCreate)  // true for 'c', false for 'C'
    {
        var currentEmbedded = _state.EmbeddedSessionName;
        if (currentEmbedded != null)
            _backend.UnembedSession(currentEmbedded).GetAwaiter().GetResult();
        _backend.EmbedSession(name).GetAwaiter().GetResult();
        _state.SetEmbedded(name);
    }
}
else
{
    // Remote sessions: create via existing backend.CreateSession
    _backend.CreateSession(name, dir, claudeConfigDir, remoteHost.Name, effectiveSkip, shellOnly: shellOnly);
}
```

- [ ] **Step 4: Add embedAfterCreate parameter to Create method**

The Create method signature needs a parameter to distinguish `c` (embed) vs `C` (background):

```csharp
public void Create(bool embedAfterCreate = true)
```

- [ ] **Step 5: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Handlers/SessionHandler.cs
git commit -m "feat: replace blocking attach with embed for local sessions"
```

---

### Task 11: Update App.cs Key Dispatch for Embed and Background Create

**Files:**
- Modify: `App.cs`

- [ ] **Step 1: Read DispatchAction() to find attach and create call sites**

Read `App.cs` lines 543-709.

- [ ] **Step 2: Replace attach dispatch with embed**

In `DispatchAction()`, find where `_sessionHandler.Attach(session)` is called (for session items) and replace with:

```csharp
// OLD:
// _sessionHandler.Attach(session);
// NEW:
_sessionHandler.Embed(session);
```

This should apply to the `"attach"` action for SessionItem entries.

- [ ] **Step 3: Add background-create action**

Add a new action ID `"create-background"` to the dispatch:

```csharp
case "create-background":
    _sessionHandler.Create(embedAfterCreate: false);
    break;
```

- [ ] **Step 4: Register the keybinding for background create**

In `Services/KeyBindingService.cs`, add a default binding for `C` (shift+c):

```csharp
new KeyBinding("create-background", "Create Session (Background)", ConsoleKey.C, shift: true),
```

Read the file first to find the correct location in the defaults list.

- [ ] **Step 5: Update the grid toggle to use embed-based grid**

In `ToggleGridView()`, replace the old grid flow with the new embed grid:

```csharp
// Replace the existing grid flow:
// OLD: backend.CreateGridSession(sessionNames); backend.AttachSession("ccc-grid"); backend.RestoreFromGrid();
// NEW:
if (_state.IsEmbeddedGridMode)
{
    // Exit grid mode
    _backend.RestoreGridToSingleEmbed().GetAwaiter().GetResult();
    _state.LeaveEmbeddedGrid();
    // Re-embed the first session from the grid list
    var firstSession = gridSessions.FirstOrDefault();
    if (firstSession != null)
    {
        _backend.EmbedSession(firstSession.Name).GetAwaiter().GetResult();
        _state.SetEmbedded(firstSession.Name);
    }
}
else
{
    // Enter grid mode
    var currentEmbedded = _state.EmbeddedSessionName;
    if (currentEmbedded != null)
    {
        _backend.UnembedSession(currentEmbedded).GetAwaiter().GetResult();
        _state.SetEmbedded(null);
    }
    var names = gridSessions.Select(s => s.Name).ToList();
    _backend.EmbedGridSessions(names).GetAwaiter().GetResult();
    _state.EnterEmbeddedGrid(names);
}
```

- [ ] **Step 6: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add App.cs Services/KeyBindingService.cs
git commit -m "feat: wire embed/swap/grid into key dispatch and action routing"
```

---

### Task 12: Update Renderer for Sidebar-Only Layout

The renderer currently builds a two-panel layout: sessions (35 chars) + preview (rest). In sidebar mode, the preview panel is replaced by the live tmux pane — CCC only renders the nav panel in its own pane.

**Files:**
- Modify: `UI/Renderer.cs`

- [ ] **Step 1: Read BuildLayout() and BuildPreviewPanel()**

Read `UI/Renderer.cs` lines 19-41 (BuildLayout) and lines 254-358 (BuildPreviewPanel).

- [ ] **Step 2: Modify BuildLayout() for sidebar mode**

When a session is embedded (or in grid mode), CCC runs in the left pane only — it doesn't need to render a preview panel. The layout becomes single-column:

```csharp
public static IRenderable BuildLayout(AppState state)
{
    // If we're in sidebar mode (embedded session), render nav-only layout
    if (state.EmbeddedSessionName != null || state.IsEmbeddedGridMode)
    {
        return new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("Main"),
                new Layout("StatusBar").Size(1))
            .Update("Header", BuildHeader(state))
            .Update("Main", BuildSessionPanel(state))
            .Update("StatusBar", BuildStatusBar(state));
    }

    // Legacy layout (no embedded session, e.g., during migration or startup)
    // ... existing two-panel layout code ...
}
```

- [ ] **Step 3: Update BuildSessionPanel for narrower width**

The session panel now takes the full pane width (which is ~25-35 chars). Ensure it handles narrow widths gracefully — session names may need truncation:

Read the current `BuildSessionPanel()` and `BuildSessionRow()` methods. The panel already handles width via Spectre.Console's layout system, but verify that long session names truncate properly at narrow widths.

If needed, add truncation in `BuildSessionRow()`:

```csharp
// Truncate session name to fit available width
var maxNameWidth = Math.Max(10, Console.WindowWidth - 6);  // Leave room for icon + padding
var displayName = name.Length > maxNameWidth ? name[..maxNameWidth] : name;
```

- [ ] **Step 4: Update BuildStatusBar for sidebar mode**

The status bar hints should reflect sidebar mode keybindings. When embedded:

```csharp
// In BuildStatusBar(), when EmbeddedSessionName is set:
if (state.EmbeddedSessionName != null)
{
    hints = $"[grey]⏎ switch │ ^Space focus │ c new │ ^G grid │ q quit[/]";
}
```

- [ ] **Step 5: Remove preview pane capture for embedded sessions in MainLoop**

In `App.cs` `UpdateCapturedPane()`, skip pane capture and resize for the currently embedded local session (its pane is live, not previewed):

```csharp
// At the start of UpdateCapturedPane():
// Skip capture for the embedded session — it's a live tmux pane
var selected = _state.GetSelectedSession();
if (selected != null && selected.Name == _state.EmbeddedSessionName && selected.RemoteHostName == null)
{
    // Still detect waiting-for-input state (needed for status indicators)
    // but skip pane content capture and resize
}
```

- [ ] **Step 6: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add UI/Renderer.cs App.cs
git commit -m "feat: sidebar-only layout when session is embedded"
```

---

### Task 13: Handle Session Deletion When Embedded

**Files:**
- Modify: `Handlers/SessionHandler.cs`

- [ ] **Step 1: Read current Delete method**

Read the session deletion flow in SessionHandler.

- [ ] **Step 2: Update Delete to handle embedded session**

When deleting a session that is currently embedded, unembed it first and auto-embed the next session:

```csharp
// In the Delete method, before killing the session:
if (_state.EmbeddedSessionName == session.Name)
{
    _backend.UnembedSession(session.Name).GetAwaiter().GetResult();
    _state.SetEmbedded(null);
}

// After killing and reloading sessions:
if (_state.EmbeddedSessionName == null)
{
    // Auto-embed next available session
    var nextSession = _state.Sessions.FirstOrDefault(s => s.IsPoolSession && !s.IsDead);
    if (nextSession != null)
    {
        _backend.EmbedSession(nextSession.Name).GetAwaiter().GetResult();
        _state.SetEmbedded(nextSession.Name);
    }
}
```

- [ ] **Step 3: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Handlers/SessionHandler.cs
git commit -m "feat: handle deletion of embedded session with auto-embed fallback"
```

---

### Task 14: Handle Session Rename When Embedded

**Files:**
- Modify: `Handlers/SessionHandler.cs`

- [ ] **Step 1: Update rename flow for embedded session**

When renaming a session that's embedded, update the embedded state:

```csharp
// In the Edit/Rename method, after successful rename:
if (_state.EmbeddedSessionName == oldName)
{
    _state.SetEmbedded(newName);
}
```

Also rename the window in the pool:

```csharp
// The existing RenameSession backend call handles the tmux rename.
// For pool sessions, the window name in ccc-pool also needs updating.
// This is already handled by RenameSession if it renames the window.
// Verify this works by testing.
```

- [ ] **Step 2: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Handlers/SessionHandler.cs
git commit -m "feat: update embedded state on session rename"
```

---

### Task 15: Migration — Import Standalone Sessions into Pool

When CCC starts and finds standalone tmux sessions (not in pool), it should offer to import them.

**Files:**
- Modify: `Services/TmuxBackend.cs`

- [ ] **Step 1: Add migration method to TmuxBackend**

```csharp
public async Task MigrateStandaloneSessionsToPool(List<Session> standaloneSessions)
{
    // Ensure pool exists
    await SetupPool();

    foreach (var session in standaloneSessions)
    {
        if (session.IsPoolSession) continue;
        if (session.Name is "ccc-pool" or "ccc-manager" or "ccc-grid") continue;
        if (session.RemoteHostName != null) continue;

        // Move the standalone session's pane into the pool as a window
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
```

- [ ] **Step 2: Call migration in App.Run() after bootstrap**

In `App.cs`, after the `IsInsideManager()` check and before `MainLoop()`, add:

```csharp
// Migrate standalone sessions to pool on first run
var standaloneSessions = _state.Sessions.Where(s => !s.IsPoolSession && s.RemoteHostName == null).ToList();
if (standaloneSessions.Count > 0)
{
    _backend.MigrateStandaloneSessionsToPool(standaloneSessions).GetAwaiter().GetResult();
    _state.Sessions = _backend.ListSessions();
}
```

- [ ] **Step 3: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Services/TmuxBackend.cs App.cs
git commit -m "feat: auto-migrate standalone tmux sessions into pool on first run"
```

---

### Task 16: Crash Recovery — Reconnect to Existing Manager

**Files:**
- Modify: `App.cs`
- Modify: `Services/TmuxBackend.cs`

- [ ] **Step 1: Add manager existence check**

In TmuxBackend, add:

```csharp
public bool ManagerSessionExists()
{
    var (exists, _) = RunTmuxWithError("has-session", "-t", $"={ManagerSession}");
    return exists;
}
```

- [ ] **Step 2: Update bootstrap to reconnect if manager exists**

In `App.Run()`, update the bootstrap logic:

```csharp
if (!_backend.IsInsideManager())
{
    _backend.SetupPool().GetAwaiter().GetResult();

    if (_backend.ManagerSessionExists())
    {
        // Manager exists from previous run — just reattach
        _backend.AttachManagerSession();
    }
    else
    {
        _backend.SetupManagerSession(_executablePath, _config.FocusKeybinding, _config.MouseEnabled)
            .GetAwaiter().GetResult();
        _backend.AttachManagerSession();
    }
    return;
}
```

Note: `ManagerSessionExists()` should be added to `ISessionBackend` with default `=> false` and routed through `BackendRouter`.

- [ ] **Step 3: Verify the project builds**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add App.cs Services/TmuxBackend.cs Services/ISessionBackend.cs Services/BackendRouter.cs
git commit -m "feat: reconnect to existing manager session on restart"
```

---

### Task 17: Update README Documentation

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Read the current README**

Read `README.md` to understand its structure and what sections need updating.

- [ ] **Step 2: Update the README**

Add/update sections covering:

- **Sidebar Mode** as the new default behavior — always-visible nav + interactive session pane
- **Focus switching** — `Ctrl+Space` (configurable) and mouse click
- **Session creation** — `c` creates and switches, `C` creates in background
- **Grid view** — now keeps sidebar visible
- **Config options** — `focusKeybinding`, `mouseEnabled`
- **Migration** — standalone sessions auto-imported on first run
- Update the keybindings table with new entries

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: update README for sidebar mode"
```

---

### Task 18: Manual Testing Checklist

No code changes — this is a verification task.

- [ ] **Step 1: Test startup bootstrap**

Run `ccc` from a plain terminal (not inside tmux).
Expected: CCC creates `ccc-pool` and `ccc-manager`, attaches to manager. Left pane shows CCC nav, right pane shows placeholder shell or first session.

- [ ] **Step 2: Test session creation with embed**

Press `c`, create a new session.
Expected: Session created in pool, embedded in right pane. Nav shows the session with `←` indicator.

- [ ] **Step 3: Test session creation in background**

Press `C`, create a new session.
Expected: Session created in pool but NOT embedded. Current session stays in right pane.

- [ ] **Step 4: Test session switching**

Navigate to a different session, press Enter.
Expected: Right pane swaps instantly to the new session. Old session returns to pool.

- [ ] **Step 5: Test focus switching**

Press `Ctrl+Space`.
Expected: Focus moves to right pane (session is interactive). Press `Ctrl+Space` again — focus returns to nav.

- [ ] **Step 6: Test grid mode**

Navigate to a group with multiple sessions, press `Ctrl+G`.
Expected: Right side splits into multiple panes. Sidebar remains. `Ctrl+G` again restores single pane.

- [ ] **Step 7: Test session deletion**

Delete the currently embedded session.
Expected: Session killed, next session auto-embedded.

- [ ] **Step 8: Test crash recovery**

Kill the CCC process (Ctrl+C in the nav pane). Run `ccc` again.
Expected: Reattaches to existing manager. Pool sessions still alive.

- [ ] **Step 9: Test remote session fallback**

If remote hosts configured, navigate to a remote session and press Enter.
Expected: Blocking attach as before — sidebar disappears, returns on detach.

- [ ] **Step 10: Test migration**

Create a standalone tmux session (`tmux new -d -s test-migrate`). Start CCC.
Expected: Standalone session imported into pool automatically.
