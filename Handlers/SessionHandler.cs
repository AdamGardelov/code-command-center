using System.Diagnostics;
using CodeCommandCenter.Models;
using CodeCommandCenter.Services;
using CodeCommandCenter.UI;
using Spectre.Console;

namespace CodeCommandCenter.Handlers;

public class SessionHandler(
    AppState state,
    CccConfig config,
    FlowHelper flow,
    ISessionBackend backend,
    Action loadSessions,
    Action render,
    Action resetPaneCache)
{
    public void Create(bool claudeAvailable, string? preSelectRemote = null)
    {
        if (!claudeAvailable)
        {
            state.SetStatus("'claude' not found in PATH — install Claude Code first");
            return;
        }

        FlowHelper.RunFlow("New Session", () =>
        {
            var hasRemotes = config.RemoteHosts.Count > 0;
            var globalSkip = config.DangerouslySkipPermissions;
            // Step count is dynamic — recalculated after session type is chosen
            var step = 0;

            // Step: Target (only if remote hosts configured)
            RemoteHost? remoteHost = null;
            var sshVerified = false;
            if (preSelectRemote != null)
            {
                remoteHost = config.RemoteHosts.FirstOrDefault(h => h.Name == preSelectRemote);
                if (remoteHost != null)
                {
                    AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(new Style(Color.Grey70))
                        .Start($"[grey70]Checking connection to [white]{remoteHost.Name}[/]...[/]", _ =>
                        {
                            sshVerified = SshService.CheckConnectivity(remoteHost.Host);
                        });

                    if (!sshVerified)
                        AnsiConsole.MarkupLine($"[yellow]⚠ Could not verify connection to {Markup.Escape(remoteHost.Name)} — continuing anyway[/]");
                }
            }
            else if (hasRemotes)
            {
                FlowHelper.PrintStep(++step, "Target");
                remoteHost = flow.PickTarget();

                if (remoteHost != null)
                {
                    AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(new Style(Color.Grey70))
                        .Start($"[grey70]Checking connection to [white]{remoteHost.Name}[/]...[/]", _ =>
                        {
                            sshVerified = SshService.CheckConnectivity(remoteHost.Host);
                        });

                    if (!sshVerified)
                        AnsiConsole.MarkupLine($"[yellow]⚠ Could not verify connection to {Markup.Escape(remoteHost.Name)} — continuing anyway[/]");
                }
            }

            // Step: Directory
            FlowHelper.PrintStep(++step, "Directory");
            string? worktreeBranch = null;
            string? dir;

            if (remoteHost != null)
            {
                dir = flow.PickRemoteDirectory(remoteHost, sshVerified: sshVerified,
                          onWorktreeBranchCreated: branch => worktreeBranch = branch)
                      ?? throw new FlowCancelledException();
            }
            else
            {
                dir = flow.PickDirectory(
                          onWorktreeBranchCreated: branch => worktreeBranch = branch)
                      ?? throw new FlowCancelledException();

                dir = ConfigService.ExpandPath(dir);
                if (!Directory.Exists(dir))
                    throw new FlowCancelledException("Invalid directory");
            }

            // Step: Session type (Claude or Shell)
            FlowHelper.PrintStep(++step, "Type");
            var typePrompt = new SelectionPrompt<string>()
                .Title("[grey70]Session type[/]")
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .AddChoices("Claude", "Shell", FlowHelper.CancelChoice);
            var sessionType = AnsiConsole.Prompt(typePrompt);
            if (sessionType == FlowHelper.CancelChoice)
                throw new FlowCancelledException();
            var shellOnly = sessionType == "Shell";

            // Step: Name
            FlowHelper.PrintStep(++step, "Name");
            var dirName = worktreeBranch ?? dir.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? "session";
            var defaultName = FlowHelper.SanitizeSessionName(dirName);
            var existingNames = new HashSet<string>(state.Sessions.Select(s => s.Name), StringComparer.Ordinal);
            defaultName = FlowHelper.UniqueSessionName(defaultName, existingNames, " ");
            var name = flow.PromptWithDefault("Session name", defaultName);

            // Step: Description
            FlowHelper.PrintStep(++step, "Description");
            var description = flow.PromptOptional("Description", null);

            // Step: Color
            FlowHelper.PrintStep(++step, "Color");
            var color = flow.PickColor();

            // Step: Skip permissions (only for Claude sessions)
            var skipPermissions = false;
            if (!shellOnly)
                skipPermissions = FlowHelper.PromptSkipPermissions(config, ref step);

            // Create session
            var effectiveSkip = skipPermissions || globalSkip;
            var claudeConfigDir = remoteHost == null && !shellOnly
                ? ConfigService.ResolveClaudeConfigDir(config, dir)
                : null;
            var error = backend.CreateSession(name, dir, claudeConfigDir, remoteHost?.Name,
                effectiveSkip, shellOnly: shellOnly);
            if (error != null)
                throw new FlowCancelledException(error);

            if (!string.IsNullOrWhiteSpace(description))
                ConfigService.SaveDescription(config, name, description);
            if (color != null)
                ConfigService.SaveColor(config, name, color);
            if (remoteHost != null)
                ConfigService.SaveRemoteHost(config, name, remoteHost.Name);
            if (effectiveSkip && !shellOnly)
                ConfigService.SetSkipPermissions(config, name, true);
            backend.ApplyStatusColor(name, color ?? "grey42");
            if (remoteHost == null)
                backend.AttachSession(name);
            loadSessions();
            resetPaneCache();
        }, state);
    }

    public void ReviewPr(bool claudeAvailable)
    {
        if (!claudeAvailable)
        {
            state.SetStatus("'claude' not found in PATH — install Claude Code first");
            return;
        }

        FlowHelper.RunFlow("Review PR", () =>
        {
            var totalSteps = 2;
            var step = 0;

            // Step 1: Pick repo
            FlowHelper.PrintStep(++step, totalSteps, "Repository");
            var favorite = flow.PickGitFavorite()
                           ?? throw new FlowCancelledException();

            var repoPath = ConfigService.ExpandPath(favorite.Path);
            var repoName = favorite.Name;

            // Step 2: Pick PR
            FlowHelper.PrintStep(++step, totalSteps, "Pull Request");
            var pr = flow.PickPullRequest(repoPath, config.PrIncludeDrafts)
                     ?? throw new FlowCancelledException();

            // Create worktree for the PR branch
            var basePath = ConfigService.ExpandPath(config.WorktreeBasePath);
            var worktreeDest = Path.Combine(basePath, "reviews", pr.HeadBranch, repoName);

            string? worktreeError = null;
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(new Style(Color.Grey70))
                .Start($"[grey70]Creating worktree for [white]{Markup.Escape(pr.HeadBranch)}[/]...[/]", _ =>
                {
                    GitService.FetchPrune(repoPath);

                    if (Directory.Exists(worktreeDest))
                        return;

                    var (success, output) = GitService.CreateWorktreeFromExisting(repoPath, worktreeDest, pr.HeadBranch);
                    if (!success)
                        worktreeError = output;
                });

            if (worktreeError != null)
                throw new FlowCancelledException($"Worktree failed: {worktreeError}");

            // Build session
            var sessionName = FlowHelper.SanitizeSessionName($"review-{pr.Number}");
            var existingNames = new HashSet<string>(state.Sessions.Select(s => s.Name));
            sessionName = FlowHelper.UniqueSessionName(sessionName, existingNames, "-");

            var prompt = PrReviewPrompts.GetPrompt(config.PrReviewLanguage);
            var claudeConfigDir = ConfigService.ResolveClaudeConfigDir(config, worktreeDest);
            var error = backend.CreateSession(sessionName, worktreeDest, claudeConfigDir, initialPrompt: prompt);
            if (error != null)
                throw new FlowCancelledException(error);

            var color = FlowHelper.PickRandomUnusedColor(config);
            if (color != null)
                ConfigService.SaveColor(config, sessionName, color);
            ConfigService.SaveDescription(config, sessionName, $"PR #{pr.Number}: {pr.Title}");
            backend.ApplyStatusColor(sessionName, color ?? "grey42");
            backend.AttachSession(sessionName);
            loadSessions();
            resetPaneCache();
        }, state);
    }

    public void Delete()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        state.SetStatus($"Kill '{session.Name}'? (y/n)");
        render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            var killError = backend.KillSession(session.Name);
            if (killError == null)
            {
                ConfigService.RemoveDescription(config, session.Name);
                ConfigService.RemoveColor(config, session.Name);
                ConfigService.RemoveExcluded(config, session.Name);
                ConfigService.RemoveStartCommit(config, session.Name);
                ConfigService.RemoveRemoteHost(config, session.Name);
                ConfigService.RemoveSkipPermissions(config, session.Name);
                state.SetStatus("Session killed");
            }
            else
            {
                state.SetStatus(killError);
            }

            loadSessions();
        }
        else
        {
            state.SetStatus("Cancelled");
        }
    }

    public void Edit()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        FlowHelper.RunFlow($"Edit Session — {session.Name}", () =>
        {
            FlowHelper.PrintStep(1, 3, "Name");
            var newName = flow.PromptOptional("Name", session.Name);

            FlowHelper.PrintStep(2, 3, "Description");
            var newDesc = flow.PromptOptional("Description", session.Description);

            FlowHelper.PrintStep(3, 3, "Color");
            var newColor = flow.PickColor();

            var currentName = session.Name;
            var changed = false;

            if (!string.IsNullOrWhiteSpace(newName) && newName != currentName)
            {
                var renameError = backend.RenameSession(currentName, newName);
                if (renameError != null)
                    throw new FlowCancelledException(renameError);

                ConfigService.RenameDescription(config, currentName, newName);
                ConfigService.RenameColor(config, currentName, newName);
                ConfigService.RenameExcluded(config, currentName, newName);
                ConfigService.RenameStartCommit(config, currentName, newName);
                ConfigService.RenameRemoteHost(config, currentName, newName);
                ConfigService.RenameSkipPermissions(config, currentName, newName);
                currentName = newName;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(newDesc))
            {
                ConfigService.SaveDescription(config, currentName, newDesc);
                changed = true;
            }

            if (newColor != null)
            {
                ConfigService.SaveColor(config, currentName, newColor);
                backend.ApplyStatusColor(currentName, newColor);
                changed = true;
            }

            if (changed)
            {
                loadSessions();
                state.SetStatus("Session updated");
            }
            else
            {
                state.SetStatus("No changes");
            }
        }, state);
    }

    public void Attach(string sessionName)
    {
        var session = state.Sessions.FirstOrDefault(s => s.Name == sessionName);
        if (session == null)
            return;
        AttachSession(session);
    }

    public void Attach()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;
        AttachSession(session);
    }

    private void AttachSession(Session session)
    {
        if (session.IsOffline)
        {
            state.SetStatus($"Cannot attach — {session.RemoteHostName ?? "host"} is offline");
            return;
        }

        // Exit CCC's alternate screen so tmux attach renders to normal screen
        Console.Write("\e[?1049l"); // Leave alternate screen
        Console.Write("\e(B");      // Reset charset — previous remote session may have corrupted it
        Console.Write("\e[0m");     // Reset all attributes
        Console.Write("\e[?25h");   // Show cursor
        Console.Write("\e[2J\e[H"); // Clear screen + cursor home

        // Resize tmux window to full terminal size before attaching.
        // CCC shrinks windows to preview width for the sidebar — without this resize,
        // the session would display at preview width with tmux dot-filler on the right.
        backend.ResizeWindow(session.Name, Console.WindowWidth, Console.WindowHeight);
        backend.AttachSession(session.Name);

        // Cooldown is NOT reset on detach — prevents re-notifying for sessions
        // that are already idle. The transition detection handles re-notification
        // naturally when a session goes active and becomes idle again.

        // Re-enter CCC's alternate screen buffer
        Console.Write("\e(B");      // Reset charset on main screen
        Console.Write("\e[?1049h"); // Enter alternate screen buffer
        Console.Write("\e(B");      // Reset charset on alternate screen (separate state)
        Console.Write("\e[0m");     // Reset all attributes
        Console.Write("\e[?1003l\e[?1006l\e[?1015l\e[?1000l"); // Disable mouse tracking
        Console.Write("\e[2J");     // Clear screen
        Console.Write("\e[H");      // Cursor home
        Console.Write("\e[?25l");   // Re-hide cursor
        loadSessions();
        resetPaneCache();
        render();
    }

    public void ToggleExclude()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        ConfigService.ToggleExcluded(config, session.Name);
        session.IsExcluded = !session.IsExcluded;

        var label = session.IsExcluded ? "Excluded" : "Restored";
        state.SetStatus(label);
        resetPaneCache();
    }

    public void SendQuickKey(string key)
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        var error = backend.SendKeys(session.Name, key);
        if (error == null)
        {
            state.SetStatus($"Sent '{key}' to {session.Name}");
            resetPaneCache(); // Force pane refresh
        }
        else
        {
            state.SetStatus(error);
        }
    }

    public void SendText()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        state.IsInputMode = true;
        state.InputBuffer = "";
        state.InputTarget = session.Name;
    }

    public void AdoptRemoteSession()
    {
        var untracked = backend.GetUntrackedRemoteSessions();
        if (untracked.Count == 0)
        {
            state.SetStatus("No untracked remote sessions found");
            return;
        }

        FlowHelper.RunFlow("Adopt Remote Session", () =>
        {
            var totalSteps = config.DangerouslySkipPermissions ? 3 : 4;
            var step = 0;

            FlowHelper.PrintStep(++step, totalSteps, "Session");
            var prompt = new SelectionPrompt<string>()
                .Title("[grey70]Select a remote session to adopt[/]")
                .HighlightStyle(new Style(Color.White, Color.Grey70));

            foreach (var s in untracked)
            {
                var host = s.RemoteHostName ?? "unknown";
                var path = s.CurrentPath != null ? $"  [grey50]{Markup.Escape(s.CurrentPath)}[/]" : "";
                prompt.AddChoice($"{Markup.Escape(s.Name)}  [mediumpurple3]@{Markup.Escape(host)}[/]{path}");
            }

            prompt.AddChoice(FlowHelper.CancelChoice);

            var selected = AnsiConsole.Prompt(prompt);
            if (selected == FlowHelper.CancelChoice)
                throw new FlowCancelledException();

            // Parse session name from the selection (text before first double-space)
            var sessionName = Markup.Remove(selected.Split("  ")[0]);
            var session = untracked.FirstOrDefault(s => s.Name == sessionName)
                          ?? throw new FlowCancelledException("Session not found");

            // Step: Description
            FlowHelper.PrintStep(++step, totalSteps, "Description");
            var description = flow.PromptOptional("Description", null);

            // Step: Color
            FlowHelper.PrintStep(++step, totalSteps, "Color");
            var color = flow.PickColor();

            // Step: Skip permissions
            var skipPermissions = FlowHelper.PromptSkipPermissions(config, ref step, totalSteps);

            // Track the session
            ConfigService.SaveRemoteHost(config, session.Name, session.RemoteHostName!);
            if (!string.IsNullOrWhiteSpace(description))
                ConfigService.SaveDescription(config, session.Name, description);
            if (color != null)
            {
                ConfigService.SaveColor(config, session.Name, color);
                backend.ApplyStatusColor(session.Name, color);
            }
            if (skipPermissions || config.DangerouslySkipPermissions)
                ConfigService.SetSkipPermissions(config, session.Name, true);

            loadSessions();
            state.SetStatus($"Adopted '{session.Name}' from {session.RemoteHostName}");
        }, state);
    }

    public void OpenFolder()
    {
        var session = state.GetSelectedSession();
        if (session?.CurrentPath == null)
            return;

        if (session.RemoteHostName != null)
        {
            state.SetStatus("Open folder not available for remote sessions");
            return;
        }

        try
        {
            var command = FlowHelper.GetFileManagerCommand();
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                ArgumentList =
                {
                    session.CurrentPath
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process.Start(startInfo);
            state.SetStatus($"Opened folder: {session.CurrentPath}");
        }
        catch
        {
            state.SetStatus("Failed to open folder");
        }
    }

    public void OpenInIde()
    {
        var session = state.GetSelectedSession();
        if (session?.CurrentPath == null)
            return;

        if (session.RemoteHostName != null)
        {
            state.SetStatus("Open in IDE not available for remote sessions");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.IdeCommand))
        {
            state.SetStatus("Set ideCommand in settings first (press s)");
            return;
        }

        state.SetStatus(FlowHelper.LaunchWithIde(config.IdeCommand, session.CurrentPath)
            ? $"Opened in {config.IdeCommand}"
            : $"Failed to run '{config.IdeCommand}'");
    }
}
