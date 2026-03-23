# Session-by-Machine Grouping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Group sessions under collapsible machine headers (Local, remote-1, remote-2...) so local and remote sessions are visually separated in the tree view. Also fix the lingering killed remote session bug.

**Architecture:** Add a `MachineHeader` variant to the `TreeItem` discriminated union. Rewrite `GetTreeItems()` to partition sessions by `RemoteHostName` and wrap each partition in a collapsible machine header. Update rendering and key dispatch to handle the new item type.

**Tech Stack:** .NET 10, Spectre.Console

**Spec:** `docs/superpowers/specs/2026-03-23-session-machine-grouping-design.md`

---

### Task 1: Bug fix — lingering killed remote session

**Files:**
- Modify: `Services/BackendRouter.cs:54-67`

- [ ] **Step 1: Fix the offline cache filter**

In `Services/BackendRouter.cs`, replace the offline branch (lines 56-66) to filter cached sessions against the tracking map:

```csharp
// Mid-session offline: transient — show cached sessions greyed out
var cached = config.CachedRemoteSessions.GetValueOrDefault(hostName) ?? [];
var offlineSessions = cached
    .Where(c => tracked.TryGetValue(c.Name, out var h) && h == hostName)
    .Select(c => new Session
    {
        Name = c.Name,
        CurrentPath = c.Path,
        Created = c.Created,
        RemoteHostName = hostName,
        IsOffline = true,
    }).ToList();
all.AddRange(offlineSessions);
```

The key change is `.Where(c => tracked.TryGetValue(c.Name, out var h) && h == hostName)` — this ensures sessions removed from `SessionRemoteHosts` via `RemoveRemoteHost()` during a kill are no longer resurrected from cache.

- [ ] **Step 2: Build and verify**

Run: `dotnet build`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add Services/BackendRouter.cs
git commit -m "fix: filter cached remote sessions against tracking map in offline branch"
```

---

### Task 2: Add `MachineHeader` to TreeItem

**Files:**
- Modify: `Models/TreeItem.cs`

- [ ] **Step 1: Add MachineHeader record**

Add the new variant to the discriminated union in `Models/TreeItem.cs`:

```csharp
public record MachineHeader(string HostName, bool IsLocal, bool IsExpanded, bool IsOffline) : TreeItem;
```

The full file becomes:

```csharp
namespace CodeCommandCenter.Models;

public abstract record TreeItem
{
    public record SessionItem(Session Session, string? GroupName) : TreeItem;
    public record GroupHeader(SessionGroup Group, bool IsExpanded) : TreeItem;
    public record RepoItem(string RepoName, string RepoPath, string GroupName) : TreeItem;
    public record MachineHeader(string HostName, bool IsLocal, bool IsExpanded, bool IsOffline) : TreeItem;
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build`
Expected: Build succeeds. No consumers break since existing `switch` expressions use `_` defaults or don't exhaustively match.

- [ ] **Step 3: Commit**

```bash
git add Models/TreeItem.cs
git commit -m "feat: add MachineHeader variant to TreeItem"
```

---

### Task 3: Add machine state to AppState and expose online status from BackendRouter

**Files:**
- Modify: `UI/AppState.cs:6-44` (add state fields and `GetSelectedMachine()`)
- Modify: `Services/BackendRouter.cs` (add `GetRemoteOnlineStatus()`)

- [ ] **Step 1: Add `GetRemoteOnlineStatus()` to BackendRouter**

In `Services/BackendRouter.cs`, add a public method after `GetUntrackedRemoteSessions()` (line 21):

```csharp
public Dictionary<string, bool> GetRemoteOnlineStatus()
{
    var status = new Dictionary<string, bool>();
    foreach (var (hostName, remoteBackend) in remotes)
        status[hostName] = remoteBackend.IsOffline;
    return status;
}
```

- [ ] **Step 2: Add machine state fields to AppState**

In `UI/AppState.cs`, add after line 23 (`private HashSet<string> _knownGroupNames = [];`):

```csharp
// Machine grouping state
public const string LocalMachineKey = "";
public Dictionary<string, bool> MachineExpansion { get; set; } = new();
public Dictionary<string, bool> MachineOnlineStatus { get; set; } = new();
```

- [ ] **Step 3: Add `GetSelectedMachine()` method**

In `UI/AppState.cs`, add after `GetSelectedGroup()` (after line 84):

```csharp
public TreeItem.MachineHeader? GetSelectedMachine()
{
    var treeItems = GetTreeItems();
    if (CursorIndex >= 0 && CursorIndex < treeItems.Count
        && treeItems[CursorIndex] is TreeItem.MachineHeader mh)
        return mh;
    return null;
}
```

- [ ] **Step 4: Add `ToggleMachineExpanded()` method**

In `UI/AppState.cs`, add after `ToggleGroupExpanded()` (after line 164):

```csharp
public void ToggleMachineExpanded(string machineKey)
{
    var current = MachineExpansion.GetValueOrDefault(machineKey, true);
    MachineExpansion[machineKey] = !current;
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add UI/AppState.cs Services/BackendRouter.cs
git commit -m "feat: add machine expansion state, online status, and GetSelectedMachine()"
```

---

### Task 4: Populate MachineOnlineStatus in App.LoadSessions()

**Files:**
- Modify: `App.cs:238-248` (synchronous `LoadSessions()`)
- Modify: `App.cs:199-212` (async session load completion block)

- [ ] **Step 1: Update synchronous `LoadSessions()`**

In `App.cs`, after line 241 (`_state.HasUntrackedRemoteSessions = backend.GetUntrackedRemoteSessions().Count > 0;`), add:

```csharp
if (backend is BackendRouter router)
    _state.MachineOnlineStatus = router.GetRemoteOnlineStatus();
```

- [ ] **Step 2: Update async load completion block**

In `App.cs`, in the `_pendingSessionLoad` completion block, after line 206 (`_state.HasUntrackedRemoteSessions = backend.GetUntrackedRemoteSessions().Count > 0;`), add the same line:

```csharp
if (backend is BackendRouter router)
    _state.MachineOnlineStatus = router.GetRemoteOnlineStatus();
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add App.cs
git commit -m "feat: populate MachineOnlineStatus from BackendRouter on session load"
```

---

### Task 5: Rewrite `GetTreeItems()` to partition by machine

**Files:**
- Modify: `UI/AppState.cs:114-158` (rewrite `GetTreeItems()`)

This is the core change. The new method partitions sessions by `RemoteHostName`, wraps each partition in a `MachineHeader`, and nests groups under their machine.

- [ ] **Step 1: Add a `Config` property to AppState**

`GetTreeItems()` needs access to `config.RemoteHosts` for machine ordering. In `UI/AppState.cs`, add a property near the top (after line 8):

```csharp
public CccConfig Config { get; set; } = new();
```

- [ ] **Step 2: Set AppState.Config in App constructor**

In `App.cs`, find where `_state` is initialized and ensure `_state.Config = _config;` is set. Search for existing `_state` initialization and add this line after it. Also add it in `LoadSessions()` to keep it in sync: `_state.Config = _config;`.

- [ ] **Step 3: Rewrite `GetTreeItems()`**

Replace the entire `GetTreeItems()` method (lines 114-158) in `UI/AppState.cs`:

```csharp
public List<TreeItem> GetTreeItems()
{
    var items = new List<TreeItem>();

    // Partition sessions by machine
    var machineBuckets = new Dictionary<string, List<Session>>();
    foreach (var session in Sessions)
    {
        var key = session.RemoteHostName ?? LocalMachineKey;
        if (!machineBuckets.ContainsKey(key))
            machineBuckets[key] = [];
        machineBuckets[key].Add(session);
    }

    // Machine order: local first, then remotes in config order
    var machineOrder = new List<string> { LocalMachineKey };
    foreach (var host in Config.RemoteHosts)
    {
        machineOrder.Add(host.Name);
        // Ensure bucket exists even if no sessions (shows empty machine header)
        machineBuckets.TryAdd(host.Name, []);
    }

    // Pre-compute grouped session names
    var groupedNames = new HashSet<string>(Groups.SelectMany(g => g.Sessions));

    // Determine which machine each group belongs to
    var groupMachine = new Dictionary<string, string>();
    foreach (var group in Groups)
    {
        var liveRemotes = Sessions
            .Where(s => group.Sessions.Contains(s.Name))
            .Select(s => s.RemoteHostName ?? LocalMachineKey)
            .Distinct()
            .ToList();

        // Group belongs to a machine if ALL its live sessions are on that machine
        // Zero live sessions → local (safe default)
        groupMachine[group.Name] = liveRemotes.Count == 1 ? liveRemotes[0] : LocalMachineKey;
    }

    foreach (var machineKey in machineOrder)
    {
        if (!machineBuckets.TryGetValue(machineKey, out var machineSessions))
            machineSessions = [];

        var isLocal = machineKey == LocalMachineKey;
        var isExpanded = MachineExpansion.GetValueOrDefault(machineKey, true);
        var isOffline = MachineOnlineStatus.GetValueOrDefault(machineKey, false);

        // Count includes all sessions under this machine (standalone + grouped)
        items.Add(new TreeItem.MachineHeader(
            isLocal ? "Local" : machineKey, isLocal, isExpanded, isOffline));

        if (!isExpanded)
            continue;

        // Standalone sessions (not in any group), sorted
        var standalone = machineSessions
            .Where(s => !groupedNames.Contains(s.Name))
            .OrderBy(s => s.IsExcluded)
            .ThenBy(s => s.Created)
            .ThenBy(s => s.Name)
            .ToList();

        foreach (var session in standalone)
            items.Add(new TreeItem.SessionItem(session, null));

        // Groups scoped to this machine
        foreach (var group in Groups)
        {
            if (groupMachine.GetValueOrDefault(group.Name) != machineKey)
                continue;

            var groupIsExpanded = ExpandedGroups.Contains(group.Name);
            items.Add(new TreeItem.GroupHeader(group, groupIsExpanded));

            if (groupIsExpanded)
            {
                var groupSessionNames = new HashSet<string>(group.Sessions);
                var groupSessions = Sessions
                    .Where(s => groupSessionNames.Contains(s.Name))
                    .ToList();

                foreach (var session in groupSessions)
                {
                    if (session.Name == group.Name)
                        continue;
                    items.Add(new TreeItem.SessionItem(session, group.Name));
                }

                if (group.Repos.Count > 0)
                {
                    var liveSessionNames = new HashSet<string>(
                        groupSessions.Select(s => s.Name));
                    foreach (var (repoName, repoPath) in group.Repos)
                    {
                        var expectedSessionName = $"{group.Name}-{repoName}";
                        if (!liveSessionNames.Contains(expectedSessionName))
                            items.Add(new TreeItem.RepoItem(repoName, repoPath, group.Name));
                    }
                }
            }
        }
    }

    return items;
}
```

- [ ] **Step 4: Update `GetStandaloneSessions()` for `GetVisibleSessions()` compatibility**

`GetStandaloneSessions()` is still used by `GetVisibleSessions()` (for grid) and `GetMobileVisibleSessions()`. It should continue working as-is — it returns a flat list of ungrouped sessions regardless of machine. No changes needed here.

- [ ] **Step 5: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add UI/AppState.cs App.cs
git commit -m "feat: rewrite GetTreeItems() to partition sessions by machine"
```

---

### Task 6: Render MachineHeader and update indentation

**Files:**
- Modify: `UI/Renderer.cs:67-117` (BuildSessionPanel switch + border color)
- Modify: `UI/Renderer.cs:119-183` (BuildSessionRow — indentation + remove cloud icon)
- Modify: `UI/Renderer.cs:185-191` (BuildRepoRow — indentation)
- Modify: `UI/Renderer.cs:193-234` (BuildTreeGroupRow — indentation)
- Modify: `UI/Renderer.cs:236-264` (BuildPreviewPanel — MachineHeader preview)

- [ ] **Step 1: Add `BuildMachineHeaderRow()` method**

In `UI/Renderer.cs`, add after `BuildRepoRow()` (after line 191):

```csharp
private static Markup BuildMachineHeaderRow(TreeItem.MachineHeader mh, bool isSelected, int sessionCount)
{
    var expandIcon = mh.IsExpanded ? "\u25bc" : "\u25b6";
    var name = Markup.Escape(mh.HostName);
    var countLabel = $"({sessionCount})";

    if (mh.IsOffline)
    {
        var row = $"[grey35]{expandIcon} {name} {countLabel} [italic]offline[/][/]";
        return isSelected ? new Markup($"[on grey15]{row}[/]") : new Markup(row);
    }

    if (isSelected)
        return new Markup($"[grey70 on grey19]{expandIcon} {name} {countLabel}[/]");

    return new Markup($"[grey50]{expandIcon} {name} {countLabel}[/]");
}
```

- [ ] **Step 2: Add MachineHeader case to BuildSessionPanel switch**

In `UI/Renderer.cs`, in `BuildSessionPanel()` inside the `switch (treeItems[i])` block (after line 91), add a case before the closing brace:

```csharp
case TreeItem.MachineHeader mh:
    // Count sessions under this machine
    var machineSessionCount = 0;
    for (var j = i + 1; j < treeItems.Count; j++)
    {
        if (treeItems[j] is TreeItem.MachineHeader)
            break;
        if (treeItems[j] is TreeItem.SessionItem)
            machineSessionCount++;
    }
    rows.Add(BuildMachineHeaderRow(mh, isSelected, machineSessionCount));
    break;
```

- [ ] **Step 3: Add MachineHeader to border color logic**

In `UI/Renderer.cs`, in the border color block (around line 98-111), add a case for `MachineHeader` by updating the `else` fallback. After the `RepoItem` check (line 108), before the `else`:

```csharp
else if (selectedItem is TreeItem.MachineHeader)
    borderColor = Color.Grey42;
```

This is the same as the existing `else` default, so it's just for explicitness.

- [ ] **Step 4: Update indentation — all sessions and groups get 1 extra indent level**

Since all sessions and groups now live under a machine header, increase indentation by one level.

In `BuildSessionRow()` (line 119-183):
- Change `var indent = indented ? "   " : "";` to `var indent = indented ? "      " : "   ";` (machine indent + optional group indent)
- Change `var nameWidth = indented ? 19 : 22;` to `var nameWidth = indented ? 16 : 19;` (3 chars less to fit indent)
- Update offline row prefix: change `var prefix = indented ? "  " : "";` to `var prefix = indented ? "     " : "  ";`

In `BuildTreeGroupRow()` (line 193-234):
- Add `" "` (3 spaces) prefix to all return markup strings (before the status icon / expand icon)

In `BuildRepoRow()` (line 185-191):
- Change `"    ○"` to `"       ○"` (add 3 spaces of machine indent)

- [ ] **Step 5: Remove cloud icon from BuildSessionRow**

In `BuildSessionRow()`, remove line 124:
```csharp
var remoteIcon = session.RemoteHostName != null ? "[mediumpurple3]☁[/]" : "";
```

Replace with:
```csharp
var remoteIcon = "";
```

Also remove host info from offline session rows. In the offline block (lines 130-141), remove lines 134-136:
```csharp
var hostInfo = session.RemoteHostName != null
    ? $" [grey35]({Markup.Escape(session.RemoteHostName)})[/]"
    : "";
```
And change the row to remove `{hostInfo}`:
```csharp
var row = $"[grey35]{prefix}✗ {escapedName}[/]{skipIcon}";
```

- [ ] **Step 6: Add MachineHeader preview to BuildPreviewPanel**

In `UI/Renderer.cs`, in `BuildPreviewPanel()` (around line 246-249), after the `RepoItem` check, add:

```csharp
if (currentItem is TreeItem.MachineHeader mhPreview)
{
    var machineCount = state.Sessions.Count(s =>
        (s.RemoteHostName ?? AppState.LocalMachineKey) ==
        (mhPreview.IsLocal ? AppState.LocalMachineKey : mhPreview.HostName));
    var label = mhPreview.IsOffline ? "offline" : $"{machineCount} session(s)";
    var previewText = mhPreview.IsLocal
        ? $"[grey50]Local machine — {label}[/]"
        : $"[grey50]{Markup.Escape(mhPreview.HostName)} — {label}[/]";

    return new Panel(new Markup(previewText))
        .Header("[grey70] Machine [/]")
        .BorderColor(Color.Grey42)
        .Expand();
}
```

- [ ] **Step 7: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 8: Commit**

```bash
git add UI/Renderer.cs
git commit -m "feat: render MachineHeader rows, update indentation, remove cloud icon"
```

---

### Task 7: Pre-select remote host in session create flow

**Files:**
- Modify: `Handlers/SessionHandler.cs:18-68` (Create method)

- [ ] **Step 1: Add `preSelectRemote` parameter to `Create()`**

In `Handlers/SessionHandler.cs`, change the method signature from:
```csharp
public void Create(bool claudeAvailable)
```
to:
```csharp
public void Create(bool claudeAvailable, string? preSelectRemote = null)
```

- [ ] **Step 2: Skip `PickTarget` when pre-selected**

In the Create method body, after line 33 (`RemoteHost? remoteHost = null;`), replace the `if (hasRemotes)` block (lines 36-54) with:

```csharp
if (preSelectRemote != null)
{
    remoteHost = config.RemoteHosts.FirstOrDefault(h => h.Name == preSelectRemote);
    if (remoteHost != null)
    {
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey70))
            .Start($"[grey70]Checking connection to [white]{remoteHost.Name}[/]...[/]", _ =>
            {
                sshVerified = SshService.CheckConnectivity(remoteHost.Host);
            });

        if (!sshVerified)
            AnsiConsole.MarkupLine($"[yellow]⚠ Could not verify connection to {Markup.Escape(remoteHost.Name)} — continuing anyway[/]");
    }
}
else if (hasRemotes)
{
    FlowHelper.PrintStep(++step, totalSteps, "Target");
    remoteHost = flow.PickTarget();

    if (remoteHost != null)
    {
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey70))
            .Start($"[grey70]Checking connection to [white]{remoteHost.Name}[/]...[/]", _ =>
            {
                sshVerified = SshService.CheckConnectivity(remoteHost.Host);
            });

        if (!sshVerified)
            AnsiConsole.MarkupLine($"[yellow]⚠ Could not verify connection to {Markup.Escape(remoteHost.Name)} — continuing anyway[/]");
    }
}
```

Also update the `totalSteps` calculation to account for skipping the target step:

```csharp
var totalSteps = (hasRemotes && preSelectRemote == null ? 1 : 0) + 4 + (globalSkip ? 0 : 1);
```

- [ ] **Step 3: Update other callers of `Create()`**

The existing call in `App.cs` `DispatchAction()` at line 623 (`_sessionHandler.Create(_claudeAvailable);`) continues to work as-is since `preSelectRemote` defaults to `null`.

- [ ] **Step 4: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add Handlers/SessionHandler.cs
git commit -m "feat: pre-select remote host in session create flow from machine header"
```

---

### Task 8: Handle MachineHeader in key dispatch

**Files:**
- Modify: `App.cs:535-680` (DispatchAction method)
- Modify: `App.cs:825-894` (ToggleGridView method)

- [ ] **Step 1: Add MachineHeader guard to DispatchAction**

In `App.cs`, in `DispatchAction()`, after the `RepoItem` guard block (line 551) and before the `GroupHeader` guard (line 553), add a `MachineHeader` guard:

```csharp
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
            // Pre-select this machine's remote host in the create flow
            _sessionHandler.Create(_claudeAvailable,
                preSelectRemote: mh.IsLocal ? null : mh.HostName);
            return;
        case "toggle-grid":
            ToggleGridView();
            return;
    }
    // All other actions are no-ops on machine headers
    return;
}
```

- [ ] **Step 2: Update ToggleGridView for MachineHeader**

In `App.cs`, in `ToggleGridView()`, after the `GroupHeader` check (line 840-845) and before the `else` (line 846), add:

```csharp
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
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add App.cs
git commit -m "feat: handle MachineHeader in key dispatch and grid view"
```

---

### Task 9: Manual testing and polish

**Files:**
- No new files

- [ ] **Step 1: Build release binary**

```bash
dotnet build
```

- [ ] **Step 2: Run and test locally**

Run `dotnet run` (outside tmux). Verify:
- Local machine header appears at top with `▼ Local (N)` in dim grey
- Sessions are indented under the machine header
- Groups appear under their machine section
- Collapse/expand works with Enter on machine header
- Cursor navigation works smoothly through machine headers
- Preview panel shows machine summary when machine header is selected
- Cloud icon is removed from individual session rows
- Grid view works when triggered from machine header

- [ ] **Step 3: Test with remote hosts (if available)**

Verify:
- Remote machine headers appear after Local in config order
- Remote sessions appear under their machine header
- Offline remotes show `[offline]` label
- Creating a session from a remote machine header pre-selects that remote
- Killing the last session on a remote doesn't cause it to linger

- [ ] **Step 4: Final commit if any polish needed**

```bash
git add -A
git commit -m "fix: polish machine header rendering and edge cases"
```

---

### Task 10: Update README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update README to document machine headers**

Add a section or update the existing feature description to mention:
- Sessions are now grouped by machine (Local, remote hosts)
- Machine headers are collapsible (Enter to toggle)
- Creating a session from a machine header pre-selects that host
- The cloud icon has been removed from individual sessions (machine header provides context)

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: update README with machine header grouping feature"
```
