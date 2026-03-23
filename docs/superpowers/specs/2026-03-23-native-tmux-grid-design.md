# Native tmux Grid Mode + Drop Windows Support

**Date:** 2026-03-23
**Status:** Approved

## Problem

The current grid mode captures tmux pane content as screenshots and renders them through Spectre.Console, forwarding keystrokes via `tmux send-keys`. This creates:

- Input lag (keystrokes proxied through CCC → tmux, not direct PTY)
- Lossy key translation (ConsoleKeyInfo → tmux key names, edge cases break)
- Delayed visual feedback (polling-based capture, not real-time)
- Complex code (~200 lines of grid rendering + key batching + fast-path capture)

Users want the feel of Ghostty/terminal splits — real interactive panes, zero lag.

## Solution

Replace the screenshot-based grid with **native tmux split panes**. When entering grid mode, CCC creates a temporary tmux session with split panes, each pane being the actual session's pane moved via `tmux join-pane`. The user interacts directly with real terminal panes. CCC's main loop pauses (same as single-session attach).

### Grid lifecycle

**Entering grid mode (Ctrl+G from CCC list view):**

1. Determine eligible sessions (context-aware: group sessions if on a group, standalone sessions otherwise)
2. Enforce max 6 sessions. If over, show status "Too many sessions for grid (max 6)"
3. Create temporary tmux session: `tmux new-session -d -s ccc-grid`
4. Move first session's pane: the first pane already exists in `ccc-grid`, so use `tmux swap-pane` or respawn it. Simpler approach: kill the default empty pane after joining all session panes.
   - For each session: `tmux join-pane -s <session-name>:0.0 -t ccc-grid:0 -d`
   - Remove the initial empty pane from `ccc-grid`
5. Apply tiled layout: `tmux select-layout -t ccc-grid tiled`
6. Bind exit key: `tmux bind-key -T root C-g detach-client` (scoped to `ccc-grid` session)
7. Record which session names are in the grid (for restoration)
8. Attach: `tmux attach-session -t ccc-grid` (blocks CCC main loop)

**Exiting grid mode (Ctrl+G inside grid, or tmux detach):**

1. CCC resumes after attach process exits
2. For each session that was moved into the grid:
   - Check if the pane is still alive in `ccc-grid`
   - If alive: `tmux break-pane -s ccc-grid:<window>.<pane> -t <original-session>:` to move it back
   - If dead: skip (session exited while in grid)
3. Kill the `ccc-grid` session: `tmux kill-session -t ccc-grid`
4. Unbind the custom key (session is dead, so this is automatic)
5. Resume CCC main loop, refresh session list

**If a session dies while in grid:**

The dead pane stays visible in tmux with its exit message. When the user exits the grid (Ctrl+G), CCC cleans up: live panes are moved back, dead panes are discarded, and the grid session is killed.

### Crash safety

If CCC crashes while sessions are in the grid:
- Sessions' panes are still alive inside `ccc-grid` — they're real tmux panes, not proxied
- On next CCC startup, detect if `ccc-grid` session exists
- If it does: break all panes back to their original sessions (session names are stored as a tmux environment variable on `ccc-grid`), then kill `ccc-grid`

Store the session manifest as a tmux environment variable:
```
tmux set-environment -t ccc-grid CCC_GRID_SESSIONS "session1,session2,session3"
```

On startup, if `ccc-grid` exists:
```
tmux show-environment -t ccc-grid CCC_GRID_SESSIONS
```
Parse and restore.

### Session selection (context-aware)

Same logic as current `ToggleGridView`:
- Cursor on grouped session or group header → grid that group's sessions
- Cursor on standalone session → grid all standalone non-excluded sessions
- Max 6 sessions enforced

### Key binding

- `Ctrl+G` inside the grid triggers detach (set via `tmux bind-key` before attach)
- This binding is scoped to the `ccc-grid` session and dies with it
- User can still use standard `Ctrl+b d` to detach (also works)
- User navigates between panes with standard tmux pane navigation (`Ctrl+b` + arrow keys, or whatever their tmux.conf defines)

## Drop Windows/ConPTY Support

### Removed files

- `Services/ConPty/ConPtyBackend.cs`
- `Services/ConPty/ConPtySession.cs`
- `Services/ConPty/VtScreenBuffer.cs`
- `Services/ConPty/NativeMethods.cs`
- `Services/RingBuffer.cs` (only used by ConPTY)
- `install.ps1`
- `docs/WSL2-SETUP.md`

### Removed code paths

- `Program.cs`: remove `OperatingSystem.IsWindows()` platform detection, always use `TmuxBackend`
- `App.cs`: remove Windows-specific quit messages ("will terminate N sessions")
- `Services/NotificationService.cs`: remove Windows guard
- `Services/SshControlMasterService.cs`: remove Windows chmod guard
- `Services/SshService.cs`: remove Windows `cmd` vs `/bin/sh` branching
- `Handlers/FlowHelper.cs`: remove Windows `explorer` fallback
- `Handlers/SettingsHandler.cs`: remove Windows `explorer` fallback
- `.github/workflows/release.yml`: remove `win-x64` build matrix entry
- `CLAUDE.md`: remove ConPTY references from architecture table
- `README.md`: remove Windows install/setup sections

### What stays

- `BackendRouter` — still needed to route between local `TmuxBackend` and `RemoteTmuxBackend` instances
- `ISessionBackend` interface — unchanged, both backends implement it

## Removed grid code (screenshot-based)

### Renderer.cs
- `BuildGridLayout()` method
- `BuildGridCell()` method
- `BuildGridStatusBar()` method
- `BuildGroupGridStatusBar()` method
- Grid fallback logic in `BuildLayout()` (the `ViewMode.Grid` branch)

### App.cs
- `HandleGridKey()` method
- `FlushGridKeyBatch()` method
- `MoveGridCursor()` method
- `UpdateActiveGridPane()` method
- `UpdateAllCapturedPanes()` method
- `ResizeGridPanes()` method
- Fields: `_gridKeyForwarded`, `_gridKeyBatch`, `_lastGridActivity`, `_allCapturedPanes`
- Active-grid-typing fast path in `MainLoop` (the `isActiveGridTyping` logic)

### AppState.cs
- `GetGridDimensions()` method
- `GetGridCellOutputLines()` method
- Grid cursor clamping branch in `ClampCursor()`

### Enums/ViewMode.cs
- Remove `Grid` value

### Kept from current grid
- `AppState.GetGridSessions()` — reused to determine which sessions enter the native grid
- `AppState.ActiveGroup` / `EnterGroupGrid()` / `LeaveGroupGrid()` — track group context for grid entry
- `ToggleGridView()` in App.cs — rewritten to orchestrate tmux join/break instead of ViewMode switch

## New code

### TmuxBackend additions

New methods on `TmuxBackend` (and corresponding `ISessionBackend` interface additions):

```csharp
// Create the ccc-grid session, join panes, bind keys, set manifest
string? CreateGridSession(List<string> sessionNames);

// Break panes back to original sessions, kill ccc-grid
void RestoreFromGrid();

// Check if ccc-grid exists (for crash recovery)
bool GridSessionExists();

// Read the session manifest from ccc-grid environment
List<string>? GetGridSessionManifest();
```

### RemoteTmuxBackend

Grid mode only operates on the local tmux server. Remote sessions cannot participate in native grid (they're on a different tmux server). If a group contains remote sessions, they are excluded from the grid with a note in the status bar.

### App.cs changes

`ToggleGridView()` rewritten:

```
1. Determine eligible sessions (exclude remote, enforce max 6)
2. Call backend.CreateGridSession(sessionNames)
3. Call backend.AttachSession("ccc-grid")  // blocks
4. On resume: call backend.RestoreFromGrid()
5. LoadSessions() + Render()
```

Startup crash recovery:
```
if (backend.GridSessionExists())
    backend.RestoreFromGrid()
```

## Edge cases

- **All sessions are remote**: status message "No local sessions for grid". Grid requires local tmux.
- **Only 1 eligible session**: status message "Need at least 2 sessions for grid".
- **ccc-grid session name conflicts**: unlikely but check for existence before creating. If exists from a crash, restore first.
- **User creates new CCC session while grid is active**: not possible, CCC main loop is paused.
- **tmux join-pane fails**: if a session has multiple windows/panes, `join-pane -s <session>:0.0` targets the first pane of the first window. This is always the Claude Code pane.
