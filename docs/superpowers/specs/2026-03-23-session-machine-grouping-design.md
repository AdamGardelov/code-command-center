# Session-by-Machine Grouping

Group sessions under collapsible machine headers so local and remote sessions are visually separated in the tree view.

## Problem

Sessions from local and remote machines are mixed together in a flat list. The only indicator of a remote session is a small `☁` icon. With multiple remotes, the list becomes hard to scan.

## Design

### Data Model

New `TreeItem` variant:

```csharp
public record MachineHeader(string HostName, bool IsLocal, bool IsExpanded, bool IsOffline) : TreeItem;
```

`AppState` gains `Dictionary<string, bool> MachineExpansion` to track expand/collapse per machine (keyed by host name, `""` for local). All machines default to expanded. Not persisted to config.

No changes to `Session`, `SessionGroup`, or `CccConfig`.

### Tree Building

`AppState.GetTreeItems()` changes:

1. Partition `Sessions` by `RemoteHostName` (null → local bucket).
2. Order: local first, then remotes in `config.RemoteHosts` order.
3. For each machine, emit:
   - `MachineHeader` (count = all sessions under this machine, including grouped)
   - If expanded: standalone sessions, then groups scoped to this machine
4. A group belongs to the machine where all (or majority) of its sessions live.

### Rendering

`Renderer.BuildSessionPanel()` handles `MachineHeader`:

- **Style**: dim grey text — `▼ Local (10)` or `▶ remote-1 (5)`
- **Offline**: `▶ remote-1 (5) [offline]` in grey italic
- **Selected**: highlighted background in grey/dim tones
- **Indentation**:
  - Machine header: 0 indent
  - Standalone sessions: 1 indent
  - Group headers: 1 indent
  - Sessions inside groups: 2 indents

Remove per-session `☁` cloud icon from `BuildSessionRow()` — the machine header provides host context.

When a `MachineHeader` is selected, the preview shows a brief summary line (e.g., "3 sessions on remote-1").

### Navigation & Interaction

- `MachineHeader` is selectable and part of the cursor list.
- Enter/Space/Right: toggle expand/collapse.
- Left: collapse.
- Session-specific actions: no-op on machine headers.
- Create session (`n`) while machine header is selected: pre-select that remote host in the create flow.

### Machine Order

Local always first. Remotes follow `config.RemoteHosts` order.

## Bug Fix: Lingering Killed Remote Session

**Root cause**: Killing the last tmux session on a remote causes `tmux list-sessions` to return an error. `RemoteTmuxBackend` marks `IsOffline = true`. The offline branch in `BackendRouter.ListSessions()` restores all cached sessions without filtering against `SessionRemoteHosts`, so the killed session reappears until a new session is created.

**Fix**: In `BackendRouter.ListSessions()` offline branch (lines 54-67), filter cached sessions against the tracking map:

```csharp
var cached = config.CachedRemoteSessions.GetValueOrDefault(hostName) ?? [];
var offlineSessions = cached
    .Where(c => tracked.ContainsKey(c.Name))
    .Select(c => new Session { ... })
    .ToList();
```

## Files Changed

| File | Change |
|------|--------|
| `Models/TreeItem.cs` | Add `MachineHeader` variant |
| `UI/AppState.cs` | Add `MachineExpansion` state, rewrite `GetTreeItems()` to partition by machine |
| `UI/Renderer.cs` | Add `BuildMachineHeaderRow()`, update indentation, remove `☁` from `BuildSessionRow()` |
| `App.cs` | Handle `MachineHeader` in key dispatch (expand/collapse, scope create to host) |
| `Handlers/SessionHandler.cs` | Accept optional remote host pre-selection in create flow |
| `Services/BackendRouter.cs` | Filter cached sessions against tracking map in offline branch |
