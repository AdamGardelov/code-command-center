namespace CodeCommandCenter.Models;

public abstract record TreeItem
{
    public record SessionItem(Session Session, string? GroupName) : TreeItem;
    public record GroupHeader(SessionGroup Group, bool IsExpanded) : TreeItem;
    public record RepoItem(string RepoName, string RepoPath, string GroupName) : TreeItem;
    public record MachineHeader(string HostName, bool IsLocal, bool IsExpanded, bool IsOffline, int SessionCount) : TreeItem;
}
