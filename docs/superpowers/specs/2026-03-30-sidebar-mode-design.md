# CCC v2 — Sidebar Mode

## Problem

Current CCC requires full context switches to interact with sessions: `List → attach → work → Ctrl-b d → List`. The preview pane is read-only. Forwarding keys via `send-keys` introduces unacceptable latency. Every session switch breaks flow.

## Solution: Tmux-Embedded Sidebar (Pool Model)

CCC runs inside a tmux session (`ccc-manager`). Left pane = Spectre.Console navigation (always visible). Right pane = live interactive session pane moved from a hidden pool. No attach/detach cycle.

```
┌──────────────────┬─────────────────────────────────────────┐
│ ◆ Sessions       │                                         │
│ ──────────────── │  $ claude                               │
│ ● api-service  ← │  > Fixing the API endpoint...           │
│   frontend       │                                         │
│   core-lib       │  ⠋ Working...                           │
│                  │                                         │
│ ↑↓ nav  ⏎ embed  │           ← real tmux pane              │
│ ^Space → focus   │                                         │
└──────────────────┴─────────────────────────────────────────┘
```

## Tmux Topology

Two tmux sessions:

```
ccc-pool (hidden, holds all session panes)
  ├── window 0: api-service    (claude process)
  ├── window 1: frontend       (claude process)
  ├── window 2: core-lib       (claude process)
  └── window 3: playground     (claude process)

ccc-manager (user-facing)
  ├── pane 0: CCC nav          (dotnet process, Spectre.Console)
  └── pane 1: [api-service]    (move-pane'd from ccc-pool window 0)
```

- New sessions are created as windows in `ccc-pool`.
- Enter on a session → `move-pane` from pool into `ccc-manager` pane 1.
- Switching session → move current pane back to pool, move new one in.
- Pool keeps sessions alive independently — they survive CCC crashes.

## Decisions

| Topic | Decision |
|-------|----------|
| Architecture | Pool Model — `ccc-pool` (hidden) + `ccc-manager` (visible) |
| Focus switching | Keybinding (default `Ctrl+Space`, configurable) + mouse (`set mouse on`) |
| Initial state | Auto-embed first session if any exist, otherwise welcome message |
| Session switching | Direct swap via `move-pane` (no animation) |
| Grid view | Sub-panes in right side — sidebar remains visible |
| Startup | Auto-bootstrap — `ccc` creates manager session and re-execs itself |
| Remote sessions | Blocking attach as today, clearly marked in session list |
| Session creation | `c` = create + embed, `C` = create in background |

## Startup Flow

1. User runs `ccc` in any terminal.
2. CCC detects it is not inside `ccc-manager`.
3. CCC creates `ccc-pool` session if it doesn't exist.
4. CCC creates `ccc-manager` session with two panes: left (nav), right (session area).
5. CCC re-execs itself inside the left pane of `ccc-manager`.
6. CCC attaches the user's terminal to `ccc-manager`.
7. If sessions exist in the pool, auto-embed the first one in tree view order in the right pane.
8. If no sessions exist, right pane runs a placeholder shell. CCC renders a status hint ("Press `c` to create a session") in the nav pane.

On subsequent runs, if `ccc-manager` already exists, CCC attaches to it directly (reconnect after SSH drop, etc.).

## Focus Switching

Two mechanisms, configured at manager startup:

### Keybinding

CCC registers a tmux keybinding that only activates inside `ccc-manager`:

```
tmux bind -n C-Space if -F '#{==:#{session_name},ccc-manager}' \
  'select-pane -t {last}' \
  'send-keys C-Space'
```

Inside `ccc-manager`: toggles focus between nav and session pane. Outside: passes `Ctrl+Space` through to the application. Configurable in CCC settings — stored in `config.json` under `keybindings`.

### Mouse

```
tmux set -t ccc-manager mouse on
```

Click left pane = nav focus. Click right pane = session focus. Works when available, gracefully absent over problematic SSH connections.

## Session Lifecycle

### Creating Sessions

**`c` — Create + Embed:**
1. CCC runs the existing session creation flow (pick directory, name, etc.).
2. Session is created as a new window in `ccc-pool` running `claude` (or shell).
3. If a session is currently embedded, move it back to pool.
4. Move the new session's pane into `ccc-manager` pane 1.

**`C` — Create in Background:**
1. Same creation flow.
2. Session is created in `ccc-pool` but NOT embedded.
3. Current embedded session stays. User switches when ready.

### Switching Sessions

1. User navigates to a session in the nav list, presses Enter.
2. CCC calls `UnembedSession(current)` — moves pane back to its pool window.
3. CCC calls `EmbedSession(selected)` — moves pane from pool into manager pane 1.
4. CCC render loop continues without interruption.

### Deleting Sessions

Same as today — `d` kills the session. If it was embedded, the right pane returns to welcome state or auto-embeds the next session in the list.

## Grid View

Ctrl+G activates grid mode. Instead of a single session in the right pane, multiple sessions are moved from the pool and arranged as sub-panes.

### Grid Flow

1. Ctrl+G pressed — CCC determines which sessions to grid (current group, or selected sessions).
2. If a session is currently embedded, it stays as the first pane.
3. Additional sessions are `move-pane`'d from pool into the right side of `ccc-manager`.
4. Tmux layout applied: `even-horizontal` for 2-3, `tiled` for 4-6.
5. Sidebar remains in the left pane throughout.
6. Ctrl+G again (or `q`) restores: all grid panes moved back to pool, single session re-embedded.

### Grid Limits

- Max 6 sessions (tmux pane management becomes unwieldy beyond this).
- Only local sessions (remote cannot be moved between sessions).
- Sidebar remains interactive — user can still navigate and see status.

## Remote Sessions

Remote sessions (via SSH backend) cannot participate in the pool model because `move-pane` only works within a single tmux server.

- Remote sessions appear in the nav list with a clear visual indicator (e.g., hostname prefix).
- Enter on a remote session triggers blocking attach as today — CCC exits alternate screen, tmux takes over, user detaches to return.
- Remote sessions cannot be gridded.
- This is an honest limitation, not a workaround. Future options: SSH port forwarding to remote tmux, or remote pool agents.

## ISessionBackend Changes

New methods on `ISessionBackend`:

```csharp
// Pool management
Task SetupPool();                              // Create ccc-pool if not exists
Task CreateSessionInPool(string name, string dir, bool runClaude = true);

// Manager lifecycle
Task SetupManagerSession();                     // Create ccc-manager with two panes, register keybindings
Task AttachManagerSession();                    // Attach user terminal to ccc-manager
bool IsInsideManager();                         // Detect if already running inside ccc-manager

// Embed/unembed
Task EmbedSession(string name);                 // move-pane from pool into manager pane 1
Task UnembedSession(string name);               // move-pane back to pool
Task SwapEmbeddedSession(string oldName, string newName);  // atomic: unembed old + embed new

// Grid
Task EmbedGridSessions(List<string> names);     // move multiple panes into manager right side
Task RestoreGridSessions();                     // move all grid panes back to pool
```

Existing methods (`ListSessions`, `KillSession`, `RenameSession`, `CapturePaneContent`, `DetectWaitingForInputBatch`) remain unchanged — they query `ccc-pool` windows instead of standalone tmux sessions.

## App.cs Changes

### Run() / Bootstrap

```
if (!backend.IsInsideManager())
{
    backend.SetupPool();
    backend.SetupManagerSession();
    // Re-exec: start CCC inside manager's left pane, then attach
    backend.AttachManagerSession();
    return; // Original process exits after attach
}

// We're inside the manager — run normal main loop
MainLoop();
```

### MainLoop Changes

- Remove `AttachSession()` calls — replace with `EmbedSession()` / `SwapEmbeddedSession()`.
- Preview pane capture logic is no longer needed for local sessions (the pane is live). Keep it for:
  - Populating session status indicators (waiting-for-input detection).
  - Remote session preview (read-only, as today).
- Render loop continues running during session interaction (nav is always live).

### Renderer Changes

- Nav panel renders in a narrower space (left pane only, ~28-35 chars).
- Preview panel content rendering is removed for embedded sessions (tmux handles display).
- Status bar and header adapt to narrower width.
- Grid mode: nav shows which sessions are gridded with visual indicator.

## Session Handler Changes

`SessionHandler.Attach()` becomes `SessionHandler.Embed()`:

- No more blocking `tmux attach-session`.
- Calls `backend.SwapEmbeddedSession(current, selected)`.
- CCC keeps running, render loop continues.
- Focus stays on nav pane — user presses `Ctrl+Space` to interact with session.

## Crash Recovery

- Sessions live in `ccc-pool` independently — they survive CCC crashes.
- If CCC crashes, user can restart `ccc` — it detects existing `ccc-pool` and `ccc-manager`, re-attaches.
- If `ccc-manager` is corrupted, CCC can recreate it and re-embed sessions from pool.
- Manifest of pool windows stored in tmux environment variable on `ccc-pool` (similar to current grid crash recovery).

## Migration / Backwards Compatibility

- Existing standalone tmux sessions (created by current CCC) should be detected and offered for import into the pool on first run.
- Config format (`~/.ccc/config.json`) gains new fields: `focusKeybinding` (default `C-Space`), `mouseEnabled` (default `true`).
- No breaking changes to existing config fields.

## Out of Scope

- Remote session pool (SSH-based pool management) — future enhancement.
- Browser/Electron alternative — potentially a separate project.
- Custom tmux status bar integration — nice-to-have, not required.
- Mobile mode changes — sidebar doesn't apply on narrow terminals, keep current behavior.
