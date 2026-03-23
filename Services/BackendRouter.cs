using CodeCommandCenter.Models;

namespace CodeCommandCenter.Services;

/// <summary>
/// Routes ISessionBackend calls to the correct local or remote backend.
/// This is the single backend instance seen by App and all handlers.
/// </summary>
public class BackendRouter(ISessionBackend local, Dictionary<string, RemoteTmuxBackend> remotes, CccConfig config) : ISessionBackend
{
    // Maps session name → remote host name (null = local). Rebuilt on each ListSessions().
    private Dictionary<string, string?> _sessionHosts = new();

    // Untracked remote sessions discovered during last ListSessions() — available for adoption
    private List<Session> _untrackedRemoteSessions = [];

    // First ListSessions() call = startup. Offline hosts get their cache cleared at startup
    // since the remote may have been down for days. Mid-session offline is transient — keep cache.
    private bool _isFirstList = true;

    public List<Session> GetUntrackedRemoteSessions() => _untrackedRemoteSessions;

    public List<Session> ListSessions()
    {
        var all = new List<Session>();
        var untracked = new List<Session>();

        // Local sessions
        all.AddRange(local.ListSessions());

        // Remote sessions — only include tracked (known to CCC) sessions
        var tracked = config.SessionRemoteHosts;
        foreach (var (hostName, remoteBackend) in remotes)
        {
            // ListSessions() sets IsOffline as a side effect — result is empty list on failure
            var remoteSessions = remoteBackend.ListSessions();

            if (remoteBackend.IsOffline)
            {
                if (_isFirstList)
                {
                    // Startup: remote is unreachable — clear cached sessions.
                    // They can be re-adopted when the host comes back online.
                    var staleNames = tracked
                        .Where(kv => kv.Value == hostName)
                        .Select(kv => kv.Key)
                        .ToList();
                    foreach (var name in staleNames)
                        ConfigService.RemoveRemoteHost(config, name);
                    config.CachedRemoteSessions.Remove(hostName);
                    if (staleNames.Count > 0)
                        ConfigService.SaveConfig(config);
                }
                else
                {
                    // Mid-session offline: transient — show cached sessions greyed out
                    var cached = config.CachedRemoteSessions.GetValueOrDefault(hostName) ?? [];
                    var offlineSessions = cached.Select(c => new Session
                    {
                        Name = c.Name,
                        CurrentPath = c.Path,
                        Created = c.Created,
                        RemoteHostName = hostName,
                        IsOffline = true,
                    }).ToList();
                    all.AddRange(offlineSessions);
                }
            }
            else
            {
                // Split into tracked (show in dashboard) and untracked (available for adoption)
                var remoteNames = new HashSet<string>(remoteSessions.Select(s => s.Name));
                foreach (var s in remoteSessions)
                {
                    if (tracked.TryGetValue(s.Name, out var trackedHost) && trackedHost == hostName)
                        all.Add(s);
                    else
                        untracked.Add(s);
                }

                // Prune tracked sessions that no longer exist on the remote
                var staleTracked = tracked
                    .Where(kv => kv.Value == hostName && !remoteNames.Contains(kv.Key))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var name in staleTracked)
                    ConfigService.RemoveRemoteHost(config, name);

                // Cache only tracked sessions
                var trackedForHost = remoteSessions
                    .Where(s => tracked.TryGetValue(s.Name, out var h) && h == hostName)
                    .ToList();
                ConfigService.SaveRemoteSessionCache(config, hostName, trackedForHost);
            }
        }

        _isFirstList = false;
        _untrackedRemoteSessions = untracked;

        // Deduplicate sessions by name — if a session name exists both locally and as a
        // tracked remote, prefer the tracked remote (it was explicitly adopted by the user).
        // Without this, both appear in the grid and route to the same backend.
        var deduped = new Dictionary<string, Session>();
        foreach (var s in all)
        {
            if (deduped.TryGetValue(s.Name, out var existing))
            {
                // Prefer the one that's tracked as remote (explicit user intent)
                if (s.RemoteHostName != null && existing.RemoteHostName == null)
                    deduped[s.Name] = s;
                // else keep existing (first wins for same-type duplicates)
            }
            else
            {
                deduped[s.Name] = s;
            }
        }
        all = deduped.Values.ToList();

        // Rebuild routing map
        _sessionHosts = new Dictionary<string, string?>();
        foreach (var s in all)
            _sessionHosts[s.Name] = s.RemoteHostName;

        // Also route untracked sessions so operations (like adopt → kill) work
        foreach (var s in untracked)
            _sessionHosts[s.Name] = s.RemoteHostName;

        return all;
    }

    public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null,
        string? remoteHost = null, bool dangerouslySkipPermissions = false, string? initialPrompt = null)
    {
        if (remoteHost != null && remotes.TryGetValue(remoteHost, out var remoteBackend))
            return remoteBackend.CreateSession(name, workingDirectory, claudeConfigDir, null, dangerouslySkipPermissions, initialPrompt);

        return local.CreateSession(name, workingDirectory, claudeConfigDir, null, dangerouslySkipPermissions, initialPrompt);
    }

    public string? KillSession(string name) => BackendFor(name).KillSession(name);

    public string? RenameSession(string oldName, string newName)
    {
        var result = BackendFor(oldName).RenameSession(oldName, newName);
        if (result == null)
        {
            // Update routing map: old name is gone, new name takes its host
            if (_sessionHosts.TryGetValue(oldName, out var host))
            {
                _sessionHosts.Remove(oldName);
                _sessionHosts[newName] = host;
            }
        }
        return result;
    }

    public void AttachSession(string name) => BackendFor(name).AttachSession(name);

    public void DetachSession() => local.DetachSession();

    public string? SendKeys(string sessionName, string text) =>
        BackendFor(sessionName).SendKeys(sessionName, text);

    public void ForwardKey(string sessionName, ConsoleKeyInfo key) =>
        BackendFor(sessionName).ForwardKey(sessionName, key);

    public void ForwardLiteralBatch(string sessionName, string text) =>
        BackendFor(sessionName).ForwardLiteralBatch(sessionName, text);

    public string? CapturePaneContent(string sessionName, int lines = 500)
    {
        if (IsRemoteOffline(sessionName))
            return null;
        return BackendFor(sessionName).CapturePaneContent(sessionName, lines);
    }

    public void ResizeWindow(string sessionName, int width, int height)
    {
        if (!IsRemoteOffline(sessionName))
            BackendFor(sessionName).ResizeWindow(sessionName, width, height);
    }

    public void ResetWindowSize(string sessionName)
    {
        if (!IsRemoteOffline(sessionName))
            BackendFor(sessionName).ResetWindowSize(sessionName);
    }

    public void ApplyStatusColor(string sessionName, string? spectreColor)
    {
        if (!IsRemoteOffline(sessionName))
            BackendFor(sessionName).ApplyStatusColor(sessionName, spectreColor);
    }

    public void DetectWaitingForInputBatch(List<Session> sessions)
    {
        // Split sessions by backend and dispatch to each
        var localSessions = sessions.Where(s => s.RemoteHostName == null).ToList();
        if (localSessions.Count > 0)
            local.DetectWaitingForInputBatch(localSessions);

        foreach (var (hostName, remoteBackend) in remotes)
        {
            // Skip sessions for offline hosts — they have IsOffline = true and
            // RemoteTmuxBackend.DetectWaitingForInputBatch skips them, but we
            // avoid the SSH round-trip entirely by not calling it when offline
            if (remoteBackend.IsOffline)
                continue;

            var remoteSessions = sessions
                .Where(s => s.RemoteHostName == hostName)
                .ToList();
            if (remoteSessions.Count > 0)
                remoteBackend.DetectWaitingForInputBatch(remoteSessions);
        }
    }

    public bool IsAvailable() => local.IsAvailable();
    public bool IsInsideHost() => local.IsInsideHost();
    public bool HasClaude() => local.HasClaude();

    public void Dispose()
    {
        local.Dispose();
        foreach (var remote in remotes.Values)
            remote.Dispose();
    }

    private bool IsRemoteOffline(string sessionName)
    {
        return _sessionHosts.TryGetValue(sessionName, out var hostName)
               && hostName != null
               && remotes.TryGetValue(hostName, out var remote)
               && remote.IsOffline;
    }

    private ISessionBackend BackendFor(string sessionName)
    {
        if (_sessionHosts.TryGetValue(sessionName, out var hostName)
            && hostName != null
            && remotes.TryGetValue(hostName, out var remoteBackend))
            return remoteBackend;

        return local;
    }
}
