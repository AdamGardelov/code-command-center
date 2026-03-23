# Native tmux Grid + Drop Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace screenshot-based grid with native tmux split panes and remove Windows/ConPTY support.

**Architecture:** Grid mode uses `tmux join-pane` to move session panes into a temporary `ccc-grid` session, attaches the user directly (real PTY), and restores panes on detach. Windows support is removed entirely — only tmux-based backends remain.

**Tech Stack:** .NET 10, tmux, Spectre.Console

**Spec:** `docs/superpowers/specs/2026-03-23-native-tmux-grid-design.md`

---

### Task 1: Delete ConPTY/Windows files

Remove all Windows-only files that are no longer needed.

**Files:**
- Delete: `Services/ConPty/ConPtyBackend.cs`
- Delete: `Services/ConPty/ConPtySession.cs`
- Delete: `Services/ConPty/VtScreenBuffer.cs`
- Delete: `Services/ConPty/NativeMethods.cs`
- Delete: `Services/RingBuffer.cs`
- Delete: `install.ps1`
- Delete: `docs/WSL2-SETUP.md`

- [ ] **Step 1: Delete the files**

```bash
rm -rf Services/ConPty/
rm Services/RingBuffer.cs
rm install.ps1
rm docs/WSL2-SETUP.md
```

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build`
Expected: Build succeeded, 0 errors. If there are errors, they will be from references to deleted types — those are fixed in Task 2.

- [ ] **Step 3: Commit**

```bash
git add -u
git commit -m "refactor!: remove ConPTY/Windows support files"
```

---

### Task 2: Remove Windows code paths from remaining files

Strip all `OperatingSystem.IsWindows()` branches and ConPTY references from the codebase.

**Files:**
- Modify: `Program.cs` — remove ConPTY import and platform branch
- Modify: `App.cs:786-788` — simplify quit message (remove Windows branch)
- Modify: `App.cs:1061-1063` — simplify mobile quit message (remove Windows branch)
- Modify: `App.cs:1141-1163` — remove Windows update path (PowerShell), keep only bash
- Modify: `Services/NotificationService.cs:51` — remove `if (!OperatingSystem.IsWindows())` guard, always call `SendTmuxDisplayMessage`
- Modify: `Services/SshControlMasterService.cs:201` — remove `if (!OperatingSystem.IsWindows())` guard around chmod, always run chmod
- Modify: `Services/SshService.cs:99-109` — remove `cmd`/Windows branch, always use `/bin/sh`
- Modify: `Handlers/FlowHelper.cs:288-289` — remove `explorer` branch
- Modify: `Handlers/SettingsHandler.cs:372-373` — remove `explorer` from ternary
- Modify: `.github/workflows/release.yml:73-74` — remove `win-x64` matrix entry

- [ ] **Step 1: Fix Program.cs**

Remove `using CodeCommandCenter.Services.ConPty;` and replace the platform detection with a direct `TmuxBackend`:

```csharp
// Before:
ISessionBackend localBackend = OperatingSystem.IsWindows()
    ? new ConPtyBackend()
    : new TmuxBackend();

// After:
var localBackend = new TmuxBackend();
```

Also remove the `using CodeCommandCenter.Services.ConPty;` import at the top.

- [ ] **Step 2: Fix App.cs quit messages**

Two locations. Replace Windows-aware ternary with simple string:

```csharp
// Line ~786 (DispatchAction, case "quit"):
// Before:
var quitMsg = OperatingSystem.IsWindows() && activeCount > 0
    ? $"Quit? This will terminate {activeCount} active session(s). (y/n)"
    : "Quit? (y/n)";

// After:
var quitMsg = "Quit? (y/n)";
```

Same pattern at line ~1061 (DispatchMobileAction, case "quit"):
```csharp
// Before:
var mobileQuitMsg = OperatingSystem.IsWindows() && mobileActiveCount > 0
    ? $"Quit? This will terminate {mobileActiveCount} active session(s). (y/n)"
    : "Quit? (y/n)";

// After:
var mobileQuitMsg = "Quit? (y/n)";
```

Remove the now-unused `activeCount`/`mobileActiveCount` variables if they're no longer referenced.

- [ ] **Step 3: Fix App.cs RunUpdate()**

Remove the entire `if (OperatingSystem.IsWindows())` branch (PowerShell irm). Keep only the bash/curl path:

```csharp
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
```

- [ ] **Step 4: Fix NotificationService.cs**

Remove the `if (!OperatingSystem.IsWindows())` guard. Always call `SendTmuxDisplayMessage`:

```csharp
// Before:
if (!OperatingSystem.IsWindows())
    SendTmuxDisplayMessage($"⏳ {message}");

// After:
SendTmuxDisplayMessage($"⏳ {message}");
```

- [ ] **Step 5: Fix SshControlMasterService.cs**

Remove the `if (!OperatingSystem.IsWindows())` guard around chmod. Always run it:

```csharp
// Before:
if (!OperatingSystem.IsWindows())
{
    var chmodInfo = ...
}

// After (remove the if, keep the body):
var chmodInfo = new ProcessStartInfo { FileName = "chmod", UseShellExecute = false };
chmodInfo.ArgumentList.Add("700");
chmodInfo.ArgumentList.Add(_socketDir);
Process.Start(chmodInfo)?.WaitForExit(1000);
```

- [ ] **Step 6: Fix SshService.cs**

Replace the Windows/Linux shell branching with always using `/bin/sh`:

```csharp
// Before:
FileName = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
// ...
if (OperatingSystem.IsWindows())
{
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add(command);
}
else
{
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add(command);
}

// After:
FileName = "/bin/sh",
// ...
startInfo.ArgumentList.Add("-c");
startInfo.ArgumentList.Add(command);
```

- [ ] **Step 7: Fix FlowHelper.cs and SettingsHandler.cs**

FlowHelper.cs — remove the Windows branch from the folder opener detection:
```csharp
// Before:
if (OperatingSystem.IsWindows())
    return "explorer";

// After: delete these two lines entirely
```

SettingsHandler.cs — simplify the ternary:
```csharp
// Before:
var opener = OperatingSystem.IsMacOS() ? "open" :
    OperatingSystem.IsWindows() ? "explorer" : "xdg-open";

// After:
var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
```

- [ ] **Step 8: Fix release.yml**

Remove the `win-x64` matrix entry:

```yaml
# Delete these two lines:
          - rid: win-x64
            os: windows-latest
```

- [ ] **Step 9: Build and verify**

Run: `dotnet build`
Expected: Build succeeded, 0 errors, 0 warnings

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "refactor!: remove all Windows/ConPTY code paths"
```

---

### Task 3: Remove screenshot-based grid rendering

Delete the grid rendering code from Renderer.cs, grid-specific state from AppState.cs, and the `Grid` ViewMode enum value.

**Files:**
- Modify: `UI/Renderer.cs` — remove `BuildGridLayout`, `BuildGridCell`, `BuildGridStatusBar`, `BuildGroupGridStatusBar`, grid branch in `BuildLayout`
- Modify: `UI/AppState.cs` — remove `GetGridDimensions`, `GetGridCellOutputLines`, grid branch in `ClampCursor`, rewrite `EnterGroupGrid`/`LeaveGroupGrid`
- Modify: `Enums/ViewMode.cs` — remove `Grid` value

- [ ] **Step 1: Remove grid methods from Renderer.cs**

Delete these entire methods:
- `BuildGridLayout` (line ~477-518)
- `BuildGridCell` (line ~520-615)
- `BuildGridStatusBar` (line ~629-638)
- `BuildGroupGridStatusBar` (line ~617-627)

In `BuildLayout`, remove the `ViewMode.Grid` branch (lines ~33-48). The method should go straight from the header to the list/preview layout:

```csharp
// Before:
layout["Header"].Update(BuildHeader(state));

if (state.ViewMode == ViewMode.Grid)
{
    var (cols, _) = state.GetGridDimensions();
    if (cols == 0)
    {
        state.ViewMode = ViewMode.List;
    }
    else
    {
        layout["Main"].Update(BuildGridLayout(state, allCapturedPanes));
        layout["StatusBar"].Update(...);
        return layout;
    }
}

// After:
layout["Header"].Update(BuildHeader(state));
```

Also remove the `allCapturedPanes` parameter from `BuildLayout` since it's no longer used:

```csharp
// Before:
public static IRenderable BuildLayout(AppState state, string? capturedPane,
    Dictionary<string, string>? allCapturedPanes = null)

// After:
public static IRenderable BuildLayout(AppState state, string? capturedPane)
```

- [ ] **Step 2: Remove grid state from AppState.cs**

Delete these methods:
- `GetGridDimensions()` (line ~258-270)
- `GetGridCellOutputLines()` (line ~276-288)

In `ClampCursor()`, remove the `ViewMode.Grid` branch:
```csharp
// Before:
if (ViewMode == ViewMode.Grid)
{
    var grid = GetGridSessions();
    CursorIndex = grid.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, grid.Count - 1);
    return;
}

// After: delete this entire if block
```

In `GetSelectedSession()`, remove the `ViewMode.Grid` branch:
```csharp
// Before:
if (ViewMode == ViewMode.Grid)
{
    var gridSessions = GetGridSessions();
    if (CursorIndex >= 0 && CursorIndex < gridSessions.Count)
        return gridSessions[CursorIndex];
    return null;
}

// After: delete this entire if block
```

Rewrite `EnterGroupGrid` — only sets `ActiveGroup`, no ViewMode change:
```csharp
public void EnterGroupGrid(string groupName)
{
    _savedCursorIndex = CursorIndex;
    ActiveGroup = groupName;
}
```

Rewrite `LeaveGroupGrid` — only clears `ActiveGroup`:
```csharp
public void LeaveGroupGrid()
{
    ActiveGroup = null;
    CursorIndex = _savedCursorIndex;
    ClampCursor();
}
```

Keep `GetGridSessions()` — it's reused by the new native grid logic.

- [ ] **Step 3: Remove Grid from ViewMode enum**

```csharp
// Before:
public enum ViewMode
{
    List,
    Grid,
    Settings,
    DiffOverlay,
}

// After:
public enum ViewMode
{
    List,
    Settings,
    DiffOverlay,
}
```

- [ ] **Step 4: Build to identify remaining ViewMode.Grid references**

Run: `dotnet build`
Expected: Compile errors from remaining `ViewMode.Grid` references in App.cs and SessionHandler.cs. These will be fixed in the next steps.

- [ ] **Step 5: Commit (may not build yet — grid removal in App.cs is Task 4)**

```bash
git add -u
git commit -m "refactor: remove screenshot-based grid rendering and ViewMode.Grid"
```

---

### Task 4: Remove grid key handling and capture logic from App.cs

Strip the old grid input forwarding, pane capture, and resize logic.

**Files:**
- Modify: `App.cs` — remove grid fields, methods, and main loop grid paths
- Modify: `Handlers/SessionHandler.cs:354-363` — remove grid-exclude clamp logic

- [ ] **Step 1: Remove grid fields**

Delete these field declarations from the top of App.cs:
```csharp
private Dictionary<string, string> _allCapturedPanes = new();
private bool _gridKeyForwarded;
private readonly List<ConsoleKeyInfo> _gridKeyBatch = [];
private DateTime _lastGridActivity = DateTime.MinValue;
```

- [ ] **Step 2: Remove grid methods**

Delete these entire methods:
- `HandleGridKey()` (~line 848-918)
- `FlushGridKeyBatch()` (~line 919-940)
- `MoveGridCursor()` (~line 959-923)
- `UpdateActiveGridPane()` (~line 495-511)
- `UpdateAllCapturedPanes()` (~line 513-534)
- `ResizeGridPanes()` (~line 537-558)

- [ ] **Step 3: Simplify MainLoop**

In the key drain loop, remove the `FlushGridKeyBatch()` call and the `_gridKeyForwarded` check:

```csharp
// Before:
while (Console.KeyAvailable)
{
    var key = Console.ReadKey(true);
    HandleKey(key);
}

FlushGridKeyBatch();

if (_gridKeyForwarded)
{
    _gridKeyForwarded = false;
    _lastGridActivity = DateTime.UtcNow;
}
else
{
    Render();
}

// After:
while (Console.KeyAvailable)
{
    var key = Console.ReadKey(true);
    HandleKey(key);
}

Render();
```

Remove the `isActiveGridTyping` logic from the capture interval section:

```csharp
// Before:
var isActiveGridTyping = _state.ViewMode == ViewMode.Grid
    && (DateTime.UtcNow - _lastGridActivity).TotalMilliseconds < 1000;
var captureInterval = isActiveGridTyping ? 80 : 500;

if (_state.ViewMode != ViewMode.Settings && _state.ViewMode != ViewMode.DiffOverlay
    && (DateTime.Now - _lastCapture).TotalMilliseconds > captureInterval)
{
    if (isActiveGridTyping)
    {
        if (UpdateActiveGridPane())
            Render();
    }
    else
    {
        ...
    }
}

Thread.Sleep(hadInput || isActiveGridTyping ? 5 : 30);

// After:
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
```

- [ ] **Step 4: Remove grid branches from HandleKey**

Remove the `ViewMode.Grid` dispatch:

```csharp
// Before:
if (_state.ViewMode == ViewMode.Grid)
{
    HandleGridKey(key);
    return;
}

// After: delete this entire block
```

- [ ] **Step 5: Remove grid check from UpdateCapturedPane**

```csharp
// Before:
if (_state.ViewMode == ViewMode.Grid)
    return UpdateAllCapturedPanes();

// After: delete these two lines
```

- [ ] **Step 6: Remove allCapturedPanes from Render call**

```csharp
// Before:
AnsiConsole.Write(Renderer.BuildLayout(_state, _capturedPane, _allCapturedPanes));

// After:
AnsiConsole.Write(Renderer.BuildLayout(_state, _capturedPane));
```

- [ ] **Step 7: Fix SessionHandler.cs ToggleExclude**

The grid-exclude logic at line ~354 references `ViewMode.Grid`. Replace with a simple status message since grid is no longer a view mode:

```csharp
// Before:
if (state.ViewMode == ViewMode.Grid && session.IsExcluded)
{
    var gridSessions = state.GetGridSessions();
    if (gridSessions.Count < 2)
        state.ViewMode = ViewMode.List;
    else
        state.CursorIndex = Math.Clamp(state.CursorIndex, 0, gridSessions.Count - 1);
}

var label = session.IsExcluded ? "Excluded from grid" : "Restored to grid";

// After: remove the if block, simplify the label
var label = session.IsExcluded ? "Excluded" : "Restored";
```

- [ ] **Step 8: Build and verify**

Run: `dotnet build`
Expected: Build succeeded, 0 errors

- [ ] **Step 9: Commit**

```bash
git add -u
git commit -m "refactor: remove grid key forwarding and capture logic from App"
```

---

### Task 5: Add grid methods to TmuxBackend

Implement the native tmux grid lifecycle on `TmuxBackend`.

**Files:**
- Modify: `Services/ISessionBackend.cs` — add grid method defaults
- Modify: `Services/TmuxBackend.cs` — implement grid methods

- [ ] **Step 1: Add grid interface methods to ISessionBackend**

Add default implementations (no-ops) so `RemoteTmuxBackend` and `BackendRouter` don't need changes:

```csharp
// Add to ISessionBackend interface:

// Grid mode — native tmux split panes
string? CreateGridSession(List<string> sessionNames) => "Grid not supported";
void RestoreFromGrid() { }
bool GridSessionExists() => false;
List<string>? GetGridSessionManifest() => null;
```

- [ ] **Step 2: Implement CreateGridSession on TmuxBackend**

```csharp
public string? CreateGridSession(List<string> sessionNames)
{
    // Clean up any leftover grid session from a previous crash
    if (GridSessionExists())
        RestoreFromGrid();

    // Create the grid session with a throwaway shell
    var (createOk, createErr) = RunTmuxWithError("new-session", "-d", "-s", "ccc-grid");
    if (!createOk)
        return createErr ?? "Failed to create grid session";

    // Store the session manifest for crash recovery
    RunTmux("set-environment", "-t", "ccc-grid", "CCC_GRID_SESSIONS",
        string.Join(",", sessionNames));

    // Record the initial empty pane's ID so we can kill it after joining session panes
    var initialPaneId = RunTmux("display-message", "-t", "ccc-grid:0", "-p", "#{pane_id}");

    // Move each session's pane into the grid window.
    // join-pane moves the pane, leaving the source session empty (tmux auto-kills it).
    foreach (var name in sessionNames)
    {
        RunTmux("join-pane", "-d", "-s", $"{name}:0.0", "-t", "ccc-grid:0");
    }

    // Kill the initial empty pane that was created with new-session
    if (initialPaneId != null)
        RunTmux("kill-pane", "-t", initialPaneId);

    // Apply tiled layout
    RunTmux("select-layout", "-t", "ccc-grid:0", "tiled");

    // Bind Ctrl+G to detach from the grid session
    RunTmux("bind-key", "-T", "root", "C-g", "detach-client");

    return null;
}
```

- [ ] **Step 3: Implement RestoreFromGrid on TmuxBackend**

**Important:** When `join-pane` moves the only pane out of a session, tmux automatically
kills the now-empty source session. So the original sessions no longer exist when restoring.
We must use `break-pane` without a `-t` target, which creates a new session from the pane,
then rename that session back to its original name.

Also, `bind-key -T root` is server-wide (not session-scoped), so we must `unbind-key` after
grid teardown to avoid clobbering user keybindings.

```csharp
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
        var sessionIndex = 0;

        foreach (var line in paneLines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;

            var paneId = parts[0];
            var isDead = parts[1] == "1";

            if (isDead || sessionIndex >= manifest.Count)
            {
                sessionIndex++;
                continue;
            }

            var originalSession = manifest[sessionIndex];

            // break-pane without -t creates a new session from this pane.
            // The original session was destroyed when join-pane moved its only pane out.
            RunTmux("break-pane", "-d", "-s", paneId);

            // break-pane creates a session with an auto-generated name.
            // The pane ends up in the newest session. Find it by pane ID.
            var newSession = RunTmux("display-message", "-t", paneId, "-p", "#{session_name}");
            if (newSession != null && newSession != originalSession)
                RunTmux("rename-session", "-t", newSession, originalSession);

            sessionIndex++;
        }
    }

    // Unbind Ctrl+G — bind-key is server-wide, not session-scoped
    RunTmux("unbind-key", "-T", "root", "C-g");

    // Kill the grid session (cleans up dead panes)
    RunTmux("kill-session", "-t", "ccc-grid");
}
```

- [ ] **Step 4: Implement GridSessionExists and GetGridSessionManifest**

```csharp
public bool GridSessionExists()
{
    var result = RunTmux("has-session", "-t", "ccc-grid");
    return result != null;
}

public List<string>? GetGridSessionManifest()
{
    var output = RunTmux("show-environment", "-t", "ccc-grid", "CCC_GRID_SESSIONS");
    if (output == null)
        return null;

    // Output format: "CCC_GRID_SESSIONS=session1,session2,session3"
    var eqIdx = output.IndexOf('=');
    if (eqIdx < 0)
        return null;

    var value = output[(eqIdx + 1)..];
    return value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build`
Expected: Build succeeded, 0 errors

- [ ] **Step 6: Commit**

```bash
git add Services/ISessionBackend.cs Services/TmuxBackend.cs
git commit -m "feat: add native tmux grid session lifecycle methods"
```

---

### Task 6: Add grid methods to BackendRouter

Route grid operations to the local backend (grid is local-only).

**Files:**
- Modify: `Services/BackendRouter.cs` — add grid method pass-through

- [ ] **Step 1: Add grid method delegation**

Grid mode only works with local tmux. Add pass-through methods:

```csharp
public string? CreateGridSession(List<string> sessionNames) =>
    local.CreateGridSession(sessionNames);

public void RestoreFromGrid() => local.RestoreFromGrid();

public bool GridSessionExists() => local.GridSessionExists();

public List<string>? GetGridSessionManifest() => local.GetGridSessionManifest();
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Services/BackendRouter.cs
git commit -m "feat: route grid operations through BackendRouter to local backend"
```

---

### Task 7: Rewrite ToggleGridView to use native tmux grid

Replace the screenshot grid orchestration with native tmux attach/detach.

**Files:**
- Modify: `App.cs` — rewrite `ToggleGridView()`, add crash recovery to `Run()`

- [ ] **Step 1: Rewrite ToggleGridView**

```csharp
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

    // Create the grid session with native tmux panes
    var error = backend.CreateGridSession(sessionNames);
    if (error != null)
    {
        if (groupName != null)
            _state.LeaveGroupGrid();
        _state.SetStatus(error);
        return;
    }

    // Attach to the grid session — blocks CCC main loop
    backend.AttachSession("ccc-grid");

    // User has detached — restore panes to original sessions
    backend.RestoreFromGrid();

    if (groupName != null)
        _state.LeaveGroupGrid();

    _lastSelectedSession = null;
    LoadSessions();
    Render();
}
```

- [ ] **Step 2: Remove the old ToggleGridView entirely first**

Before adding the new one, delete the existing `ToggleGridView` method completely, then paste the new implementation from Step 1.

- [ ] **Step 3: Add crash recovery to Run()**

In the `Run()` method, after `_claudeAvailable` check and before `LoadSessions()`, add:

```csharp
// Recover from a previous crash that left sessions in a grid
if (backend.GridSessionExists())
{
    backend.RestoreFromGrid();
}
```

This goes around line 80 in `Run()`, just before `LoadSessions()`.

- [ ] **Step 4: Remove grid-related keybinding references**

In `HandleKey()`, the grid check was already removed in Task 4. Verify that `DispatchAction` still has the `"toggle-grid"` case pointing to `ToggleGridView()`.

Check that `KeyBindingService.Defaults` still has the `toggle-grid` action mapped to `Ctrl+G`.

- [ ] **Step 5: Build and verify**

Run: `dotnet build`
Expected: Build succeeded, 0 errors

- [ ] **Step 6: Commit**

```bash
git add App.cs
git commit -m "feat: rewrite grid mode to use native tmux split panes"
```

---

### Task 8: Manual testing

Verify the full grid lifecycle works correctly.

- [ ] **Step 1: Build release binary**

```bash
dotnet build
```

- [ ] **Step 2: Test basic grid — 2 sessions**

1. Start CCC
2. Create 2 sessions
3. Press `Ctrl+G`
4. Verify: tmux splits into 2 panes, each showing a real Claude Code session
5. Type in one pane — verify real-time response
6. Switch panes with `Ctrl+b` + arrow keys
7. Press `Ctrl+G` — verify return to CCC list view
8. Verify both sessions are back and working normally

- [ ] **Step 3: Test grid with group**

1. Create a group with 3 sessions
2. Navigate to group header
3. Press `Ctrl+G`
4. Verify: only group sessions appear in grid
5. Exit with `Ctrl+G`
6. Verify group sessions restored

- [ ] **Step 4: Test session death in grid**

1. Enter grid with 2+ sessions
2. Kill one Claude Code session (e.g., type `/exit`)
3. Verify dead pane shows exit message
4. Press `Ctrl+G` to exit
5. Verify live sessions restored, dead session handled

- [ ] **Step 5: Test crash recovery**

1. Enter grid with 2 sessions
2. Kill the CCC process (e.g., `kill -9 <pid>` from another terminal)
3. Restart CCC
4. Verify sessions were automatically restored from the grid

- [ ] **Step 6: Test edge cases**

- Try grid with only 1 session → status message
- Try grid with 7+ sessions → status message
- Try grid when all sessions are remote → status message
- Try grid with mix of local and remote → only local sessions in grid

---

### Task 9: Update documentation

Update README.md and CLAUDE.md to reflect the changes.

**Files:**
- Modify: `README.md` — remove Windows sections, update grid description
- Modify: `CLAUDE.md` — remove ConPTY from architecture, update grid description

- [ ] **Step 1: Update CLAUDE.md**

Remove ConPTY from the Session Backend table:
```
| Backend        | Platform      | Session persistence                        |
|----------------|---------------|--------------------------------------------|
| `TmuxBackend`  | Linux / macOS | Persistent (tmux server survives CCC exit) |
```

Remove any ConPTY/Windows references from the Architecture section. Remove `RingBuffer` from the Services table.

Update the Grid view description in Key Patterns:
```
**Grid view**: Ctrl+G creates a temporary `ccc-grid` tmux session using `join-pane` to move session panes
into a tiled layout. User interacts with real tmux panes. On exit (Ctrl+G or detach), panes are restored
to their original sessions. Max 6 sessions. Crash recovery via tmux environment variable manifest.
```

- [ ] **Step 2: Update README.md**

Remove Windows install/setup sections. Update grid documentation to describe native tmux panes. Remove any ConPTY/ephemeral session references.

- [ ] **Step 3: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: update for native tmux grid and Windows removal"
```
