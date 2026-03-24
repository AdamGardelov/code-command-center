using System.Diagnostics;

namespace CodeCommandCenter.Services;

public static class SshService
{
    /// <summary>
    /// Runs a command locally or on a remote host via SSH.
    /// When remoteHost is null, runs locally. When set, runs via ssh.
    /// </summary>
    public static (bool Success, string? Output) Run(string? remoteHost, string command)
    {
        return remoteHost == null ? RunLocal(command) : RunSsh(remoteHost, command);
    }

    /// <summary>
    /// Quick connectivity check with a timeout. Returns true if the host responds.
    /// </summary>
    public static bool CheckConnectivity(string remoteHost, int timeoutSeconds = 5)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add($"ConnectTimeout={timeoutSeconds}");
            startInfo.ArgumentList.Add(remoteHost);
            startInfo.ArgumentList.Add("echo ok");

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            var exited = process.WaitForExit(timeoutSeconds * 1000 + 2000);
            if (!exited)
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the filename and arguments for launching a Claude session.
    /// Local: shell -lic claude (interactive so .bashrc/.zshrc functions are available)
    /// Remote: ssh -t host 'cd path && claude'
    /// </summary>
    public static (string FileName, List<string> Args) BuildSessionCommand(
        string? remoteHost, string workingDirectory, bool dangerouslySkipPermissions = false, string? initialPrompt = null, bool shellOnly = false)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";

        if (shellOnly)
        {
            if (remoteHost == null)
                return (shell, ["-li"]);
            return ("ssh", ["-t", remoteHost, $"cd {EscapePath(workingDirectory)} && exec \"$SHELL\" -li"]);
        }

        var claudeCmd = dangerouslySkipPermissions ? "claude --dangerously-skip-permissions" : "claude";
        if (initialPrompt != null)
        {
            var escaped = initialPrompt.Replace("'", "'\\''");
            claudeCmd += $" '{escaped}'";
        }

        if (remoteHost == null)
            return (shell, ["-lic", claudeCmd]);

        // Wrap in exec $SHELL -lc so the remote gets a login shell (loads PATH etc.)
        // This mirrors the local behavior where we use "$SHELL -lc claude".
        return ("ssh", ["-t", remoteHost, $"cd {EscapePath(workingDirectory)} && exec \"$SHELL\" -lc {EscapeSegment(claudeCmd)}"]);
    }

    /// <summary>
    /// Checks if a path is a git repo, locally or remotely.
    /// </summary>
    public static bool IsGitRepo(string? remoteHost, string path)
    {
        if (remoteHost == null)
            return GitService.IsGitRepo(path);

        var (success, _) = RunSsh(remoteHost, $"git -C {EscapePath(path)} rev-parse --git-dir");
        return success;
    }

    private static (bool Success, string? Output) RunLocal(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);

            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, "Failed to start process");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                ? (true, stdout.Trim())
                : (false, stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool Success, string? Output) RunSsh(string remoteHost, string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(remoteHost);
            startInfo.ArgumentList.Add(command);

            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, "Failed to start ssh");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                ? (true, stdout.Trim())
                : (false, stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    internal static string EscapePath(string path)
    {
        // Handle tilde prefix: ~/foo → ~/'foo' so the shell expands ~ but quotes the rest
        if (path.StartsWith("~/"))
        {
            var rest = path[2..];
            return $"~/{EscapeSegment(rest)}";
        }

        return EscapeSegment(path);
    }

    private static string EscapeSegment(string value) =>
        $"'{value.Replace("'", "'\\''")}'";
}
