namespace CodeCommandCenter.Models;

public class Session
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime? Created { get; set; }
    public bool IsAttached { get; set; }
    public int WindowCount { get; set; }
    public string? ColorTag { get; set; }
    public string? CurrentPath { get; set; }
    public bool IsWaitingForInput { get; set; }
    public bool IsIdle { get; set; }
    public bool IsDead { get; set; }
    public string? PreviousContent { get; set; }
    public int StableContentCount { get; set; }
    public string? GitBranch { get; set; }
    public bool IsWorktree { get; set; }
    public bool IsExcluded { get; set; }
    public string? StartCommitSha { get; set; }
    public string? RemoteHostName { get; set; }
    public bool SkipPermissions { get; set; }
    public bool IsOffline { get; set; }
    public bool IsContainer { get; set; }
    public string? ContainerName { get; set; }
}
