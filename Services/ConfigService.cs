using System.Text.Json;
using CodeCommandCenter.Models;

namespace CodeCommandCenter.Services;

public static class ConfigService
{
    private static readonly string _configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccc");

    private static readonly string _configPath = Path.Combine(_configDir, "config.json");

    public static string GetConfigPath() => _configPath;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static CccConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            var config = new CccConfig
            {
                Keybindings = KeyBindingService.GetDefaultConfigs(),
            };
            Save(config);
            return config;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<CccConfig>(json, _jsonOptions) ?? new CccConfig();

            var dirty = BackfillKeybindings(config);
            dirty |= MigrateContainerConfig(config);

            if (dirty)
                Save(config);

            return config;
        }
        catch
        {
            return new CccConfig();
        }
    }

    public static void SaveDescription(CccConfig config, string sessionName, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            config.SessionDescriptions.Remove(sessionName);
        else
            config.SessionDescriptions[sessionName] = description;
        Save(config);
    }

    public static void RenameDescription(CccConfig config, string oldName, string newName)
    {
        if (config.SessionDescriptions.Remove(oldName, out var desc))
        {
            config.SessionDescriptions[newName] = desc;
            Save(config);
        }
    }

    public static void RemoveDescription(CccConfig config, string sessionName)
    {
        if (config.SessionDescriptions.Remove(sessionName))
            Save(config);
    }

    public static void SaveColor(CccConfig config, string sessionName, string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            config.SessionColors.Remove(sessionName);
        else
            config.SessionColors[sessionName] = color;
        Save(config);
    }

    public static void RenameColor(CccConfig config, string oldName, string newName)
    {
        if (config.SessionColors.Remove(oldName, out var color))
        {
            config.SessionColors[newName] = color;
            Save(config);
        }
    }

    public static void RemoveColor(CccConfig config, string sessionName)
    {
        if (config.SessionColors.Remove(sessionName))
            Save(config);
    }

    public static void ToggleExcluded(CccConfig config, string sessionName)
    {
        if (!config.ExcludedSessions.Remove(sessionName))
            config.ExcludedSessions.Add(sessionName);
        Save(config);
    }

    public static void RemoveExcluded(CccConfig config, string sessionName)
    {
        if (config.ExcludedSessions.Remove(sessionName))
            Save(config);
    }

    public static void RenameExcluded(CccConfig config, string oldName, string newName)
    {
        if (config.ExcludedSessions.Remove(oldName))
        {
            config.ExcludedSessions.Add(newName);
            Save(config);
        }
    }

    public static void RemoveStartCommit(CccConfig config, string sessionName)
    {
        if (config.SessionStartCommits.Remove(sessionName))
            Save(config);
    }

    public static void RenameStartCommit(CccConfig config, string oldName, string newName)
    {
        if (config.SessionStartCommits.Remove(oldName, out var sha))
        {
            config.SessionStartCommits[newName] = sha;
            Save(config);
        }
    }

    public static void SaveRemoteHost(CccConfig config, string sessionName, string remoteHostName)
    {
        config.SessionRemoteHosts[sessionName] = remoteHostName;
        Save(config);
    }

    public static void RemoveRemoteHost(CccConfig config, string sessionName)
    {
        if (config.SessionRemoteHosts.Remove(sessionName))
            Save(config);
    }

    public static void RenameRemoteHost(CccConfig config, string oldName, string newName)
    {
        if (config.SessionRemoteHosts.Remove(oldName, out var host))
        {
            config.SessionRemoteHosts[newName] = host;
            Save(config);
        }
    }

    public static void SetSkipPermissions(CccConfig config, string sessionName, bool enabled)
    {
        if (enabled)
            config.SkipPermissionsSessions.Add(sessionName);
        else
            config.SkipPermissionsSessions.Remove(sessionName);
        Save(config);
    }

    public static void RemoveSkipPermissions(CccConfig config, string sessionName)
    {
        if (config.SkipPermissionsSessions.Remove(sessionName))
            Save(config);
    }

    public static void SetContainerSession(CccConfig config, string sessionName, string? containerName)
    {
        if (containerName != null)
            config.SessionContainers[sessionName] = containerName;
        else
            config.SessionContainers.Remove(sessionName);
        Save(config);
    }

    public static void RemoveContainerSession(CccConfig config, string sessionName)
    {
        if (config.SessionContainers.Remove(sessionName))
            Save(config);
    }

    public static void RenameContainerSession(CccConfig config, string oldName, string newName)
    {
        if (config.SessionContainers.Remove(oldName, out var container))
        {
            config.SessionContainers[newName] = container;
            Save(config);
        }
    }

    public static void RenameSkipPermissions(CccConfig config, string oldName, string newName)
    {
        if (config.SkipPermissionsSessions.Remove(oldName))
        {
            config.SkipPermissionsSessions.Add(newName);
            Save(config);
        }
    }

    private static bool MigrateContainerConfig(CccConfig config)
    {
        if (string.IsNullOrEmpty(config.ContainerName))
            return false;

        // Migrate legacy single ContainerName to Containers list
        if (!config.Containers.Any(c => c.Name == config.ContainerName && c.RemoteHost == null))
            config.Containers.Add(new ContainerConfig { Name = config.ContainerName });

        config.ContainerName = "";
        return true;
    }

    private static bool BackfillKeybindings(CccConfig config)
    {
        var defaults = KeyBindingService.GetDefaultConfigs();
        var validIds = KeyBindingService.GetValidActionIds();
        var changed = false;

        // Add missing actions
        foreach (var (id, def) in defaults)
            if (config.Keybindings.TryAdd(id, def))
                changed = true;

        // Remove stale actions
        var staleKeys = config.Keybindings.Keys
            .Where(id => !validIds.Contains(id))
            .ToList();

        foreach (var key in staleKeys)
        {
            config.Keybindings.Remove(key);
            changed = true;
        }

        return changed;
    }

    public static void SaveRemoteSessionCache(CccConfig config, string hostName, List<Session> sessions)
    {
        config.CachedRemoteSessions[hostName] = sessions
            .Select(s => new CachedRemoteSession
            {
                Name = s.Name,
                Path = s.CurrentPath,
                Created = s.Created ?? DateTime.UtcNow,
            })
            .ToList();
        Save(config);
    }

    public static void SaveConfig(CccConfig config) => Save(config);

    private static void Save(CccConfig config)
    {
        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        File.WriteAllText(_configPath, json);
    }

    public static void SaveGroup(CccConfig config, SessionGroup group)
    {
        config.Groups[group.Name] = group;
        Save(config);
    }

    public static void RemoveGroup(CccConfig config, string groupName)
    {
        if (config.Groups.Remove(groupName))
            Save(config);
    }

    public static void RemoveSessionFromGroup(CccConfig config, string groupName, string sessionName)
    {
        if (!config.Groups.TryGetValue(groupName, out var group))
            return;

        group.Sessions.Remove(sessionName);

        if (group.Sessions.Count == 0)
            config.Groups.Remove(groupName);

        Save(config);
    }

    public static string? ResolveClaudeConfigDir(CccConfig config, string workingDirectory)
    {
        var expandedDir = Path.GetFullPath(ExpandPath(workingDirectory));

        foreach (var route in config.ClaudeConfigRoutes)
        {
            var expandedPrefix = Path.GetFullPath(ExpandPath(route.PathPrefix));
            if (expandedDir.Equals(expandedPrefix, StringComparison.Ordinal) ||
                expandedDir.StartsWith(expandedPrefix.TrimEnd('/') + "/", StringComparison.Ordinal))
                return ExpandPath(route.ConfigDir);
        }

        return string.IsNullOrEmpty(config.DefaultClaudeConfigDir)
            ? null
            : ExpandPath(config.DefaultClaudeConfigDir);
    }

    public static string ExpandPath(string path)
    {
        if (path.StartsWith('~'))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[1..].TrimStart('/'));

        return path;
    }
}
