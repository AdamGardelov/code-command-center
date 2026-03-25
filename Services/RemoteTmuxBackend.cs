using System.Diagnostics;
using CodeCommandCenter.Models;
using Spectre.Console;

namespace CodeCommandCenter.Services;

public class RemoteTmuxBackend : ISessionBackend
{
    private readonly RemoteHost _host;

    public bool IsOffline { get; private set; }

    public RemoteTmuxBackend(RemoteHost remoteHost)
    {
        _host = remoteHost;
    }

    public List<Session> ListSessions()
    {
        // Use | as separator instead of \t because the format string is shell-quoted
        // for SSH transport, and single quotes prevent \t interpretation
        const string sep = "|";
        var fmt = $"#{{session_name}}{sep}#{{session_created}}{sep}#{{session_attached}}{sep}#{{session_windows}}{sep}#{{pane_current_path}}{sep}#{{pane_dead}}";
        var (success, output) = Run("list-sessions", "-F", fmt);

        if (!success || output == null)
        {
            IsOffline = true;
            return [];
        }

        IsOffline = false;

        var sessions = new List<Session>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 4)
                continue;

            var session = new Session
            {
                Name = parts[0],
                IsAttached = parts[2] != "0",
                WindowCount = int.TryParse(parts[3], out var wc) ? wc : 0,
                CurrentPath = parts.Length > 4 ? parts[4] : null,
                IsDead = parts.Length > 5 && parts[5] == "1",
                RemoteHostName = _host.Name,
            };

            if (long.TryParse(parts[1], out var epoch))
                session.Created = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;

            sessions.Add(session);
        }

        return sessions.OrderBy(s => s.Created).ThenBy(s => s.Name).ToList();
    }

    public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null, bool dangerouslySkipPermissions = false, string? initialPrompt = null, bool shellOnly = false, string? containerName = null)
    {
        string shellCmd;
        if (shellOnly)
        {
            shellCmd = "bash -li";
        }
        else
        {
            var claudeCmd = dangerouslySkipPermissions ? "claude --dangerously-skip-permissions" : "claude";
            if (initialPrompt != null)
            {
                var escaped = initialPrompt.Replace("'", "'\\''");
                claudeCmd += $" '{escaped}'";
            }
            // Use tmux -c flag for working directory instead of cd && which gets
            // split by the remote SSH shell (SSH joins ArgumentList into one string)
            shellCmd = $"bash -lc '{claudeCmd.Replace("'", "'\\''")}'";
        }

        var args = new List<string>
        {
            "new-session", "-d", "-s", name, "-n", name,
            "-c", workingDirectory,
            "-e", $"CCC_SESSION_NAME={name}",
        };
        if (!string.IsNullOrEmpty(claudeConfigDir))
        {
            args.Add("-e");
            args.Add($"CLAUDE_CONFIG_DIR={claudeConfigDir}");
        }
        args.Add(shellCmd);

        var (success, error) = RunWithError([.. args]);
        if (!success)
            return error ?? "Failed to create remote tmux session";

        Run("set-option", "-t", name, "automatic-rename", "off");
        return null;
    }

    public string? KillSession(string name)
    {
        var (success, error) = RunWithError("kill-session", "-t", name);
        return success ? null : error ?? "Failed to kill remote session";
    }

    public string? RenameSession(string oldName, string newName)
    {
        var (success, error) = RunWithError("rename-session", "-t", oldName, newName);
        return success ? null : error ?? "Failed to rename remote session";
    }

    public void AttachSession(string name)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(_host.Host);
        // Pass as single command string so session names with spaces aren't split
        startInfo.ArgumentList.Add($"tmux attach-session -t {SshControlMasterService.ShellQuote(name)}");

        try
        {
            var process = Process.Start(startInfo);
            process?.WaitForExit();
        }
        catch
        {
            // attach failed silently
        }
    }

    public void DetachSession()
    {
        // No-op — tmux handles detach via Ctrl-b d
    }

    public string? SendKeys(string sessionName, string text)
    {
        var (success, error) = RunWithError("send-keys", "-t", sessionName, "-l", text);
        if (!success)
            return error ?? "Failed to send keys";

        Run("send-keys", "-t", sessionName, "Enter");
        return null;
    }

    public void ForwardKey(string sessionName, ConsoleKeyInfo key)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key >= ConsoleKey.A && key.Key <= ConsoleKey.Z)
        {
            Run("send-keys", "-t", sessionName, $"C-{(char)('a' + key.Key - ConsoleKey.A)}");
            return;
        }

        var tmuxKey = key.Key switch
        {
            ConsoleKey.Enter => "Enter",
            ConsoleKey.Backspace => "BSpace",
            ConsoleKey.Delete => "DC",
            ConsoleKey.Tab => "Tab",
            ConsoleKey.Escape => "Escape",
            ConsoleKey.UpArrow => "Up",
            ConsoleKey.DownArrow => "Down",
            ConsoleKey.LeftArrow => "Left",
            ConsoleKey.RightArrow => "Right",
            ConsoleKey.Home => "Home",
            ConsoleKey.End => "End",
            ConsoleKey.PageUp => "PPage",
            ConsoleKey.PageDown => "NPage",
            ConsoleKey.Insert => "IC",
            ConsoleKey.F1 => "F1",
            ConsoleKey.F2 => "F2",
            ConsoleKey.F3 => "F3",
            ConsoleKey.F4 => "F4",
            ConsoleKey.F5 => "F5",
            ConsoleKey.F6 => "F6",
            ConsoleKey.F7 => "F7",
            ConsoleKey.F8 => "F8",
            ConsoleKey.F9 => "F9",
            ConsoleKey.F10 => "F10",
            ConsoleKey.F11 => "F11",
            ConsoleKey.F12 => "F12",
            _ => null,
        };

        if (tmuxKey != null)
        {
            Run("send-keys", "-t", sessionName, tmuxKey);
            return;
        }

        if (key.KeyChar != '\0')
            Run("send-keys", "-t", sessionName, "-l", key.KeyChar.ToString());
    }

    public void ForwardLiteralBatch(string sessionName, string text)
    {
        if (text.Length > 0)
            Run("send-keys", "-t", sessionName, "-l", text);
    }

    public string? CapturePaneContent(string sessionName, int lines = 500)
    {
        var (_, output) = Run("capture-pane", "-t", sessionName, "-p", "-e", "-S", $"-{lines}");
        return output;
    }

    public void ResizeWindow(string sessionName, int width, int height) =>
        Run("resize-window", "-t", sessionName, "-x", width.ToString(), "-y", height.ToString());

    public void ResetWindowSize(string sessionName) =>
        Run("set-option", "-u", "-t", sessionName, "window-size");

    public void ApplyStatusColor(string sessionName, string? spectreColor)
    {
        if (string.IsNullOrWhiteSpace(spectreColor))
            return;

        try
        {
            var color = Style.Parse(spectreColor).Foreground;
            var hex = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
            Run("set-option", "-t", sessionName, "status-style", $"bg={hex},fg=white");
        }
        catch
        {
            // Invalid color name — skip silently
        }
    }

    public void DetectWaitingForInputBatch(List<Session> sessions)
    {
        if (sessions.Count == 0)
            return;

        foreach (var session in sessions)
        {
            if (session.IsDead || session.IsOffline)
            {
                session.IsWaitingForInput = false;
                session.IsIdle = false;
                continue;
            }

            // No hook state for remote sessions — always use pane content stability detection
            DetectWaitingByPaneContent(session);
        }
    }

    public bool IsAvailable() => true;

    public bool IsInsideHost() => false;

    public bool HasClaude() => true;

    public void Dispose()
    {
        SshControlMasterService.Disconnect(_host.Host);
    }

    private void DetectWaitingByPaneContent(Session session)
    {
        var (_, output) = Run("capture-pane", "-t", session.Name, "-p", "-S", "-20");
        if (output == null)
        {
            session.IsWaitingForInput = true;
            return;
        }

        var content = SessionContentAnalyzer.GetContentAboveStatusBar(output);

        if (content == session.PreviousContent)
            session.StableContentCount++;
        else
        {
            session.StableContentCount = 0;
            session.PreviousContent = content;
        }

        var isStable = session.StableContentCount >= SessionContentAnalyzer.StableThreshold;
        session.IsIdle = isStable && SessionContentAnalyzer.IsIdlePrompt(content);
        session.IsWaitingForInput = isStable && !session.IsIdle;
    }

    private (bool Success, string? Output) Run(params string[] args)
    {
        var (success, output, _) = SshControlMasterService.RunTmuxCommand(_host.Host, args);
        return (success, output);
    }

    private (bool Success, string? Error) RunWithError(params string[] args)
    {
        var (success, _, error) = SshControlMasterService.RunTmuxCommand(_host.Host, args);
        return (success, success ? null : (error ?? "Remote tmux command failed"));
    }
}
