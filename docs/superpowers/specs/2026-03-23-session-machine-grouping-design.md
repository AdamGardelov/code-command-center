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

`AppState` gains:
- `Dictionary<string, bool> MachineExpansion` — expand/collapse per machine, keyed by host name. Use `const string LocalMachineKey = ""` for local. All machines default to expanded. Not persisted to config.
- `Dictionary<string, bool> MachineOnlineStatus` — populated by `App.LoadSessions()` from backend state, used by `GetTreeItems()` to set `MachineHeader.IsOffline`.

No changes to `Session`, `SessionGroup`, or `CccConfig`.

### Tree Building

`AppState.GetTreeItems()` algorithm:

```
result = []
machineBuckets = partition Sessions by RemoteHostName (null → LocalMachineKey)
machineOrder = [LocalMachineKey] + config.RemoteHosts.Select(h => h.Name)

for each machineKey in machineOrder:
    sessions = machineBuckets[machineKey] ?? []
    isExpanded = MachineExpansion.GetValueOrDefault(machineKey, true)
    isOffline = MachineOnlineStatus.GetValueOrDefault(machineKey, false)

    // Groups for this machine: determined by WorktreePath
    // A group belongs to local if WorktreePath is null or group has no remote sessions.
    // A group belongs to a remote if ALL its live sessions are on that remote.
    // Groups with zero live sessions: assigned to local (safe default).
    machineGroups = groups where all live sessions have RemoteHostName == machineKey
                    OR (machineKey == local AND group has no live sessions)

    sessionCount = sessions.Count  // includes grouped sessions
    result.Add(MachineHeader(machineKey, isLocal, isExpanded, isOffline))

    if not isExpanded: continue

    // Standalone sessions (not in any group)
    for each session not in any group:
        result.Add(SessionItem(session, groupName: null))

    // Groups scoped to this machine
    for each group in machineGroups:
        result.Add(GroupHeader(group, isExpanded))
        if group is expanded:
            result.Add(SessionItem/RepoItem for each child)
```

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

**Cloud icon removal**: Remove per-session `☁` from `BuildSessionRow()`. Keep host name display in the preview panel (right side) since the machine header is only visible in the session list (left side). Remove host name from offline/dead session rows in the list since the machine header provides that context.

When a `MachineHeader` is selected, the preview shows a brief summary (e.g., "3 sessions on remote-1").

**Mobile mode**: Machine headers are excluded from mobile mode. `GetMobileVisibleSessions()` continues to return a flat session list.

### Navigation & Interaction

`MachineHeader` is selectable and part of the cursor list, same as `GroupHeader`.

**Expand/collapse**: Enter/Space/Right toggles, Left collapses. When a machine is collapsed, all child items (sessions, groups, repos) are removed from `GetTreeItems()`. `ClampCursor()` already handles cursor-past-end from the existing group collapse logic — no changes needed.

**Key dispatch in `App.DispatchAction()`**: Add a `MachineHeader` guard alongside the existing `GroupHeader` and `RepoItem` guards. The following actions are no-ops on machine headers: `delete-session`, `edit-session`, `toggle-exclude`, `toggle-diff`, `open-folder`, `open-ide`, `send-text`, `approve`, `reject`, `move-to-group`, `adopt-remote`, `review-pr`.

**Actions that work on machine headers**:
- Toggle expand/collapse (Enter/Space/Right/Left)
- Create session (`n`): pre-select that remote host in the create flow
- Grid view (`Ctrl+G`): grid all expanded sessions under this machine (no-op for offline remotes)

**`GetSelectedSession()` and `GetSelectedGroup()`**: Return `null` when cursor is on a `MachineHeader`. Add `GetSelectedMachine()` returning `MachineHeader?` for dispatch logic.

### Machine Order

Local always first. Remotes follow `config.RemoteHosts` order.

## Bug Fix: Lingering Killed Remote Session

**Root cause**: Killing the last tmux session on a remote causes `tmux list-sessions` to return an error. `RemoteTmuxBackend` marks `IsOffline = true`. The offline branch in `BackendRouter.ListSessions()` restores all cached sessions without filtering against `SessionRemoteHosts`, so the killed session reappears until a new session is created.

**Fix**: In `BackendRouter.ListSessions()` offline branch (lines 54-67), filter cached sessions against the tracking map, matching both name and host:

```csharp
var cached = config.CachedRemoteSessions.GetValueOrDefault(hostName) ?? [];
var offlineSessions = cached
    .Where(c => tracked.TryGetValue(c.Name, out var h) && h == hostName)
    .Select(c => new Session { ... })
    .ToList();
```

## Files Changed

| File | Change |
|------|--------|
| `Models/TreeItem.cs` | Add `MachineHeader` variant |
| `UI/AppState.cs` | Add `MachineExpansion`, `MachineOnlineStatus`, `GetSelectedMachine()`, rewrite `GetTreeItems()` |
| `UI/Renderer.cs` | Add `BuildMachineHeaderRow()`, update indentation, remove `☁` from `BuildSessionRow()` |
| `App.cs` | Populate `MachineOnlineStatus` in `LoadSessions()`, handle `MachineHeader` in `DispatchAction()` |
| `Handlers/SessionHandler.cs` | Accept optional remote host pre-selection in create flow |
| `Services/BackendRouter.cs` | Filter cached sessions against tracking map in offline branch, expose remote online status |
