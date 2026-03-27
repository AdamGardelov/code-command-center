using System.Diagnostics;
using System.Text.Json;
using CodeCommandCenter.Models;
using CodeCommandCenter.Services;
using CodeCommandCenter.UI;
using Spectre.Console;

namespace CodeCommandCenter.Handlers;

public class FlowHelper(CccConfig config)
{
    private static string? _flowTitle;

    public const string CancelChoice = "Cancel";
    private const string _customPathChoice = "Custom path...";
    private const string _worktreeBrowseChoice = "⑂ Worktrees...";
    private const string _worktreePrefix = "\u2442 "; // ⑂

    private static readonly (string Label, string SpectreColor)[] _colorPalette =
    [
        ("Steel Blue", "SteelBlue"),
        ("Indian Red", "IndianRed"),
        ("Medium Purple", "MediumPurple"),
        ("Cadet Blue", "CadetBlue"),
        ("Light Salmon", "LightSalmon3"),
        ("Dark Sea Green", "DarkSeaGreen"),
        ("Dark Khaki", "DarkKhaki"),
        ("Plum", "Plum4"),
        ("Rosy Brown", "RosyBrown"),
        ("Grey Violet", "MediumPurple4"),
        ("Slate", "LightSlateGrey"),
        ("Dusty Teal", "DarkCyan"),
        ("Thistle", "Thistle3"),
    ];

    public static void RunFlow(string title, Action body, AppState state)
    {
        Console.CursorVisible = true;
        _flowTitle = title;
        Console.Clear();
        AnsiConsole.MarkupLine($"[mediumpurple3 bold] Code Command Center[/]  [grey]\u203a[/]  [white bold]{Markup.Escape(title)}[/]\n");
        try
        {
            body();
        }
        catch (FlowCancelledException ex)
        {
            state.SetStatus(ex.Message);
        }
        finally
        {
            Console.CursorVisible = false;
            _flowTitle = null;
        }
    }

    public static void PrintStep(int current, int total, string label)
    {
        Console.Clear();
        var title = _flowTitle ?? "";
        AnsiConsole.MarkupLine($"[mediumpurple3 bold] Code Command Center[/]  [grey]\u203a[/]  [white bold]{Markup.Escape(title)}[/]\n");
        var dots = new string[total];
        for (var i = 0; i < total; i++)
            dots[i] = i < current ? "[mediumpurple3]\u25cf[/]" : "[grey42]\u25cb[/]";
        AnsiConsole.MarkupLine($"{string.Join(" ", dots)}  [grey50]Step {current}/{total} \u2014 {Markup.Escape(label)}[/]");
    }

    public static void PrintStep(int current, string label)
    {
        Console.Clear();
        var title = _flowTitle ?? "";
        AnsiConsole.MarkupLine($"[mediumpurple3 bold] Code Command Center[/]  [grey]\u203a[/]  [white bold]{Markup.Escape(title)}[/]\n");
        AnsiConsole.MarkupLine($"[grey50]Step {current} \u2014 {Markup.Escape(label)}[/]");
    }

    public static bool PromptSkipPermissions(CccConfig config, ref int step, int totalSteps)
    {
        if (config.DangerouslySkipPermissions)
        {
            AnsiConsole.MarkupLine("[grey70]Global skip-permissions is [white]ON[/] — all sessions use it[/]");
            return false;
        }

        PrintStep(++step, totalSteps, "Skip Permissions");
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey70]Launch with [white]--dangerously-skip-permissions[/]?[/]")
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .AddChoices("No", "Yes"));
        return choice == "Yes";
    }

    public static bool PromptSkipPermissions(CccConfig config, ref int step)
    {
        if (config.DangerouslySkipPermissions)
        {
            AnsiConsole.MarkupLine("[grey70]Global skip-permissions is [white]ON[/] — all sessions use it[/]");
            return false;
        }

        PrintStep(++step, "Skip Permissions");
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey70]Launch with [white]--dangerously-skip-permissions[/]?[/]")
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .AddChoices("No", "Yes"));
        return choice == "Yes";
    }

    public static string RequireText(string prompt)
    {
        var result = AnsiConsole.Prompt(
            new TextPrompt<string>(prompt)
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        return string.IsNullOrWhiteSpace(result)
            ? throw new FlowCancelledException()
            : result;
    }

    private static string? PromptCustomPath()
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Working directory:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? CreateWorktreeWithProgress(string repoPath, string worktreeDest, string branchName, string startPoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(worktreeDest)!);

        string? error = null;
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey70))
            .Start($"[grey70]Creating worktree [white]{branchName}[/] from [white]{startPoint}[/]...[/]", _ =>
            {
                GitService.FetchPrune(repoPath);
                error = GitService.CreateWorktree(repoPath, worktreeDest, branchName, startPoint);
            });

        if (error != null)
        {
            AnsiConsole.MarkupLine($"[red]Worktree failed:[/] {Markup.Escape(error)}");
            AnsiConsole.MarkupLine("[grey](Press any key)[/]");
            Console.ReadKey(true);
            return null;
        }

        return worktreeDest;
    }

    public static string SanitizeSessionName(string name)
    {
        // tmux silently replaces dots and colons with underscores in session names
        return name.Replace('.', '_').Replace(':', '_');
    }

    public static string UniqueSessionName(string baseName, ICollection<string> existing, string separator = "-")
    {
        if (!existing.Contains(baseName))
            return baseName;

        for (var i = 2; i <= 99; i++)
        {
            var candidate = $"{baseName}{separator}{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }

        return baseName; // Shouldn't happen with max 8 sessions
    }

    public static List<WorktreeFeature> ScanWorktreeFeatures(string basePath)
    {
        var features = new List<WorktreeFeature>();

        foreach (var dir in Directory.GetDirectories(basePath))
        {
            var contextFile = Path.Combine(dir, ".feature-context.json");
            if (File.Exists(contextFile))
            {
                try
                {
                    var json = File.ReadAllText(contextFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var name = root.GetProperty("feature").GetString() ?? "";
                    var description = root.GetProperty("description").GetString() ?? "";

                    var repos = new Dictionary<string, string>();
                    if (root.TryGetProperty("repos", out var reposEl))
                    {
                        foreach (var repo in reposEl.EnumerateObject())
                        {
                            var worktreePath = repo.Value.GetProperty("worktree").GetString();
                            if (worktreePath != null && Directory.Exists(worktreePath))
                                repos[repo.Name] = worktreePath;
                        }
                    }

                    if (repos.Count > 0)
                        features.Add(new WorktreeFeature(name, description, dir, repos));
                }
                catch
                {
                    // Skip malformed context files
                }
            }
            else
            {
                // No context file — detect git worktree subdirectories
                var repos = new Dictionary<string, string>();
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var gitFile = Path.Combine(subDir, ".git");
                    if (File.Exists(gitFile) || Directory.Exists(gitFile))
                        repos[Path.GetFileName(subDir)] = subDir;
                }

                if (repos.Count > 0)
                {
                    var name = Path.GetFileName(dir);
                    features.Add(new WorktreeFeature(name, "", dir, repos));
                }
            }
        }

        return features;
    }

    public static void WriteFeatureContext(string featurePath, string featureName, Dictionary<string, string> worktrees)
    {
        var repos = new Dictionary<string, object>();
        foreach (var (name, path) in worktrees)
            repos[name] = new
            {
                worktree = path
            };

        var context = new
        {
            feature = featureName,
            description = "",
            repos,
        };

        var json = JsonSerializer.Serialize(context, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(Path.Combine(featurePath, ".feature-context.json"), json);
    }

    public static bool LaunchWithIde(string command, string path)
    {
        // On macOS, app names like "rider" aren't on PATH — use "open -a" to launch by app name
        if (OperatingSystem.IsMacOS() && !command.StartsWith('/') && !command.Contains('/'))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList =
                    {
                        "-a",
                        command,
                        path
                    },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                return true;
            }
            catch
            {
                // Fall through to direct launch
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = command,
                ArgumentList =
                {
                    path
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string GetFileManagerCommand()
    {
        if (OperatingSystem.IsMacOS())
            return "open";

        // Linux — check for WSL where explorer.exe is available
        try
        {
            var version = File.ReadAllText("/proc/version");
            if (version.Contains("microsoft", StringComparison.OrdinalIgnoreCase))
                return "explorer.exe";
        }
        catch
        {
            /* not WSL */
        }

        return "xdg-open";
    }

    public string PromptWithDefault(string label, string defaultValue)
    {
        const string changeChoice = "Change...";
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[grey70]{Markup.Escape(label)}[/]")
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .AddChoices(Markup.Escape(defaultValue), changeChoice, CancelChoice));

        if (selected == CancelChoice)
            throw new FlowCancelledException();
        if (selected == changeChoice)
            return RequireText($"[grey70]{Markup.Escape(label)}:[/]");
        return defaultValue;
    }

    public string PromptOptional(string label, string? currentValue)
    {
        const string changeChoice = "Change...";
        var skipLabel = !string.IsNullOrWhiteSpace(currentValue)
            ? $"Keep \"{Markup.Escape(currentValue)}\""
            : "Skip";

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[grey70]{Markup.Escape(label)}[/]")
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .AddChoices(skipLabel, changeChoice, CancelChoice));

        if (selected == CancelChoice)
            throw new FlowCancelledException();
        if (selected == changeChoice)
            return AnsiConsole.Prompt(
                new TextPrompt<string>($"[grey70]{Markup.Escape(label)}:[/]")
                    .AllowEmpty()
                    .PromptStyle(new Style(Color.White)));
        return "";
    }

    public string? PickDirectory(string? worktreeBranchHint = null, Action<string>? onWorktreeBranchCreated = null)
    {
        var favorites = config.FavoriteFolders;
        var gitFavorites = favorites.Where(f => GitService.IsGitRepo(ConfigService.ExpandPath(f.Path))).ToList();

        while (true)
        {
            var prompt = new SelectionPrompt<string>()
                .Title("[grey70]Pick a directory[/]")
                .PageSize(15)
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .MoreChoicesText("[grey](Move up and down to reveal more)[/]");

            foreach (var fav in favorites)
                prompt.AddChoice($"{fav.Name}  [grey50]{fav.Path}[/]");

            if (gitFavorites.Count > 0)
            {
                foreach (var fav in gitFavorites)
                    prompt.AddChoice($"{_worktreePrefix}{fav.Name}  [grey50](new worktree)[/]");
            }

            var wtBasePath = ConfigService.ExpandPath(config.WorktreeBasePath);
            if (Directory.Exists(wtBasePath) && Directory.GetDirectories(wtBasePath).Length > 0)
                prompt.AddChoice(_worktreeBrowseChoice);

            prompt.AddChoice(_customPathChoice);
            prompt.AddChoice(CancelChoice);

            var selected = AnsiConsole.Prompt(prompt);
            switch (selected)
            {
                case CancelChoice:
                    return null;
                case _customPathChoice:
                {
                    var custom = PromptCustomPath();
                    if (custom != null)
                        return custom;
                    continue; // empty input -> back to picker
                }
                case _worktreeBrowseChoice:
                {
                    var picked = PickWorktreeDirectory(wtBasePath);
                    if (picked != null)
                        return picked;
                    continue; // back to picker
                }
            }

            // Handle worktree selection
            if (selected.StartsWith(_worktreePrefix))
            {
                var repoName = selected[_worktreePrefix.Length..].Split("  ")[0];
                var fav = gitFavorites.FirstOrDefault(f => f.Name == repoName);
                if (fav == null)
                    continue;

                var hint = worktreeBranchHint;
                if (string.IsNullOrWhiteSpace(hint))
                {
                    hint = AnsiConsole.Prompt(
                        new TextPrompt<string>("[grey70]Name[/] [grey](used for branch and session)[/][grey70]:[/]")
                            .AllowEmpty()
                            .PromptStyle(new Style(Color.White)));
                    if (string.IsNullOrWhiteSpace(hint))
                        continue; // back to picker
                }

                var repoPath = ConfigService.ExpandPath(fav.Path);
                var branchName = GitService.SanitizeBranchName(hint);
                var basePath = ConfigService.ExpandPath(config.WorktreeBasePath);
                var worktreeDest = Path.Combine(basePath, branchName, repoName);
                var startPoint = GitService.GetDefaultBranch(repoPath, fav.DefaultBranch);

                var result = CreateWorktreeWithProgress(repoPath, worktreeDest, branchName, startPoint);
                if (result != null)
                    onWorktreeBranchCreated?.Invoke(branchName);
                return result;
            }

            // Match back to the favorite by prefix (name before the spacing)
            var selectedName = selected.Split("  ")[0];
            var match = favorites.FirstOrDefault(f => f.Name == selectedName);
            return match?.Path;
        }
    }

    private static string? PickWorktreeDirectory(string basePath)
    {
        // Collect leaf directories: basePath/{branch}/{repo}
        var entries = new List<(string Label, string Path)>();
        foreach (var branchDir in Directory.GetDirectories(basePath).OrderBy(d => d))
        {
            var branch = System.IO.Path.GetFileName(branchDir);
            var repoDirs = Directory.GetDirectories(branchDir);
            if (repoDirs.Length == 0)
            {
                // Branch dir itself (no repo subdirs)
                entries.Add((branch, branchDir));
                continue;
            }

            foreach (var repoDir in repoDirs.OrderBy(d => d))
            {
                var repo = System.IO.Path.GetFileName(repoDir);
                entries.Add(($"{branch}/{repo}", repoDir));
            }
        }

        if (entries.Count == 0)
            return null;

        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Pick a worktree[/]")
            .PageSize(15)
            .HighlightStyle(new Style(Color.White, Color.Grey70))
            .MoreChoicesText("[grey](Move up and down to reveal more)[/]");

        foreach (var (label, path) in entries)
            prompt.AddChoice($"{label}  [grey50]{path}[/]");

        prompt.AddChoice(CancelChoice);

        var selected = AnsiConsole.Prompt(prompt);
        if (selected == CancelChoice)
            return null;

        // Extract path from the selection
        var parts = selected.Split("  ", 2);
        return parts.Length > 1 ? Markup.Remove(parts[1]) : null;
    }

    /// <summary>
    /// Picks between Local and configured remote hosts.
    /// Returns null for local, or the RemoteHost for remote.
    /// Skips the picker entirely if no remote hosts are configured.
    /// </summary>
    public RemoteHost? PickTarget()
    {
        if (config.RemoteHosts.Count == 0)
            return null;

        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Where to run?[/]")
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        prompt.AddChoice("Local");
        foreach (var host in config.RemoteHosts)
            prompt.AddChoice($"{host.Name} ☁");
        prompt.AddChoice(CancelChoice);

        var selected = AnsiConsole.Prompt(prompt);

        if (selected == CancelChoice)
            throw new FlowCancelledException();
        if (selected == "Local")
            return null;

        return config.RemoteHosts.First(h => selected.StartsWith(h.Name));
    }

    public string? PickRemoteDirectory(RemoteHost remoteHost, bool sshVerified = true, Action<string>? onWorktreeBranchCreated = null)
    {
        var favorites = remoteHost.FavoriteFolders;

        // Check which favorites are git repos via SSH (for worktree icon)
        // Skip when SSH connectivity wasn't verified (e.g. password-based auth)
        var gitFavorites = new List<FavoriteFolder>();
        if (sshVerified)
        {
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(new Style(Color.Grey70))
                .Start($"[grey70]Checking repos on {Markup.Escape(remoteHost.Name)}...[/]", _ =>
                {
                    foreach (var fav in favorites)
                    {
                        if (SshService.IsGitRepo(remoteHost.Host, fav.Path))
                            gitFavorites.Add(fav);
                    }
                });
        }
        else
        {
            AnsiConsole.MarkupLine("[grey70]Skipping repo detection (no verified connection). Consider setting up SSH keys: [white]ssh-copy-id " + Markup.Escape(remoteHost.Host) + "[/][/]");
        }

        while (true)
        {
            var prompt = new SelectionPrompt<string>()
                .Title($"[grey70]Pick a directory on[/] [white]{Markup.Escape(remoteHost.Name)}[/]")
                .PageSize(15)
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .MoreChoicesText("[grey](Move up and down to reveal more)[/]");

            foreach (var fav in favorites)
                prompt.AddChoice($"{fav.Name}  [grey50]{fav.Path}[/]");

            if (gitFavorites.Count > 0)
            {
                foreach (var fav in gitFavorites)
                    prompt.AddChoice($"{_worktreePrefix}{fav.Name}  [grey50](new worktree)[/]");
            }

            prompt.AddChoice(_customPathChoice);
            prompt.AddChoice(CancelChoice);

            var selected = AnsiConsole.Prompt(prompt);
            switch (selected)
            {
                case CancelChoice:
                    return null;
                case _customPathChoice:
                {
                    var custom = PromptCustomPath();
                    if (custom != null)
                        return custom;
                    continue;
                }
            }

            // Handle worktree selection on remote
            if (selected.StartsWith(_worktreePrefix))
            {
                var repoName = selected[_worktreePrefix.Length..].Split("  ")[0];
                var fav = gitFavorites.FirstOrDefault(f => f.Name == repoName);
                if (fav == null)
                    continue;

                var hint = AnsiConsole.Prompt(
                    new TextPrompt<string>("[grey70]Name[/] [grey](used for branch and session)[/][grey70]:[/]")
                        .AllowEmpty()
                        .PromptStyle(new Style(Color.White)));
                if (string.IsNullOrWhiteSpace(hint))
                    continue;

                var branchName = GitService.SanitizeBranchName(hint);
                var basePath = remoteHost.WorktreeBasePath.TrimEnd('/');
                var worktreeDest = $"{basePath}/{branchName}/{repoName}";

                string? error = null;
                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(new Style(Color.Grey70))
                    .Start($"[grey70]Creating worktree [white]{Markup.Escape(branchName)}[/] on {Markup.Escape(remoteHost.Name)}...[/]", _ =>
                    {
                        GitService.FetchPrune(fav.Path, remoteHost.Host);
                        var startPoint = GitService.GetDefaultBranch(fav.Path, fav.DefaultBranch, remoteHost.Host);
                        error = GitService.CreateWorktree(fav.Path, worktreeDest, branchName, startPoint, remoteHost.Host);
                    });

                if (error != null)
                {
                    AnsiConsole.MarkupLine($"[red]Worktree failed:[/] {Markup.Escape(error)}");
                    AnsiConsole.MarkupLine("[grey](Press any key)[/]");
                    Console.ReadKey(true);
                    continue;
                }

                onWorktreeBranchCreated?.Invoke(branchName);
                return worktreeDest;
            }

            // Match back to the favorite by prefix
            var selectedName = selected.Split("  ")[0];
            var match = favorites.FirstOrDefault(f => f.Name == selectedName);
            return match?.Path;
        }
    }

    public string? PickColor()
    {
        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Color[/] [grey](optional)[/]")
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        var usedColors = new HashSet<string>(config.SessionColors.Values, StringComparer.OrdinalIgnoreCase);
        var hasUnused = _colorPalette.Any(c => !usedColors.Contains(c.SpectreColor));

        if (hasUnused)
            prompt.AddChoice("[grey70]\ud83c\udfb2  Just give me one[/]");
        prompt.AddChoice("None");
        foreach (var (label, spectreColor) in _colorPalette)
            prompt.AddChoice($"[{spectreColor}]\u2588\u2588\u2588\u2588[/]  {label}");
        prompt.AddChoice(CancelChoice);

        var selected = AnsiConsole.Prompt(prompt);

        if (selected == CancelChoice)
            throw new FlowCancelledException();
        if (selected == "None")
            return null;

        if (selected.Contains("Just give me one"))
        {
            var unused = _colorPalette.Where(c => !usedColors.Contains(c.SpectreColor)).ToArray();
            return unused[Random.Shared.Next(unused.Length)].SpectreColor;
        }

        foreach (var (label, spectreColor) in _colorPalette)
            if (selected.EndsWith(label))
                return spectreColor;

        return null;
    }

    public static string ResolveKeyId(ConsoleKeyInfo key)
    {
        // Ctrl+letter combos (Ctrl+A through Ctrl+Z)
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt)
            && key.Key >= ConsoleKey.A && key.Key <= ConsoleKey.Z)
            return $"Ctrl+{key.Key}";

        return key.Key switch
        {
            ConsoleKey.Enter => "Enter",
            ConsoleKey.Spacebar => "Space",
            ConsoleKey.Tab => "Tab",
            ConsoleKey.UpArrow => "UpArrow",
            ConsoleKey.DownArrow => "DownArrow",
            _ => key.KeyChar.ToString(),
        };
    }

    public FavoriteFolder? PickGitFavorite()
    {
        var gitFavorites = config.FavoriteFolders
            .Where(f => GitService.IsGitRepo(ConfigService.ExpandPath(f.Path)))
            .ToList();

        if (gitFavorites.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No git repos found in favorites[/]");
            AnsiConsole.MarkupLine("[grey](Press any key)[/]");
            Console.ReadKey(true);
            return null;
        }

        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Pick a repo[/]")
            .PageSize(15)
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        foreach (var fav in gitFavorites)
            prompt.AddChoice($"{fav.Name}  [grey50]{fav.Path}[/]");
        prompt.AddChoice(CancelChoice);

        var selected = AnsiConsole.Prompt(prompt);
        if (selected == CancelChoice)
            return null;

        var selectedName = selected.Split("  ")[0];
        return gitFavorites.FirstOrDefault(f => f.Name == selectedName);
    }

    public PullRequest? PickPullRequest(string repoPath, bool includeDrafts = false)
    {
        List<PullRequest>? prs = null;
        string? error = null;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey70))
            .Start("[grey70]Fetching open PRs...[/]", _ =>
            {
                (prs, error) = GitService.ListPullRequests(repoPath, includeDrafts);
            });

        if (error != null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            AnsiConsole.MarkupLine("[grey](Press any key)[/]");
            Console.ReadKey(true);
            return null;
        }

        if (prs == null || prs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No open PRs found[/]");
            AnsiConsole.MarkupLine("[grey](Press any key)[/]");
            Console.ReadKey(true);
            return null;
        }

        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Pick a PR to review[/]")
            .PageSize(15)
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        foreach (var pr in prs)
        {
            var draft = pr.IsDraft ? " [grey50](draft)[/]" : "";
            prompt.AddChoice($"[white]#{pr.Number}[/]  {Markup.Escape(pr.Title)}{draft}  [grey50]{Markup.Escape(pr.Author)} → {Markup.Escape(pr.HeadBranch)}[/]");
        }
        prompt.AddChoice(CancelChoice);

        var selected = AnsiConsole.Prompt(prompt);
        if (selected == CancelChoice)
            return null;

        var raw = Markup.Remove(selected);
        var numberStr = raw.Split(' ', 2)[0].TrimStart('#');
        if (int.TryParse(numberStr, out var num))
            return prs.FirstOrDefault(p => p.Number == num);

        return null;
    }

    public static string? PickRandomUnusedColor(CccConfig config)
    {
        var usedColors = new HashSet<string>(config.SessionColors.Values, StringComparer.OrdinalIgnoreCase);
        var unused = _colorPalette.Where(c => !usedColors.Contains(c.SpectreColor)).ToArray();
        return unused.Length > 0 ? unused[Random.Shared.Next(unused.Length)].SpectreColor : null;
    }
}
