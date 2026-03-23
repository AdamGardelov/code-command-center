using CodeCommandCenter.Enums;
using CodeCommandCenter.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CodeCommandCenter.UI;

public static class Renderer
{
    private static readonly IReadOnlyList<string> _spinnerFrames = Spinner.Known.Dots.Frames;
    private static readonly TimeSpan _spinnerInterval = Spinner.Known.Dots.Interval;

    public static string GetSpinnerFrame()
    {
        var index = (int)(DateTime.Now.Ticks / _spinnerInterval.Ticks % _spinnerFrames.Count);
        return _spinnerFrames[index];
    }

    public static IRenderable BuildLayout(AppState state, string? capturedPane)
    {
        if (state.MobileMode)
            return BuildMobileLayout(state);

        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("Main"),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildHeader(state));

        layout["Main"].SplitColumns(
            new Layout("Sessions").Size(35),
            new Layout("Preview"));

        layout["Sessions"].Update(BuildSessionPanel(state));
        layout["Preview"].Update(BuildPreviewPanel(state, capturedPane));
        layout["StatusBar"].Update(BuildStatusBar(state));

        return layout;
    }

    private static readonly string _version =
        typeof(Renderer).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static Columns BuildHeader(AppState state)
    {
        var versionText = $"[grey50]v{_version}[/]";
        if (state.LatestVersion != null)
            versionText += $" [yellow bold]v{state.LatestVersion} available · u to update[/]";

        var left = new Markup($"[mediumpurple3 bold] Code Command Center[/] {versionText}");

        var groupInfo = state.ActiveGroup != null
            ? $" [grey]│[/] [mediumpurple3]{Markup.Escape(state.ActiveGroup)}[/]"
            : "";
        var excludedCount = state.Sessions.Count(s => s.IsExcluded);
        var excludedInfo = excludedCount > 0 ? $" [grey50]· {excludedCount} excluded[/]" : "";
        var right = new Markup($"[grey]{state.Sessions.Count} session(s)[/]{excludedInfo}{groupInfo} ");

        return new Columns(left, right)
        {
            Expand = true
        };
    }

    private static IRenderable BuildSessionPanel(AppState state)
    {
        var treeItems = state.GetTreeItems();
        var rows = new List<IRenderable>();

        if (treeItems.Count == 0)
        {
            rows.Add(new Markup("  [grey]No sessions[/]"));
        }
        else
        {
            for (var i = 0; i < treeItems.Count; i++)
            {
                var isSelected = i == state.CursorIndex;
                switch (treeItems[i])
                {
                    case TreeItem.SessionItem si:
                        rows.Add(BuildSessionRow(si.Session, isSelected, si.GroupName != null));
                        break;
                    case TreeItem.GroupHeader gh:
                        rows.Add(BuildTreeGroupRow(gh, isSelected, state));
                        break;
                    case TreeItem.RepoItem ri:
                        rows.Add(BuildRepoRow(ri, isSelected));
                        break;
                    case TreeItem.MachineHeader mh:
                        // Count sessions under this machine
                        var machineSessionCount = 0;
                        for (var j = i + 1; j < treeItems.Count; j++)
                        {
                            if (treeItems[j] is TreeItem.MachineHeader)
                                break;
                            if (treeItems[j] is TreeItem.SessionItem)
                                machineSessionCount++;
                        }
                        rows.Add(BuildMachineHeaderRow(mh, isSelected, machineSessionCount));
                        break;
                }
            }
        }

        // Border color based on selected item
        Color borderColor;
        var selectedItem = treeItems.ElementAtOrDefault(state.CursorIndex);
        if (selectedItem is TreeItem.SessionItem { Session.ColorTag: not null } selSession)
            borderColor = Style.Parse(selSession.Session.ColorTag).Foreground;
        else if (selectedItem is TreeItem.GroupHeader { Group.Color: not null and not "" } selGroup)
            borderColor = Style.Parse(selGroup.Group.Color).Foreground;
        else if (selectedItem is TreeItem.RepoItem ri)
        {
            var riGroup = state.Groups.FirstOrDefault(g => g.Name == ri.GroupName);
            borderColor = riGroup is { Color: not null and not "" }
                ? Style.Parse(riGroup.Color).Foreground
                : Color.Grey42;
        }
        else if (selectedItem is TreeItem.MachineHeader)
            borderColor = Color.Grey42;
        else
            borderColor = Color.Grey42;

        return new Panel(new Rows(rows))
            .Header("[grey70] Workspace [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static Markup BuildSessionRow(Session session, bool isSelected, bool indented = false)
    {
        var indent = indented ? "      " : "   ";
        var rawName = Markup.Escape(session.Name);
        var spinner = Markup.Escape(GetSpinnerFrame());
        var skipIcon = session.SkipPermissions ? "[yellow]⚡[/]" : "";
        var nameWidth = indented ? 16 : 19;
        var name = rawName.PadRight(nameWidth);

        if (session.IsOffline)
        {
            var prefix = indented ? "     " : "  ";
            var escapedName = Markup.Escape(session.Name);
            var row = $"[grey35]{prefix}✗ {escapedName}[/]{skipIcon}";
            return isSelected
                ? new Markup($"[on grey15]{row}[/]")
                : new Markup(row);
        }

        if (session.IsDead)
        {
            if (session.IsExcluded)
            {
                if (isSelected)
                    return new Markup($"[grey50 on grey19]{indent} [grey42]†[/] {name}[/]{skipIcon}");
                return new Markup($"{indent} [grey42]†[/] [grey35]{name}[/]{skipIcon}");
            }

            if (isSelected)
            {
                var bg = session.ColorTag ?? "grey37";
                return new Markup($"[white on {bg}]{indent} † {name}[/]{skipIcon}");
            }

            return new Markup($"{indent} [red]†[/] [grey50]{name}[/]{skipIcon}");
        }

        var status = session.IsWaitingForInput ? "!" : session.IsIdle ? "✓" : spinner;

        if (session.IsExcluded)
        {
            var excludedStatus = session.IsWaitingForInput ? "[grey42]![/]" : session.IsIdle ? "[grey42]✓[/]" : $"[grey35]{spinner}[/]";
            if (isSelected)
                return new Markup($"[grey50 on grey19]{indent} {excludedStatus} {name}[/]{skipIcon}");
            return new Markup($"{indent} {excludedStatus} [grey35]{name}[/]{skipIcon}");
        }

        if (isSelected)
        {
            var bg = session.ColorTag ?? "grey37";
            return new Markup($"[white on {bg}]{indent} {status} {name}[/]{skipIcon}");
        }

        if (session.IsWaitingForInput)
            return new Markup($"{indent} [yellow bold]![/] [navajowhite1]{name}[/]{skipIcon}");
        if (session.IsIdle)
            return new Markup($"{indent} [grey50]✓[/] [navajowhite1]{name}[/]{skipIcon}");

        return new Markup($"{indent} [green]{spinner}[/] [navajowhite1]{name}[/]{skipIcon}");
    }

    private static Markup BuildRepoRow(TreeItem.RepoItem repo, bool isSelected)
    {
        var name = Markup.Escape(repo.RepoName).PadRight(19);
        if (isSelected)
            return new Markup($"[grey70 on grey19]       ○ {name}[/]");
        return new Markup($"       [grey42]○[/] [grey50]{name}[/]");
    }

    private static Markup BuildMachineHeaderRow(TreeItem.MachineHeader mh, bool isSelected, int sessionCount)
    {
        var expandIcon = mh.IsExpanded ? "\u25bc" : "\u25b6";
        var name = Markup.Escape(mh.HostName);
        var countLabel = $"({sessionCount})";

        if (mh.IsOffline)
        {
            var row = $"[grey35]{expandIcon} {name} {countLabel} [italic]offline[/][/]";
            return isSelected ? new Markup($"[on grey15]{row}[/]") : new Markup(row);
        }

        if (isSelected)
            return new Markup($"[grey70 on grey19]{expandIcon} {name} {countLabel}[/]");

        return new Markup($"[grey50]{expandIcon} {name} {countLabel}[/]");
    }

    private static Markup BuildTreeGroupRow(TreeItem.GroupHeader header, bool isSelected, AppState state)
    {
        var group = header.Group;
        var name = Markup.Escape(group.Name);
        // Exclude the root session (same name as group) from the count
        var liveCount = group.Sessions.Count(s => s != group.Name);
        var hasRootSession = group.Sessions.Contains(group.Name);
        var countLabel = liveCount > 0 ? $"({liveCount})" : "";
        var colorTag = !string.IsNullOrEmpty(group.Color) ? group.Color : "grey50";

        // Resolve root session status
        var spinner = Markup.Escape(GetSpinnerFrame());
        Session? rootSession = hasRootSession
            ? state.Sessions.FirstOrDefault(s => s.Name == group.Name)
            : null;
        var expandIcon = header.IsExpanded ? "\u25bc" : "\u25b6";

        if (isSelected)
        {
            var bg = !string.IsNullOrEmpty(group.Color) ? group.Color : "grey37";
            var status = rootSession switch
            {
                { IsWaitingForInput: true } => "!",
                { IsIdle: true } => "\u2713",
                not null => spinner,
                _ => " ",
            };
            return new Markup($"[white on {bg}]    {status} {expandIcon} {name,-12} {countLabel,-4} [/]");
        }

        if (liveCount == 0 && group.Repos.Count == 0 && !hasRootSession)
            return new Markup($"      [grey50]{expandIcon}[/] [grey50 strikethrough]{name,-12}[/] [grey42]{countLabel}[/]");

        var statusIcon = rootSession switch
        {
            { IsWaitingForInput: true } => "[yellow bold]![/]",
            { IsIdle: true } => "[grey50]\u2713[/]",
            not null => $"[green]{spinner}[/]",
            _ => " ",
        };
        return new Markup($"    {statusIcon} [{colorTag}]{expandIcon}[/] [{colorTag}]{name,-12}[/] [grey50]{countLabel}[/]");
    }

    private static Panel BuildPreviewPanel(AppState state, string? capturedPane,
        Session? sessionOverride = null)
    {
        var session = sessionOverride ?? state.GetSelectedSession();

        if (session == null)
        {
            // Check if cursor is on a group header or repo item
            var treeItems = state.GetTreeItems();
            var currentItem = treeItems.ElementAtOrDefault(state.CursorIndex);
            if (currentItem is TreeItem.GroupHeader gh)
                return BuildGroupPreviewPanel(gh.Group, state);
            if (currentItem is TreeItem.RepoItem ri)
                return BuildRepoPreviewPanel(ri, state);
            if (currentItem is TreeItem.MachineHeader mhPreview)
            {
                var machineCount = state.Sessions.Count(s =>
                    (s.RemoteHostName ?? AppState.LocalMachineKey) ==
                    (mhPreview.IsLocal ? AppState.LocalMachineKey : mhPreview.HostName));
                var label = mhPreview.IsOffline ? "offline" : $"{machineCount} session(s)";
                var previewText = mhPreview.IsLocal
                    ? $"[grey50]Local machine — {label}[/]"
                    : $"[grey50]{Markup.Escape(mhPreview.HostName)} — {label}[/]";

                return new Panel(new Markup(previewText))
                    .Header("[grey70] Machine [/]")
                    .BorderColor(Color.Grey42)
                    .Expand();
            }

            // Panel width = terminal - session panel (35) - panel borders (4)
            var panelWidth = Math.Max(20, Console.WindowWidth - 35 - 4);

            return new Panel(
                    new Rows(
                        new Text(""),
                        CenterFiglet("Code", panelWidth, Color.MediumPurple3),
                        CenterFiglet("Command center", panelWidth, Color.MediumPurple3),
                        new Text(""),
                        Align.Center(new Markup("[grey50]Select a session to see preview[/]"))))
                .Header("[grey70] Live Preview [/]")
                .BorderColor(Color.Grey42)
                .Expand();
        }

        var labelColor = session.ColorTag ?? "grey50";
        var rows = new List<IRenderable>();

        if (!string.IsNullOrWhiteSpace(session.Description))
            rows.Add(new Markup($" [{labelColor}]Desc:[/]     [italic grey70]{Markup.Escape(session.Description)}[/]"));

        if (session.RemoteHostName != null)
            rows.Add(new Markup($" [{labelColor}]Path:[/]     [mediumpurple3]{Markup.Escape(session.RemoteHostName)}[/] [grey]→[/] [white]{Markup.Escape(session.CurrentPath ?? "unknown")}[/]"));
        else
            rows.Add(new Markup($" [{labelColor}]Path:[/]     [white]{Markup.Escape(session.CurrentPath ?? "unknown")}[/]"));

        if (session.GitBranch != null)
            rows.Add(new Markup($" [{labelColor}]Branch:[/]   [aqua]{Markup.Escape(session.GitBranch)}[/]"));

        rows.Add(new Markup($" [{labelColor}]Created:[/]  [white]{session.Created?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown"}[/]"));
        rows.Add(new Markup($" [{labelColor}]Status:[/]   {StatusLabel(session)}"));
        if (session.SkipPermissions)
            rows.Add(new Markup($" [{labelColor}]Perms:[/]    [yellow bold]⚡ skip-permissions[/]"));
        rows.Add(new Rule().RuleStyle(Style.Parse(session.ColorTag ?? "grey42")));

        if (session.IsOffline)
        {
            return new Panel(new Markup("[grey35]Session is offline — host unreachable[/]"))
                .BorderColor(Color.Grey35)
                .Header("[grey35]Offline[/]");
        }

        if (!string.IsNullOrWhiteSpace(capturedPane))
        {
            // Preview width = terminal width - session panel (35) - borders (6) - padding (2)
            var maxWidth = Math.Max(20, Console.WindowWidth - 35 - 8);
            var lines = capturedPane.Split('\n');

            // Available height = terminal - header (1) - status bar (1) - panel border (2) - info rows (5)
            var availableLines = Math.Max(1, Console.WindowHeight - 9);

            // Always show the bottom of the pane output
            var offset = Math.Max(0, lines.Length - availableLines);
            var visibleLines = lines.AsSpan(offset,
                Math.Min(availableLines, lines.Length - offset));

            foreach (var line in visibleLines)
                rows.Add(AnsiParser.ParseLine(line, maxWidth));
        }
        else
        {
            rows.Add(new Markup("[grey] No pane content available[/]"));
        }

        var borderColor = session.ColorTag != null
            ? Style.Parse(session.ColorTag).Foreground
            : Color.Grey42;

        var headerColor = session.ColorTag ?? "grey70";
        var headerName = Markup.Escape(session.Name);
        return new Panel(new Rows(rows))
            .Header($"[{headerColor} bold] {headerName} [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static Markup FormatDiffStatLine(string line)
    {
        // Summary line: " N files changed, N insertions(+), N deletions(-)"
        if (line.Contains("changed") && (line.Contains("insertion") || line.Contains("deletion")))
        {
            var parts = line.Split(',');
            var result = new List<string>();
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Contains("insertion"))
                    result.Add($"[green]{Markup.Escape(trimmed)}[/]");
                else if (trimmed.Contains("deletion"))
                    result.Add($"[red]{Markup.Escape(trimmed)}[/]");
                else
                    result.Add($"[grey]{Markup.Escape(trimmed)}[/]");
            }

            return new Markup(" " + string.Join("[grey],[/] ", result));
        }

        // File line: " path/to/file | N +++---"
        var pipeIdx = line.IndexOf('|');
        if (pipeIdx >= 0)
        {
            var filePart = Markup.Escape(line[..pipeIdx]);
            var statPart = line[(pipeIdx + 1)..];

            var colored = new System.Text.StringBuilder();
            foreach (var c in statPart)
            {
                if (c == '+')
                    colored.Append("[green]+[/]");
                else if (c == '-')
                    colored.Append("[red]-[/]");
                else if (c == '[')
                    colored.Append("[[");
                else if (c == ']')
                    colored.Append("]]");
                else
                    colored.Append(c);
            }

            return new Markup($"[white]{filePart}[/][grey]|[/]{colored}");
        }

        return new Markup($" [grey]{Markup.Escape(line)}[/]");
    }

    private static Panel BuildGroupPreviewPanel(SessionGroup group, AppState state)
    {
        var colorTag = !string.IsNullOrEmpty(group.Color) ? group.Color : "grey50";
        var rows = new List<IRenderable>
        {
            new Markup($" [{colorTag}]Group:[/]     [white bold]{Markup.Escape(group.Name)}[/]"),
            new Markup($" [{colorTag}]Feature:[/]   [grey70]{Markup.Escape(group.Description)}[/]"),
            new Markup($" [{colorTag}]Sessions:[/]  [white]{group.Sessions.Count}[/]"),
            new Rule().RuleStyle(Style.Parse(colorTag))
        };

        // Show each session in the group with its status (exclude root session)
        var groupSessionNames = new HashSet<string>(group.Sessions);
        var groupSessions = state.Sessions
            .Where(s => groupSessionNames.Contains(s.Name) && s.Name != group.Name)
            .ToList();

        foreach (var session in groupSessions)
        {
            var spinner = Markup.Escape(GetSpinnerFrame());
            var status = session.IsDead ? "[red]†[/]" : session.IsWaitingForInput ? "[yellow bold]![/]" : session.IsIdle ? "[grey50]✓[/]" : $"[green]{spinner}[/]";
            var name = Markup.Escape(session.Name);
            var branch = session.GitBranch != null ? $" [aqua]{Markup.Escape(session.GitBranch)}[/]" : "";
            var path = session.CurrentPath != null ? $" [grey50]{Markup.Escape(ShortenPath(session.CurrentPath))}[/]" : "";
            var remote = session.RemoteHostName != null ? $" [mediumpurple3]@{Markup.Escape(session.RemoteHostName)}[/]" : "";
            var skip = session.SkipPermissions ? " [yellow]⚡[/]" : "";
            rows.Add(new Markup($"  {status} [white]{name}[/]{branch}{remote}{skip}{path}"));
        }

        // Show repos that don't have a live session yet
        if (group.Repos.Count > 0)
        {
            var liveNames = new HashSet<string>(groupSessions.Select(s => s.Name));
            foreach (var (repoName, _) in group.Repos)
            {
                if (!liveNames.Contains($"{group.Name}-{repoName}"))
                    rows.Add(new Markup($"  [grey42]○[/] [grey50]{Markup.Escape(repoName)}[/]"));
            }
        }

        if (groupSessions.Count == 0 && group.Repos.Count == 0)
            rows.Add(new Markup("  [grey50]All sessions have ended[/]"));

        rows.Add(new Text(""));
        var hint = !string.IsNullOrEmpty(group.WorktreePath)
            ? "  [grey]Press [/][grey70 bold]Enter[/][grey] to open · [/][grey70 bold]Space[/][grey] expand/collapse · [/][grey70 bold]Ctrl+G[/][grey] grid · [/][grey70 bold]e[/][grey] to edit[/]"
            : "  [grey]Press [/][grey70 bold]Space[/][grey] expand/collapse · [/][grey70 bold]Ctrl+G[/][grey] grid · [/][grey70 bold]e[/][grey] to edit[/]";
        rows.Add(new Markup(hint));

        var borderColor = !string.IsNullOrEmpty(group.Color)
            ? Style.Parse(group.Color).Foreground
            : Color.Grey42;

        return new Panel(new Rows(rows))
            .Header($"[{colorTag} bold] {Markup.Escape(group.Name)} [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static Panel BuildRepoPreviewPanel(TreeItem.RepoItem repo, AppState state)
    {
        var group = state.Groups.FirstOrDefault(g => g.Name == repo.GroupName);
        var colorTag = group is { Color: not null and not "" } ? group.Color : "grey50";
        var rows = new List<IRenderable>
        {
            new Markup($" [{colorTag}]Repo:[/]   [white bold]{Markup.Escape(repo.RepoName)}[/]"),
            new Markup($" [{colorTag}]Path:[/]   [white]{Markup.Escape(repo.RepoPath)}[/]"),
            new Markup($" [{colorTag}]Status:[/] [grey50]No session yet[/]"),
            new Text(""),
            new Markup("  [grey]Press [/][grey70 bold]Enter[/][grey] to create session and attach[/]"),
        };

        var borderColor = group is { Color: not null and not "" }
            ? Style.Parse(group.Color).Foreground
            : Color.Grey42;

        return new Panel(new Rows(rows))
            .Header($"[{colorTag} bold] {Markup.Escape(repo.RepoName)} [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static Markup BuildInputStatusBar(AppState state)
    {
        var target = Markup.Escape(state.InputTarget ?? "");
        var buffer = Markup.Escape(state.InputBuffer);
        var limit = state.InputBuffer.Length >= 450
            ? $" [grey50]({state.InputBuffer.Length}/500)[/]"
            : "";
        return new Markup(
            $" [grey70]Send to[/] [white]{target}[/][grey70]>[/] [white]{buffer}[/][grey]▌[/]{limit}" +
            $"  [grey50]Enter[/][grey] send · [/][grey50]Esc[/][grey] cancel[/]");
    }

    // ── Mobile mode rendering ──────────────────────────────────────────

    private static IRenderable BuildMobileLayout(AppState state)
    {
        var sessions = state.GetMobileVisibleSessions();
        var listHeight = Math.Max(1, Console.WindowHeight - 6);

        state.EnsureCursorVisible(listHeight);

        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("List"),
                new Layout("Detail").Size(4),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildMobileHeader(state, sessions.Count));
        layout["List"].Update(BuildMobileSessionList(state, sessions, listHeight));
        layout["Detail"].Update(BuildMobileDetailBar(state));
        layout["StatusBar"].Update(BuildMobileStatusBar(state));

        return layout;
    }

    private static Columns BuildMobileHeader(AppState state, int sessionCount)
    {
        var filterLabel = state.GetGroupFilterLabel();
        var left = new Markup($"[mediumpurple3 bold] CCC[/] [grey50]v{_version}[/] [grey]- {sessionCount} sessions[/]");
        var right = new Markup($"[grey50][[{Markup.Escape(filterLabel)}]][/] ");
        return new Columns(left, right)
        {
            Expand = true
        };
    }

    private static Rows BuildMobileSessionList(AppState state, List<Session> sessions, int listHeight)
    {
        var rows = new List<IRenderable>();

        if (sessions.Count == 0)
        {
            rows.Add(new Markup("  [grey]No sessions[/]"));
        }
        else
        {
            var end = Math.Min(state.TopIndex + listHeight, sessions.Count);
            for (var i = state.TopIndex; i < end; i++)
            {
                var session = sessions[i];
                var isSelected = i == state.CursorIndex;
                rows.Add(BuildMobileSessionRow(session, isSelected));
            }
        }

        // Pad to fill available height so old content doesn't bleed through
        while (rows.Count < listHeight)
            rows.Add(new Text(""));

        return new Rows(rows);
    }

    private static Markup BuildMobileSessionRow(Session session, bool isSelected)
    {
        var name = Markup.Escape(session.Name);
        var spinner = Markup.Escape(GetSpinnerFrame());

        if (session.IsOffline)
        {
            var hostInfo = session.RemoteHostName != null
                ? $" [grey35]({Markup.Escape(session.RemoteHostName)})[/]"
                : "";
            var row = $"[grey35]✗ {name}[/]{hostInfo}";
            return isSelected
                ? new Markup($"[on grey15]{row}[/]")
                : new Markup(row);
        }

        if (session.IsDead)
        {
            if (session.IsExcluded)
            {
                if (isSelected)
                    return new Markup($"[grey50 on grey19] [grey42]†[/] {name} [/]");
                return new Markup($" [grey42]†[/] [grey35]{name}[/]");
            }

            if (isSelected)
            {
                var bg = session.ColorTag ?? "grey37";
                return new Markup($"[white on {bg}] † {name} [/]");
            }

            return new Markup($" [red]†[/] [grey50]{name}[/]");
        }

        var status = session.IsWaitingForInput ? "!" : session.IsIdle ? "✓" : spinner;

        if (session.IsExcluded)
        {
            var excludedStatus = session.IsWaitingForInput ? "[grey42]![/]" : session.IsIdle ? "[grey42]✓[/]" : $"[grey35]{spinner}[/]";
            if (isSelected)
                return new Markup($"[grey50 on grey19] {excludedStatus} {name} [/]");
            return new Markup($" {excludedStatus} [grey35]{name}[/]");
        }

        if (isSelected)
        {
            var bg = session.ColorTag ?? "grey37";
            return new Markup($"[white on {bg}] {status} {name} [/]");
        }

        if (session.IsWaitingForInput)
            return new Markup($" [yellow bold]![/] [navajowhite1]{name}[/]");
        if (session.IsIdle)
            return new Markup($" [grey50]✓[/] [navajowhite1]{name}[/]");

        return new Markup($" [green]{spinner}[/] [navajowhite1]{name}[/]");
    }

    private static IRenderable BuildMobileDetailBar(AppState state)
    {
        var session = state.GetSelectedSession();
        var rows = new List<IRenderable>
        {
            new Rule().RuleStyle(Style.Parse("grey27"))
        };

        if (session == null)
        {
            rows.Add(new Markup(" [grey]No session selected[/]"));
            rows.Add(new Text(""));
            rows.Add(new Text(""));
            return new Rows(rows);
        }

        var color = session.ColorTag ?? "grey70";
        var remoteTag = session.RemoteHostName != null ? $" [mediumpurple3]@{Markup.Escape(session.RemoteHostName)}[/]" : "";
        rows.Add(new Markup($" [{color} bold]{Markup.Escape(session.Name)}[/]{remoteTag}"));

        var branch = session.GitBranch != null ? $"[aqua]{Markup.Escape(session.GitBranch)}[/]" : "[grey]no branch[/]";
        var path = session.CurrentPath != null ? $" [grey50]{Markup.Escape(ShortenPath(session.CurrentPath))}[/]" : "";
        rows.Add(new Markup($" {branch}{path}"));

        var statusText = session.IsDead
            ? "[red]session ended[/]"
            : session.IsWaitingForInput
                ? "[yellow bold]waiting for input[/]"
                : session.IsIdle
                    ? "[grey50]idle[/]"
                    : session.IsAttached
                        ? "[green]attached[/]"
                        : "[grey]working[/]";
        var desc = !string.IsNullOrWhiteSpace(session.Description)
            ? $" [grey50]- {Markup.Escape(session.Description)}[/]"
            : "";
        rows.Add(new Markup($" {statusText}{desc}"));

        return new Rows(rows);
    }

    private static Markup BuildMobileStatusBar(AppState state)
    {
        if (state.IsInputMode)
            return BuildInputStatusBar(state);

        var status = state.GetActiveStatus();
        if (status != null)
            return new Markup($" [yellow]{Markup.Escape(status)}[/]");

        var kb = state.Keybindings;
        var session = state.GetSelectedSession();
        var parts = new List<string>();

        if (session?.IsWaitingForInput == true)
        {
            parts.Add(HintFor(kb, "approve"));
            parts.Add(HintFor(kb, "reject"));
        }

        parts.Add(HintFor(kb, "send-text", "send"));
        parts.Add(HintFor(kb, "attach"));

        if (state.Groups.Count > 0)
            parts.Add("[grey70 bold]g[/][grey] filter [/]"); // hardcoded — not a rebindable action

        parts.Add(HintFor(kb, "quit"));

        parts.RemoveAll(string.IsNullOrEmpty);
        return new Markup(" " + string.Join(" ", parts));
    }

    // ── Shared helpers ──────────────────────────────────────────────────

    private static string HintFor(IReadOnlyList<KeyBinding> bindings, string actionId,
        string? labelOverride = null)
    {
        var binding = bindings.FirstOrDefault(b => b.ActionId == actionId);
        if (binding == null || !binding.Enabled)
            return "";
        var label = labelOverride ?? binding.Label ?? actionId;
        return $"[grey70 bold]{Markup.Escape(binding.Key)}[/][grey] {Markup.Escape(label)} [/]";
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home))
            return "~" + path[home.Length..];
        return path;
    }

    private static Markup BuildStatusBar(AppState state)
    {
        if (state.IsInputMode)
            return BuildInputStatusBar(state);

        var status = state.GetActiveStatus();
        if (status != null)
            return new Markup($" [yellow]{Markup.Escape(status)}[/]");

        var visible = state.Keybindings
            .Where(b => b.Enabled && b.Label != null && b.StatusBarOrder >= 0)
            .OrderBy(b => b.StatusBarOrder)
            .ToList();

        if (visible.Count == 0)
            return new Markup(" ");

        var hiddenWhenNoGroups = new HashSet<string> { "move-to-group" };
        var hiddenWhenNoUntracked = new HashSet<string> { "adopt-remote" };
        var hasGroups = state.Groups.Count > 0;

        // Check if cursor is on a group header — dim session-only actions
        var treeItems = state.GetTreeItems();
        var currentItem = treeItems.ElementAtOrDefault(state.CursorIndex);
        var onGroupHeader = currentItem is TreeItem.GroupHeader or TreeItem.RepoItem;
        var sessionOnlyActions = new HashSet<string> { "approve", "reject", "send-text" };

        var parts = new List<string>();
        var prevGroup = -1;

        foreach (var b in visible)
        {
            if (!hasGroups && hiddenWhenNoGroups.Contains(b.ActionId))
                continue;
            if (!state.HasUntrackedRemoteSessions && hiddenWhenNoUntracked.Contains(b.ActionId))
                continue;
            var barGroup = b.StatusBarOrder / 10;
            if (prevGroup >= 0 && barGroup != prevGroup)
                parts.Add("[grey]│[/]");
            prevGroup = barGroup;

            var dimmed = onGroupHeader && sessionOnlyActions.Contains(b.ActionId);
            var keyColor = dimmed ? "grey35" : "grey70 bold";
            var labelColor = dimmed ? "grey27" : "grey";
            parts.Add($"[{keyColor}]{Markup.Escape(b.Key)}[/][{labelColor}] {Markup.Escape(b.Label!)} [/]");
        }

        return new Markup(" " + string.Join(" ", parts));
    }

    private static Padder CenterFiglet(string text, int availableWidth, Color color)
    {
        var figlet = new FigletText(text)
        {
            Pad = false
        }.Color(color).LeftJustified();
        var options = RenderOptions.Create(AnsiConsole.Console, AnsiConsole.Console.Profile.Capabilities);
        var measured = ((IRenderable)figlet).Measure(options, availableWidth);
        var leftPad = Math.Max(0, (availableWidth - measured.Max) / 2);
        return new Padder(figlet).PadLeft(leftPad).PadRight(0).PadTop(0).PadBottom(0);
    }

    private static string StatusLabel(Session session)
    {
        if (session.IsDead)
            return "[red]session ended[/]";
        if (session.IsWaitingForInput)
            return "[yellow bold]waiting for input[/]";
        if (session.IsIdle)
            return "[grey50]idle[/]";

        return session.IsAttached
            ? "[green]attached[/]"
            : "[grey]detached[/]";
    }

    // ── Diff Overlay View ────────────────────────────────────────────

    public static IRenderable BuildDiffOverlayLayout(AppState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("Main"),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildDiffOverlayHeader(state));
        layout["Main"].Update(BuildDiffOverlayContent(state));
        layout["StatusBar"].Update(BuildDiffOverlayStatusBar(state));

        return layout;
    }

    private static Columns BuildDiffOverlayHeader(AppState state)
    {
        var name = state.DiffOverlaySessionName ?? "?";
        var branch = state.DiffOverlayBranch != null
            ? $" [aqua]{Markup.Escape(state.DiffOverlayBranch)}[/]"
            : "";

        // File position indicator (e.g., "file 3/7")
        var fileInfo = "";
        if (state.DiffFileBoundaries.Length > 0)
        {
            var currentFile = 0;
            for (var i = 0; i < state.DiffFileBoundaries.Length; i++)
                if (state.DiffFileBoundaries[i] <= state.DiffScrollOffset)
                    currentFile = i + 1;
            if (currentFile > 0)
                fileInfo = $" [grey50]file {currentFile}/{state.DiffFileBoundaries.Length}[/]";
        }

        var totalLines = state.DiffOverlayLines.Length;
        var viewportHeight = Math.Max(1, Console.WindowHeight - 4 - DiffStatRowCount(state));
        var maxScroll = Math.Max(0, totalLines - viewportHeight);
        var pct = maxScroll > 0 ? (int)(100.0 * state.DiffScrollOffset / maxScroll) : 100;
        var scrollInfo = $"[grey50]{state.DiffScrollOffset + 1}-{Math.Min(state.DiffScrollOffset + viewportHeight, totalLines)}/{totalLines} ({pct}%)[/]";

        var left = new Markup($"[mediumpurple3 bold] Diff[/] [white bold]{Markup.Escape(name)}[/]{branch}");
        var right = new Markup($"{fileInfo} {scrollInfo} ");

        return new Columns(left, right)
        {
            Expand = true
        };
    }

    private static Panel BuildDiffOverlayContent(AppState state)
    {
        var rows = new List<IRenderable>();
        var maxWidth = Math.Max(20, Console.WindowWidth - 4);

        // Stat summary at top (collapsed by default, toggle with keybinding)
        if (state.DiffStatExpanded && !string.IsNullOrWhiteSpace(state.DiffOverlayStatSummary))
        {
            var statLines = state.DiffOverlayStatSummary.Split('\n');
            var maxStatLines = MaxDiffStatLines();
            var truncated = statLines.Length > maxStatLines;
            var displayLines = truncated ? statLines[..maxStatLines] : statLines;
            foreach (var line in displayLines)
                rows.Add(FormatDiffStatLine(line));
            if (truncated)
                rows.Add(new Markup($"[grey50]  … and {statLines.Length - maxStatLines} more files[/]"));
            rows.Add(new Rule().RuleStyle(Style.Parse("grey27")));
        }

        // Calculate viewport
        var statRowCount = rows.Count;
        var availableHeight = Math.Max(1, Console.WindowHeight - 4 - statRowCount);
        var totalLines = state.DiffOverlayLines.Length;
        var offset = Math.Clamp(state.DiffScrollOffset, 0, Math.Max(0, totalLines - 1));
        var visibleCount = Math.Min(availableHeight, totalLines - offset);

        for (var i = 0; i < visibleCount; i++)
            rows.Add(FormatDiffPatchLine(state.DiffOverlayLines[offset + i], maxWidth));

        // Pad remaining lines to fill screen
        var padding = availableHeight - visibleCount;
        for (var i = 0; i < padding; i++)
            rows.Add(new Text(""));

        return new Panel(new Rows(rows))
            .BorderColor(Color.Grey42)
            .Expand();
    }

    private static Markup FormatDiffPatchLine(string line, int maxWidth)
    {
        if (line.Length > maxWidth)
            line = line[..maxWidth];

        var escaped = Markup.Escape(line);
        var pad = new string(' ', Math.Max(0, maxWidth - line.Length));

        // File header — prominent separator with background
        if (line.StartsWith("diff --git "))
        {
            // Extract just the file path (b/path)
            var parts = line.Split(" b/");
            var fileName = parts.Length > 1 ? "b/" + parts[^1] : line["diff --git ".Length..];
            return new Markup($"[bold white on grey19] {Markup.Escape(fileName)}{new string(' ', Math.Max(0, maxWidth - fileName.Length - 1))}[/]");
        }

        // File metadata — de-emphasized
        if (line.StartsWith("+++") || line.StartsWith("---"))
            return new Markup($"[grey42]{escaped}[/]");
        if (line.StartsWith("index "))
            return new Markup($"[grey30]{escaped}[/]");

        // Additions — bright green text on subtle dark background
        if (line.StartsWith('+'))
            return new Markup($"[#80e080 on #1a3320]{escaped}{pad}[/]");

        // Deletions — bright red text on subtle dark background
        if (line.StartsWith('-'))
            return new Markup($"[#f0a0a0 on #3d1e1e]{escaped}{pad}[/]");

        // Hunk header — cyan markers, grey function context
        if (!line.StartsWith("@@"))
            return new Markup($"[grey58]{escaped}[/]");

        // Split at closing @@ to separate line range from function context
        var endMarker = line.IndexOf("@@", 2, StringComparison.Ordinal);
        if (endMarker <= 0)
            return new Markup($"[cyan]{escaped}[/]");

        var marker = Markup.Escape(line[..(endMarker + 2)]);
        var context = Markup.Escape(line[(endMarker + 2)..]);
        return new Markup($"[cyan]{marker}[/][italic grey50]{context}[/]");

        // Context lines — subtle
    }

    /// <summary>Max stat lines shown when the diff stat section is expanded (1/3 of screen, minimum 5).</summary>
    private static int MaxDiffStatLines() => Math.Max(5, (Console.WindowHeight - 4) / 3);

    /// <summary>Number of rows the stat section occupies (0 when collapsed).</summary>
    public static int DiffStatRowCount(AppState state)
    {
        if (!state.DiffStatExpanded || string.IsNullOrWhiteSpace(state.DiffOverlayStatSummary))
            return 0;
        var rawLines = state.DiffOverlayStatSummary.Split('\n').Length;
        var maxLines = MaxDiffStatLines();
        return Math.Min(rawLines, maxLines)
               + (rawLines > maxLines ? 1 : 0) // truncation indicator
               + 1; // separator rule
    }

    private static Markup BuildDiffOverlayStatusBar(AppState state)
    {
        var kb = state.Keybindings;
        var parts = new List<string>
        {
            // File navigation with arrows
            "[grey70 bold]↑/↓[/][grey] file [/]"
        };

        // Scroll: combine up/down keys
        var scrollDown = kb.FirstOrDefault(b => b.ActionId == "diff-scroll-down" && b.Enabled);
        var scrollUp = kb.FirstOrDefault(b => b.ActionId == "diff-scroll-up" && b.Enabled);
        if (scrollDown != null || scrollUp != null)
        {
            var keys = new[]
                {
                    scrollDown?.Key, scrollUp?.Key
                }
                .Where(k => k != null).Select(k => Markup.Escape(k!));
            parts.Add($"[grey70 bold]{string.Join("/", keys)}[/][grey] scroll [/]");
        }

        // Page down: rebindable key + PgDn fallback
        var pageDown = kb.FirstOrDefault(b => b.ActionId == "diff-page-down" && b.Enabled);
        if (pageDown != null)
            parts.Add($"[grey70 bold]{Markup.Escape(pageDown.Key)}/PgDn[/][grey] page down [/]");
        else
            parts.Add("[grey70 bold]PgDn[/][grey] page down [/]");

        parts.Add("[grey70 bold]PgUp[/][grey] page up [/]");
        parts.Add(HintFor(kb, "diff-top"));
        parts.Add(HintFor(kb, "diff-bottom"));
        // Toggle file stats
        var toggleStats = kb.FirstOrDefault(b => b.ActionId == "diff-toggle-stats" && b.Enabled);
        if (toggleStats != null)
            parts.Add($"[grey70 bold]{Markup.Escape(toggleStats.Key)}[/][grey] {(state.DiffStatExpanded ? "hide" : "show")} files [/]");

        parts.Add("[grey]│[/]");

        // Close: Esc always works + rebindable key
        var close = kb.FirstOrDefault(b => b.ActionId == "diff-close" && b.Enabled);
        if (close != null)
            parts.Add($"[grey70 bold]Esc/{Markup.Escape(close.Key)}[/][grey] back [/]");
        else
            parts.Add("[grey70 bold]Esc[/][grey] back [/]");

        parts.RemoveAll(string.IsNullOrEmpty);
        return new Markup(" " + string.Join(" ", parts));
    }

    // ── Settings View ──────────────────────────────────────────────

    public static IRenderable BuildSettingsLayout(AppState state, CccConfig config)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("Main"),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildSettingsHeader());

        var categories = SettingsDefinition.GetCategories();
        var selectedCategory = categories[Math.Clamp(state.SettingsCategory, 0, categories.Count - 1)];
        var items = selectedCategory.BuildItems(config);

        layout["Main"].SplitColumns(
            new Layout("Categories").Size(22),
            new Layout("Settings"));

        layout["Categories"].Update(BuildCategoryPanel(categories, state));
        layout["Settings"].Update(BuildSettingsPanel(selectedCategory, items, state, config));
        layout["StatusBar"].Update(BuildSettingsStatusBar(state, items));

        return layout;
    }

    private static Columns BuildSettingsHeader()
    {
        var left = new Markup("[mediumpurple3 bold] Code Command Center[/] [grey50]Settings[/]");
        var right = new Markup("[grey50]Esc to return [/]");
        return new Columns(left, right)
        {
            Expand = true
        };
    }

    private static Panel BuildCategoryPanel(List<SettingsCategory> categories, AppState state)
    {
        var rows = new List<IRenderable>();

        for (var i = 0; i < categories.Count; i++)
        {
            var cat = categories[i];
            var isSelected = i == state.SettingsCategory;
            var isFocused = !state.SettingsFocusRight;

            switch (isSelected)
            {
                case true when isFocused:
                    rows.Add(new Markup($"[white on grey37] {cat.Icon} {Markup.Escape(cat.Name),-16} [/]"));
                    break;
                case true:
                    rows.Add(new Markup($"[white] {cat.Icon} {Markup.Escape(cat.Name),-16} [/]"));
                    break;
                default:
                    rows.Add(new Markup($"[grey70]   {Markup.Escape(cat.Name),-16} [/]"));
                    break;
            }
        }

        var borderColor = !state.SettingsFocusRight ? Color.MediumPurple3 : Color.Grey42;

        return new Panel(new Rows(rows))
            .Header("[grey70] Categories [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static Panel BuildSettingsPanel(SettingsCategory category, List<SettingsItem> items,
        AppState state, CccConfig config)
    {
        var rows = new List<IRenderable>();
        var isFocused = state.SettingsFocusRight;

        if (items.Count == 0)
            rows.Add(new Markup("[grey]No settings in this category[/]"));
        else
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var isSelected = isFocused && i == state.SettingsItemCursor;
                rows.Add(BuildSettingsRow(item, isSelected, state, config));
            }

        var borderColor = isFocused ? Color.MediumPurple3 : Color.Grey42;

        return new Panel(new Rows(rows))
            .Header($"[grey70] {Markup.Escape(category.Name)} [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static IRenderable BuildSettingsRow(SettingsItem item, bool isSelected,
        AppState state, CccConfig config)
    {
        var label = Markup.Escape(item.Label);
        var maxValueWidth = Math.Max(10, Console.WindowWidth - 22 - 6 - item.Label.Length - 8);

        switch (item.Type)
        {
            case SettingsItemType.Toggle:
            {
                var value = item.GetValue?.Invoke(config) ?? "OFF";
                var isOn = value == "ON";
                var toggleColor = isOn ? "green" : "grey50";
                var toggleText = isOn ? " ON " : " OFF";

                if (item.ActionId != null && config.Keybindings.TryGetValue(item.ActionId, out var kb))
                {
                    var keyDisplay = isSelected && state.IsSettingsRebinding
                        ? "[yellow]Press a key...[/]"
                        : $"[grey70][[{Markup.Escape(kb.Key ?? "?")}]][/]";

                    return isSelected
                        ? new Markup($"[white on grey37] {label,-20} {keyDisplay}  [{toggleColor}]{toggleText}[/] [/]")
                        : new Markup($" {label,-20} [grey70][[{Markup.Escape(kb.Key ?? "?")}]][/]  [{toggleColor}]{toggleText}[/]");
                }

                return isSelected
                    ? new Markup($"[white on grey37] {label,-20} [{toggleColor}]{toggleText}[/] [/]")
                    : new Markup($" {label,-20} [{toggleColor}]{toggleText}[/]");
            }

            case SettingsItemType.Text:
            case SettingsItemType.Number:
            {
                var value = item.GetValue?.Invoke(config) ?? "";

                if (isSelected && state.IsSettingsEditing)
                {
                    var buf = Markup.Escape(state.SettingsEditBuffer);
                    return new Markup($"[white on grey37] {label,-20} [/][white on grey27] {buf}[grey]▌[/] [/]");
                }

                var displayValue = value.Length > maxValueWidth
                    ? value[..(maxValueWidth - 2)] + ".."
                    : value;
                var valueColor = string.IsNullOrEmpty(value) ? "grey42 italic" : "white";
                var displayText = string.IsNullOrEmpty(value) ? "(not set)" : Markup.Escape(displayValue);

                return isSelected
                    ? new Markup($"[white on grey37] {label,-20} [{valueColor}]{displayText}[/] [/]")
                    : new Markup($" [grey70]{label,-20}[/] [{valueColor}]{displayText}[/]");
            }

            case SettingsItemType.Action:
            {
                return isSelected
                    ? new Markup($"[white on grey37] {label,-20} [grey50]→[/] [/]")
                    : new Markup($" [mediumpurple3]{label}[/]");
            }

            default:
                return new Markup($" {label}");
        }
    }

    private static Markup BuildSettingsStatusBar(AppState state, List<SettingsItem> items)
    {
        if (state.IsSettingsRebinding)
        {
            var statusMsg = state.SettingsEditBuffer.Length > 0
                ? $" [yellow]{Markup.Escape(state.SettingsEditBuffer)}[/]  "
                : " ";
            return new Markup(
                statusMsg +
                "[grey70 bold]Press a key[/][grey] to bind · [/]" +
                "[grey70 bold]Esc[/][grey] cancel[/]");
        }

        if (state.IsSettingsEditing)
        {
            var limit = state.SettingsEditBuffer.Length >= 200
                ? $" [grey50]({state.SettingsEditBuffer.Length}/250)[/]"
                : "";
            return new Markup(
                $" [grey70]Editing>[/] [white]{Markup.Escape(state.SettingsEditBuffer)}[/][grey]▌[/]{limit}" +
                "  [grey50]Enter[/][grey] save · [/][grey50]Esc[/][grey] cancel[/]");
        }

        var itemHint = "";
        if (state.SettingsFocusRight && state.SettingsItemCursor < items.Count)
        {
            var item = items[state.SettingsItemCursor];
            itemHint = item.Type switch
            {
                SettingsItemType.Toggle when item.ActionId != null =>
                    "[grey70 bold]e[/][grey] rebind [/][grey70 bold]Enter[/][grey] toggle [/]",
                SettingsItemType.Toggle => "[grey70 bold]Enter[/][grey] toggle [/]",
                SettingsItemType.Text or SettingsItemType.Number =>
                    "[grey70 bold]Enter[/][grey] edit [/]",
                SettingsItemType.Action => "[grey70 bold]Enter[/][grey] execute [/]",
                _ => "",
            };
        }

        var favoritesHint = "";
        if (state.SettingsFocusRight)
        {
            var categories = SettingsDefinition.GetCategories();
            if (state.SettingsCategory < categories.Count && categories[state.SettingsCategory].Name == "Favorites")
                favoritesHint = "[grey70 bold]n[/][grey] add [/][grey70 bold]d[/][grey] delete [/]";
        }

        return new Markup(
            " [grey70 bold]j/k[/][grey] navigate [/]" +
            "[grey70 bold]Tab[/][grey] switch panel [/]" +
            "[grey]│[/] " +
            itemHint +
            favoritesHint +
            "[grey]│[/] " +
            "[grey70 bold]o[/][grey] open config [/]" +
            "[grey70 bold]Esc[/][grey] back [/]");
    }
}
