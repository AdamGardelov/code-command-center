using CodeCommandCenter.Enums;
using CodeCommandCenter.Models;

namespace CodeCommandCenter.UI;

public class AppState
{
    public List<Session> Sessions { get; set; } = [];
    public CccConfig Config { get; set; } = new();
    public int CursorIndex { get; set; }
    public ViewMode ViewMode { get; set; } = ViewMode.List;
    public bool Running { get; set; } = true;
    public bool IsInputMode { get; set; }
    public string InputBuffer { get; set; } = "";
    public string? InputTarget { get; set; }
    private string? StatusMessage { get; set; }
    private DateTime? StatusMessageTime { get; set; }

    // Group state
    public List<SessionGroup> Groups { get; set; } = [];
    public HashSet<string> ExpandedGroups { get; set; } = [];
    public string? ActiveGroup { get; set; }
    private int _savedCursorIndex;
    private HashSet<string> _knownGroupNames = [];

    // Machine grouping state
    public const string LocalMachineKey = "";
    public Dictionary<string, bool> MachineExpansion { get; set; } = new();
    public Dictionary<string, bool> MachineOnlineStatus { get; set; } = new();

    // Mobile mode state
    public bool MobileMode { get; set; }
    private int GroupFilterIndex { get; set; } // 0 = All, 1+ = group index
    public int TopIndex { get; set; } // Scroll offset for mobile list

    // Settings state
    public int SettingsCategory { get; set; }
    public int SettingsItemCursor { get; set; }
    public bool SettingsFocusRight { get; set; }
    public bool IsSettingsEditing { get; set; }
    public bool IsSettingsRebinding { get; set; }
    public string SettingsEditBuffer { get; set; } = "";

    // Diff overlay state
    public int DiffScrollOffset { get; set; }
    public string[] DiffOverlayLines { get; set; } = [];
    public string? DiffOverlaySessionName { get; set; }
    public string? DiffOverlayBranch { get; set; }
    public string? DiffOverlayStatSummary { get; set; }

    public Session? GetSelectedSession()
    {
        if (MobileMode)
        {
            var mobileSessions = GetMobileVisibleSessions();
            if (CursorIndex >= 0 && CursorIndex < mobileSessions.Count)
                return mobileSessions[CursorIndex];
            return null;
        }

        // List view: resolve from tree items
        var treeItems = GetTreeItems();
        if (CursorIndex >= 0 && CursorIndex < treeItems.Count)
        {
            if (treeItems[CursorIndex] is TreeItem.SessionItem si)
                return si.Session;

            // Group header with a root session — return it for preview/actions
            if (treeItems[CursorIndex] is TreeItem.GroupHeader gh
                && gh.Group.Sessions.Contains(gh.Group.Name))
            {
                return Sessions.FirstOrDefault(s => s.Name == gh.Group.Name);
            }
        }

        return null;
    }

    public SessionGroup? GetSelectedGroup()
    {
        var treeItems = GetTreeItems();
        if (CursorIndex >= 0 && CursorIndex < treeItems.Count)
        {
            if (treeItems[CursorIndex] is TreeItem.GroupHeader gh)
                return gh.Group;
        }

        return null;
    }

    public TreeItem.MachineHeader? GetSelectedMachine()
    {
        var treeItems = GetTreeItems();
        if (CursorIndex >= 0 && CursorIndex < treeItems.Count
            && treeItems[CursorIndex] is TreeItem.MachineHeader mh)
            return mh;
        return null;
    }

    public List<Session> GetVisibleSessions()
    {
        // Group grid: show only group's sessions
        if (ActiveGroup != null)
        {
            var group = Groups.FirstOrDefault(g => g.Name == ActiveGroup);
            if (group != null)
            {
                var groupSessionNames = new HashSet<string>(group.Sessions);
                return Sessions.Where(s => groupSessionNames.Contains(s.Name)).ToList();
            }
        }

        // Standalone sessions only (list view sessions section + global grid)
        return GetStandaloneSessions();
    }

    public List<Session> GetStandaloneSessions()
    {
        var groupedNames = new HashSet<string>(Groups.SelectMany(g => g.Sessions));
        return Sessions
            .Where(s => !groupedNames.Contains(s.Name))
            .OrderBy(s => s.IsExcluded)
            .ThenBy(s => s.Created)
            .ThenBy(s => s.Name)
            .ToList();
    }

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

        // Determine if any remote machine has sessions (to decide whether headers are needed)
        var anyRemoteHasSessions = machineOrder
            .Where(k => k != LocalMachineKey)
            .Any(k => machineBuckets.TryGetValue(k, out var s) && s.Count > 0);

        foreach (var machineKey in machineOrder)
        {
            if (!machineBuckets.TryGetValue(machineKey, out var machineSessions))
                machineSessions = [];

            var isLocal = machineKey == LocalMachineKey;

            // Hide categories with no sessions
            if (machineSessions.Count == 0)
                continue;

            // Skip local header if no remotes have sessions (no categories needed)
            var showHeader = anyRemoteHasSessions;

            var isExpanded = MachineExpansion.GetValueOrDefault(machineKey, true);
            var isOffline = MachineOnlineStatus.GetValueOrDefault(machineKey, false);

            if (showHeader)
            {
                items.Add(new TreeItem.MachineHeader(
                    isLocal ? "Local" : machineKey, isLocal, isExpanded, isOffline,
                    machineSessions.Count));

                if (!isExpanded)
                    continue;
            }

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

    public void ToggleGroupExpanded(string groupName)
    {
        if (!ExpandedGroups.Remove(groupName))
            ExpandedGroups.Add(groupName);
    }

    public void ToggleMachineExpanded(string machineKey)
    {
        var current = MachineExpansion.GetValueOrDefault(machineKey, true);
        MachineExpansion[machineKey] = !current;
    }

    public void InitExpandedGroups()
    {
        var currentNames = new HashSet<string>(Groups.Select(g => g.Name));
        // Clean up stale entries for groups that no longer exist
        ExpandedGroups.RemoveWhere(n => !currentNames.Contains(n));
        _knownGroupNames.RemoveWhere(n => !currentNames.Contains(n));
        // Auto-expand new groups, but keep worktree groups with no per-repo sessions collapsed
        foreach (var group in Groups)
        {
            var hasPerRepoSessions = group.Sessions.Any(s => s != group.Name);
            if (_knownGroupNames.Add(group.Name) && (hasPerRepoSessions || group.Repos.Count == 0))
                ExpandedGroups.Add(group.Name);
        }
    }

    public List<Session> GetGridSessions() =>
        GetVisibleSessions()
            .Where(s => !s.IsExcluded)
            .ToList();

    public List<Session> GetMobileVisibleSessions()
    {
        if (GroupFilterIndex == 0)
            return GetStandaloneSessions();

        var groupIdx = GroupFilterIndex - 1;
        if (groupIdx < Groups.Count)
        {
            var group = Groups[groupIdx];
            var groupSessionNames = new HashSet<string>(group.Sessions);
            return Sessions.Where(s => groupSessionNames.Contains(s.Name)).ToList();
        }

        return GetStandaloneSessions();
    }

    public void CycleGroupFilter()
    {
        if (Groups.Count == 0)
            return;

        GroupFilterIndex++;
        if (GroupFilterIndex > Groups.Count)
            GroupFilterIndex = 0;

        CursorIndex = 0;
        TopIndex = 0;
    }

    public string GetGroupFilterLabel()
    {
        if (GroupFilterIndex == 0 || GroupFilterIndex > Groups.Count)
            return "All";
        return Groups[GroupFilterIndex - 1].Name;
    }

    public void EnsureCursorVisible(int visibleHeight)
    {
        if (CursorIndex < TopIndex)
            TopIndex = CursorIndex;
        else if (CursorIndex >= TopIndex + visibleHeight)
            TopIndex = CursorIndex - visibleHeight + 1;
    }

    public void EnterGroupGrid(string groupName)
    {
        _savedCursorIndex = CursorIndex;
        ActiveGroup = groupName;
    }

    public void LeaveGroupGrid()
    {
        ActiveGroup = null;
        CursorIndex = _savedCursorIndex;
        ClampCursor();
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
        StatusMessageTime = DateTime.Now;
    }

    public void ClearStatus()
    {
        StatusMessage = null;
        StatusMessageTime = null;
    }

    public bool HasPendingStatus =>
        StatusMessage != null && StatusMessageTime != null;

    public string? GetActiveStatus()
    {
        if (StatusMessage == null || StatusMessageTime == null)
            return null;

        if ((DateTime.Now - StatusMessageTime.Value).TotalSeconds > 2)
        {
            ClearStatus();
            return null;
        }

        return StatusMessage;
    }

    public bool HasUntrackedRemoteSessions { get; set; }
    public string? LatestVersion { get; set; }

    public int[] DiffFileBoundaries { get; set; } = [];
    public bool DiffStatExpanded { get; set; }

    public List<KeyBinding> Keybindings { get; set; } = [];

    public void ClampCursor()
    {
        if (MobileMode)
        {
            var mobile = GetMobileVisibleSessions();
            CursorIndex = mobile.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, mobile.Count - 1);
            return;
        }

        var tree = GetTreeItems();
        CursorIndex = tree.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, tree.Count - 1);
    }

    public void EnterDiffOverlay(string name, string? branch, string? stat, string[] lines)
    {
        ViewMode = ViewMode.DiffOverlay;
        DiffScrollOffset = 0;
        DiffOverlaySessionName = name;
        DiffOverlayBranch = branch;
        DiffOverlayStatSummary = stat;
        DiffOverlayLines = lines;

        // Pre-compute file boundaries for arrow-key file navigation
        var boundaries = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("diff --git "))
                boundaries.Add(i);
        DiffFileBoundaries = boundaries.ToArray();
        DiffStatExpanded = false;
    }

    public void LeaveDiffOverlay()
    {
        ViewMode = ViewMode.List;
        DiffOverlayLines = [];
        DiffOverlaySessionName = null;
        DiffOverlayBranch = null;
        DiffOverlayStatSummary = null;
        DiffFileBoundaries = [];
    }

    public void EnterSettings()
    {
        ViewMode = ViewMode.Settings;
        SettingsCategory = 0;
        SettingsItemCursor = 0;
        SettingsFocusRight = false;
        IsSettingsEditing = false;
        IsSettingsRebinding = false;
        SettingsEditBuffer = "";
    }

    public void LeaveSettings()
    {
        ViewMode = ViewMode.List;
        IsSettingsEditing = false;
        IsSettingsRebinding = false;
        SettingsEditBuffer = "";
    }
}
