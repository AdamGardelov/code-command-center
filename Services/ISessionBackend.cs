using CodeCommandCenter.Models;

namespace CodeCommandCenter.Services;

public interface ISessionBackend : IDisposable
{
    // Lifecycle
    List<Session> ListSessions();
    string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null, bool dangerouslySkipPermissions = false, string? initialPrompt = null, bool shellOnly = false);
    string? KillSession(string name);
    string? RenameSession(string oldName, string newName);

    // Interaction
    void AttachSession(string name);
    void DetachSession();
    string? SendKeys(string sessionName, string text);
    void ForwardKey(string sessionName, ConsoleKeyInfo key);
    void ForwardLiteralBatch(string sessionName, string text);
    string? CapturePaneContent(string sessionName, int lines = 500);

    // Display
    void ResizeWindow(string sessionName, int width, int height);
    void ResetWindowSize(string sessionName);
    void ApplyStatusColor(string sessionName, string? spectreColor);

    // State detection
    void DetectWaitingForInputBatch(List<Session> sessions);

    // Remote session discovery
    List<Session> GetUntrackedRemoteSessions() => [];

    // Grid mode — native tmux split panes
    string? CreateGridSession(List<string> sessionNames) => "Grid not supported";
    void RestoreFromGrid() { }
    bool GridSessionExists() => false;
    Dictionary<string, string>? GetGridSessionManifest() => null;

    // Environment checks
    bool IsAvailable();
    bool IsInsideHost();
    bool HasClaude();

    // Pool model — sidebar mode
    Task SetupPool() => Task.CompletedTask;
    Task CreateSessionInPool(string name, string dir, string? claudeConfigDir = null,
        bool dangerouslySkipPermissions = false, string? initialPrompt = null,
        bool shellOnly = false) => Task.CompletedTask;

    // Manager lifecycle
    Task SetupManagerSession(string executablePath, string focusKeybinding = "C-Space",
        bool mouseEnabled = true) => Task.CompletedTask;
    void AttachManagerSession() { }
    bool IsInsideManager() => false;
    bool ManagerSessionExists() => false;

    // Embed/unembed
    Task EmbedSession(string sessionName) => Task.CompletedTask;
    Task UnembedSession(string sessionName) => Task.CompletedTask;
    Task SwapEmbeddedSession(string oldName, string newName) => Task.CompletedTask;
    string? GetEmbeddedSessionName() => null;

    // Grid in manager
    Task EmbedGridSessions(List<string> sessionNames) => Task.CompletedTask;
    Task RestoreGridToSingleEmbed() => Task.CompletedTask;

    // Migration
    Task MigrateStandaloneSessionsToPool(List<Session> standaloneSessions) => Task.CompletedTask;
}
