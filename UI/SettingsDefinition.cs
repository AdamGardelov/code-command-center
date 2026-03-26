using CodeCommandCenter.Enums;
using CodeCommandCenter.Models;
using CodeCommandCenter.Services;

namespace CodeCommandCenter.UI;

public static class SettingsDefinition
{
    public static List<SettingsCategory> GetCategories() =>
    [
        new()
        {
            Name = "General",
            Icon = "⚙",
            BuildItems = BuildGeneralItems,
        },
        new()
        {
            Name = "Keybindings",
            Icon = "⌨",
            BuildItems = BuildKeybindingItems,
        },
        new()
        {
            Name = "Pull Requests",
            Icon = "⑂",
            BuildItems = BuildPullRequestItems,
        },
        new()
        {
            Name = "Notifications",
            Icon = "🔔",
            BuildItems = BuildNotificationItems,
        },
        new()
        {
            Name = "Favorites",
            Icon = "★",
            BuildItems = BuildFavoriteItems,
        },
        new()
        {
            Name = "Containers",
            Icon = "\U0001f433",
            BuildItems = BuildContainerItems,
        },
        new()
        {
            Name = "Advanced",
            Icon = "⚡",
            BuildItems = BuildAdvancedItems,
        },
    ];

    private static List<SettingsItem> BuildGeneralItems(CccConfig config) =>
    [
        new()
        {
            Label = "IDE Command",
            Type = SettingsItemType.Text,
            GetValue = c => c.IdeCommand,
            SetValue = (c, v) => c.IdeCommand = v,
        },
        new()
        {
            Label = "Worktree Base Path",
            Type = SettingsItemType.Text,
            GetValue = c => c.WorktreeBasePath,
            SetValue = (c, v) => c.WorktreeBasePath = v,
        },
    ];

    private static List<SettingsItem> BuildPullRequestItems(CccConfig config) =>
    [
        new()
        {
            Label = "Review Language",
            Type = SettingsItemType.Text,
            GetValue = c => c.PrReviewLanguage,
            SetValue = (c, v) =>
            {
                var normalized = v.Trim().ToLowerInvariant();
                if (normalized is "en" or "sv")
                    c.PrReviewLanguage = normalized;
            },
        },
        new()
        {
            Label = "Include Drafts",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.PrIncludeDrafts ? "ON" : "OFF",
            SetValue = (c, _) => c.PrIncludeDrafts = !c.PrIncludeDrafts,
        },
    ];

    private static List<SettingsItem> BuildKeybindingItems(CccConfig config)
    {
        var defaults = KeyBindingService.GetDefaultConfigs();
        var items = new List<SettingsItem>();

        foreach (var (actionId, kbConfig) in config.Keybindings)
        {
            if (defaults.TryGetValue(actionId, out var def) && def.Enabled == null)
                continue;

            items.Add(new SettingsItem
            {
                Label = kbConfig.Label ?? actionId,
                Type = SettingsItemType.Toggle,
                ActionId = actionId,
                GetValue = c => (!c.Keybindings.TryGetValue(actionId, out var kb) || (kb.Enabled ?? true))
                    ? "ON"
                    : "OFF",
                SetValue = (c, _) =>
                {
                    if (c.Keybindings.TryGetValue(actionId, out var kb))
                        kb.Enabled = !(kb.Enabled ?? true);
                },
            });
        }

        return items;
    }

    private static List<SettingsItem> BuildNotificationItems(CccConfig config) =>
    [
        new()
        {
            Label = "Notifications",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.Notifications.Enabled ? "ON" : "OFF",
            SetValue = (c, _) => c.Notifications.Enabled = !c.Notifications.Enabled,
        },
        new()
        {
            Label = "Bell",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.Notifications.Bell ? "ON" : "OFF",
            SetValue = (c, _) => c.Notifications.Bell = !c.Notifications.Bell,
        },
        new()
        {
            Label = "OSC Notify",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.Notifications.OscNotify ? "ON" : "OFF",
            SetValue = (c, _) => c.Notifications.OscNotify = !c.Notifications.OscNotify,
        },
        new()
        {
            Label = "Desktop Notify",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.Notifications.DesktopNotify ? "ON" : "OFF",
            SetValue = (c, _) => c.Notifications.DesktopNotify = !c.Notifications.DesktopNotify,
        },
        new()
        {
            Label = "Cooldown (seconds)",
            Type = SettingsItemType.Number,
            GetValue = c => c.Notifications.CooldownSeconds.ToString(),
            SetValue = (c, v) =>
            {
                if (int.TryParse(v, out var seconds) && seconds >= 0)
                    c.Notifications.CooldownSeconds = seconds;
            },
        },
    ];

    private static List<SettingsItem> BuildFavoriteItems(CccConfig config)
    {
        var items = new List<SettingsItem>();

        for (var i = 0; i < config.FavoriteFolders.Count; i++)
        {
            var index = i;
            items.Add(new SettingsItem
            {
                Label = config.FavoriteFolders[index].Name,
                Type = SettingsItemType.Text,
                FavoriteIndex = index,
                GetValue = c => index < c.FavoriteFolders.Count
                    ? c.FavoriteFolders[index].Path
                    : "",
                SetValue = (c, v) =>
                {
                    if (index < c.FavoriteFolders.Count)
                        c.FavoriteFolders[index].Path = v;
                },
            });
            items.Add(new SettingsItem
            {
                Label = $"  └ Default Branch",
                Type = SettingsItemType.Text,
                FavoriteIndex = index,
                GetValue = c => index < c.FavoriteFolders.Count
                    ? c.FavoriteFolders[index].DefaultBranch
                    : "",
                SetValue = (c, v) =>
                {
                    if (index < c.FavoriteFolders.Count)
                        c.FavoriteFolders[index].DefaultBranch = v;
                },
            });
        }

        items.Add(new SettingsItem
        {
            Label = "+ Add Favorite",
            Type = SettingsItemType.Action,
        });

        foreach (var host in config.RemoteHosts)
        {
            var hostName = host.Name;
            items.Add(new SettingsItem
            {
                Label = $"── {hostName} ──",
                Type = SettingsItemType.Action,
                RemoteHostName = hostName,
            });

            for (var i = 0; i < host.FavoriteFolders.Count; i++)
            {
                var index = i;
                items.Add(new SettingsItem
                {
                    Label = host.FavoriteFolders[index].Name,
                    Type = SettingsItemType.Text,
                    RemoteHostName = hostName,
                    FavoriteIndex = index,
                    GetValue = c =>
                    {
                        var h = c.RemoteHosts.FirstOrDefault(r => r.Name == hostName);
                        return h != null && index < h.FavoriteFolders.Count
                            ? h.FavoriteFolders[index].Path
                            : "";
                    },
                    SetValue = (c, v) =>
                    {
                        var h = c.RemoteHosts.FirstOrDefault(r => r.Name == hostName);
                        if (h != null && index < h.FavoriteFolders.Count)
                            h.FavoriteFolders[index].Path = v;
                    },
                });
                items.Add(new SettingsItem
                {
                    Label = $"  └ Default Branch",
                    Type = SettingsItemType.Text,
                    RemoteHostName = hostName,
                    FavoriteIndex = index,
                    GetValue = c =>
                    {
                        var h = c.RemoteHosts.FirstOrDefault(r => r.Name == hostName);
                        return h != null && index < h.FavoriteFolders.Count
                            ? h.FavoriteFolders[index].DefaultBranch
                            : "";
                    },
                    SetValue = (c, v) =>
                    {
                        var h = c.RemoteHosts.FirstOrDefault(r => r.Name == hostName);
                        if (h != null && index < h.FavoriteFolders.Count)
                            h.FavoriteFolders[index].DefaultBranch = v;
                    },
                });
            }

            items.Add(new SettingsItem
            {
                Label = "+ Add Remote Favorite",
                Type = SettingsItemType.Action,
                RemoteHostName = hostName,
            });
        }

        return items;
    }

    private static List<SettingsItem> BuildContainerItems(CccConfig config)
    {
        var items = new List<SettingsItem>();

        for (var i = 0; i < config.Containers.Count; i++)
        {
            var index = i;
            var container = config.Containers[index];
            var hostTag = container.RemoteHost != null ? $" @{container.RemoteHost}" : "";
            items.Add(new SettingsItem
            {
                Label = $"{container.Name}{hostTag}",
                Type = SettingsItemType.Text,
                ContainerIndex = index,
                GetValue = c => index < c.Containers.Count
                    ? c.Containers[index].Label ?? ""
                    : "",
                SetValue = (c, v) =>
                {
                    if (index < c.Containers.Count)
                        c.Containers[index].Label = string.IsNullOrWhiteSpace(v) ? null : v;
                },
            });
        }

        items.Add(new SettingsItem
        {
            Label = "+ Add Container",
            Type = SettingsItemType.Action,
        });

        return items;
    }

    private static List<SettingsItem> BuildAdvancedItems(CccConfig config) =>
    [
        new()
        {
            Label = "Skip Permissions",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.DangerouslySkipPermissions ? "ON" : "OFF",
            SetValue = (c, _) => c.DangerouslySkipPermissions = !c.DangerouslySkipPermissions,
        },
        new()
        {
            Label = "Open Config File",
            Type = SettingsItemType.Action,
        },
        new()
        {
            Label = "Reset Keybindings to Defaults",
            Type = SettingsItemType.Action,
        },
    ];
}
