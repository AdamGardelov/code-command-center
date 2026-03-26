namespace CodeCommandCenter.Models;

public class CccConfig
{
    public List<FavoriteFolder> FavoriteFolders { get; set; } = [];
    public string IdeCommand { get; set; } = "";
    public Dictionary<string, string> SessionDescriptions { get; set; } = new();
    public Dictionary<string, string> SessionColors { get; set; } = new();
    public Dictionary<string, KeyBindingConfig> Keybindings { get; set; } = new();
    public Dictionary<string, SessionGroup> Groups { get; set; } = new();
    public HashSet<string> ExcludedSessions { get; set; } = [];
    public string WorktreeBasePath { get; set; } = "~/Dev/Wint/worktrees/";
    public Dictionary<string, string> SessionStartCommits { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public List<ClaudeConfigRoute> ClaudeConfigRoutes { get; set; } = [];
    public string DefaultClaudeConfigDir { get; set; } = "";
    public List<RemoteHost> RemoteHosts { get; set; } = [];
    public Dictionary<string, string> SessionRemoteHosts { get; set; } = new();
    public Dictionary<string, List<CachedRemoteSession>> CachedRemoteSessions { get; set; } = new();
    public bool DangerouslySkipPermissions { get; set; }
    public HashSet<string> SkipPermissionsSessions { get; set; } = [];
    public string PrReviewLanguage { get; set; } = "en";
    public bool PrIncludeDrafts { get; set; }
    public List<ContainerConfig> Containers { get; set; } = [];
    public Dictionary<string, string> SessionContainers { get; set; } = new();

    // Legacy — migrated to Containers list on load
    public string ContainerName { get; set; } = "";
}
