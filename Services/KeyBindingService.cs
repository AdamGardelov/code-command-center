using CodeCommandCenter.Models;

namespace CodeCommandCenter.Services;

public static class KeyBindingService
{
    private static readonly List<KeyBinding> _defaults =
    [
        new()
        {
            ActionId = "navigate-up",
            Key = "k",
            Label = null,
            CanDisable = false,
            StatusBarOrder = -1
        },
        new()
        {
            ActionId = "navigate-down",
            Key = "j",
            Label = null,
            CanDisable = false,
            StatusBarOrder = -1
        },
        // Group 1: Interact (send input to sessions)
        new()
        {
            ActionId = "approve",
            Key = "Y",
            Label = "approve",
            CanDisable = true,
            StatusBarOrder = 10
        },
        new()
        {
            ActionId = "reject",
            Key = "N",
            Label = "reject",
            CanDisable = true,
            StatusBarOrder = 11
        },
        new()
        {
            ActionId = "send-text",
            Key = "S",
            Label = "send",
            CanDisable = true,
            StatusBarOrder = 12
        },
        // Group 2: CRUD (create, edit, delete)
        new()
        {
            ActionId = "new-session",
            Key = "n",
            Label = "new",
            CanDisable = true,
            StatusBarOrder = 20
        },
        new()
        {
            ActionId = "create-background",
            Key = "C",
            Label = "bg new",
            CanDisable = true,
            StatusBarOrder = 21
        },
        new()
        {
            ActionId = "new-group",
            Key = "g",
            Label = "group",
            CanDisable = true,
            StatusBarOrder = 22
        },
        new()
        {
            ActionId = "edit-session",
            Key = "e",
            Label = "edit",
            CanDisable = true,
            StatusBarOrder = 23
        },
        new()
        {
            ActionId = "delete-session",
            Key = "d",
            Label = "del",
            CanDisable = true,
            StatusBarOrder = 24
        },
        new()
        {
            ActionId = "toggle-exclude",
            Key = "x",
            Label = "hide",
            CanDisable = true,
            StatusBarOrder = 25
        },
        new()
        {
            ActionId = "move-to-group",
            Key = "m",
            Label = "move",
            CanDisable = true,
            StatusBarOrder = 26
        },
        new()
        {
            ActionId = "adopt-remote",
            Key = "a",
            Label = "adopt",
            CanDisable = true,
            StatusBarOrder = 27
        },
        new()
        {
            ActionId = "review-pr",
            Key = "p",
            Label = "review",
            CanDisable = true,
            StatusBarOrder = 28
        },
        // Group 3: Open (navigate to things)
        new()
        {
            ActionId = "attach",
            Key = "Enter",
            Label = "attach",
            CanDisable = true,
            StatusBarOrder = 30
        },
        new()
        {
            ActionId = "open-folder",
            Key = "f",
            Label = "folder",
            CanDisable = true,
            StatusBarOrder = 31
        },
        new()
        {
            ActionId = "open-ide",
            Key = "i",
            Label = "ide",
            CanDisable = true,
            StatusBarOrder = 32
        },
        new()
        {
            ActionId = "open-settings",
            Key = "s",
            Label = "settings",
            CanDisable = false,
            StatusBarOrder = 33
        },
        new()
        {
            ActionId = "toggle-expand",
            Key = "Space",
            Label = null,
            CanDisable = false,
            StatusBarOrder = -1
        },
        // Group 4: View (mode + exit)
        new()
        {
            ActionId = "toggle-grid",
            Key = "Ctrl+G",
            Label = "grid",
            CanDisable = true,
            StatusBarOrder = 40
        },
        new()
        {
            ActionId = "toggle-diff",
            Key = "D",
            Label = "diff",
            CanDisable = true,
            StatusBarOrder = 41
        },
        new()
        {
            ActionId = "update",
            Key = "u",
            Label = "update",
            CanDisable = true,
            StatusBarOrder = -1 // Shown conditionally by renderer when update available
        },
        new()
        {
            ActionId = "refresh",
            Key = "r",
            Label = null,
            CanDisable = true,
            StatusBarOrder = -1
        },
        new()
        {
            ActionId = "quit",
            Key = "q",
            Label = "quit",
            CanDisable = false,
            StatusBarOrder = 99
        },
        // Diff overlay actions (scoped — excluded from global keyMap)
        new()
        {
            ActionId = "diff-scroll-down",
            Key = "j",
            Label = "scroll ↓",
            CanDisable = true,
            StatusBarOrder = -1
        },
        new()
        {
            ActionId = "diff-scroll-up",
            Key = "k",
            Label = "scroll ↑",
            CanDisable = true,
            StatusBarOrder = -1
        },
        new()
        {
            ActionId = "diff-page-down",
            Key = "Space",
            Label = "page ↓",
            CanDisable = true,
            StatusBarOrder = -1
        },
        new()
        {
            ActionId = "diff-top",
            Key = "g",
            Label = "top",
            CanDisable = true,
            StatusBarOrder = -1
        },
        new()
        {
            ActionId = "diff-bottom",
            Key = "G",
            Label = "bottom",
            CanDisable = true,
            StatusBarOrder = -1
        },
        new()
        {
            ActionId = "diff-toggle-stats",
            Key = "s",
            Label = "files",
            CanDisable = true,
            StatusBarOrder = -1
        },
        new()
        {
            ActionId = "diff-close",
            Key = "q",
            Label = "back",
            CanDisable = true,
            StatusBarOrder = -1
        },
    ];

    public static List<KeyBinding> Resolve(CccConfig config)
    {
        var overrides = config.Keybindings;
        var result = new List<KeyBinding>(_defaults.Count);

        foreach (var def in _defaults)
        {
            if (!overrides.TryGetValue(def.ActionId, out var ovr))
            {
                result.Add(def);
                continue;
            }

            var enabled = !def.CanDisable || (ovr.Enabled ?? def.Enabled);

            // If label is explicitly set in override, use it (even if null/empty to hide).
            // Otherwise keep the default.
            var label = ovr.Label ?? def.Label;
            var order = string.IsNullOrEmpty(label) ? -1 : def.StatusBarOrder;

            result.Add(new KeyBinding
            {
                ActionId = def.ActionId,
                Key = ovr.Key ?? def.Key,
                Enabled = enabled,
                Label = label,
                CanDisable = def.CanDisable,
                StatusBarOrder = order,
            });
        }

        return result;
    }

    public static Dictionary<string, KeyBindingConfig> GetDefaultConfigs()
    {
        var result = new Dictionary<string, KeyBindingConfig>();
        foreach (var def in _defaults)
            result[def.ActionId] = new KeyBindingConfig
            {
                Key = def.Key,
                Enabled = def.CanDisable ? def.Enabled : null,
                Label = def.Label,
            };

        return result;
    }

    public static HashSet<string> GetValidActionIds() => _defaults.Select(d => d.ActionId).ToHashSet();

    public static Dictionary<string, string> BuildKeyMap(List<KeyBinding> bindings)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in bindings.Where(b => b.Enabled && !b.ActionId.StartsWith("diff-")))
            map[b.Key] = b.ActionId;

        return map;
    }
}
