using System.Diagnostics;

namespace CodeCommandCenter.Services;

public static class SshControlMasterService
{
    private static readonly string _socketDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccc", "ssh");

    // Tracks last failed connection attempt per host to throttle retries (30s cooldown)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastFailure = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private static readonly TimeSpan _retryCooldown = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Ensures a ControlMaster socket exists for the host.
    /// Returns true if the socket is alive (or was successfully started).
    /// Uses a 30s cooldown after failure to avoid hammering unreachable hosts.
    /// </summary>
    public static bool EnsureConnected(string host)
    {
        // Throttle: don't retry a failed host within cooldown period
        if (_lastFailure.TryGetValue(host, out var lastFail)
            && DateTime.UtcNow - lastFail < _retryCooldown)
            return false;

        if (IsAlive(host))
        {
            _lastFailure.TryRemove(host, out _);
            return true;
        }

        // If this host has failed before, reconnect in background to avoid
        // blocking the main loop. The next poll will pick it up once connected.
        if (_lastFailure.ContainsKey(host))
        {
            _ = Task.Run(() => StartControlMaster(host));
            return false;
        }

        // First connection attempt (startup) — block so sessions are available immediately
        return StartControlMaster(host);
    }

    /// <summary>
    /// Runs a tmux command on the remote host via the ControlMaster socket.
    /// Returns (success, stdout, null) on success, or (false, null, stderr) on failure.
    /// Returns (false, null, null) if the host is offline.
    /// </summary>
    public static (bool Success, string? Output, string? Error) RunTmuxCommand(string host, params string[] tmuxArgs)
    {
        if (!EnsureConnected(host))
            return (false, null, null);

        var socketPath = SocketPath(host);
        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add(socketPath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ConnectTimeout=5");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ServerAliveInterval=3");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ServerAliveCountMax=1");
        startInfo.ArgumentList.Add(host);
        // Build the remote command as a single string so the remote shell
        // doesn't re-split arguments (e.g. format strings with spaces/tabs)
        var quotedArgs = string.Join(" ", tmuxArgs.Select(ShellQuote));
        startInfo.ArgumentList.Add($"tmux {quotedArgs}");

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, null, null);

            // Use async reads to avoid deadlock, with a timeout to prevent
            // hanging when the network drops (iptables, cable pull, etc.)
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(3000))
            {
                // Timed out — kill the SSH command and the stale ControlMaster,
                // so next EnsureConnected creates a fresh one with keepalive
                try { process.Kill(); } catch { }
                _ = Task.Run(() => Disconnect(host));
                _lastFailure[host] = DateTime.UtcNow;
                return (false, null, "SSH command timed out");
            }

            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;

            if (process.ExitCode == 0)
            {
                // Clear failure state so future EnsureConnected calls don't go async
                _lastFailure.TryRemove(host, out _);
                return (true, stdout.TrimEnd(), null);
            }
            return (false, null, stderr.Trim());
        }
        catch
        {
            return (false, null, null);
        }
    }

    /// <summary>
    /// Disconnects the ControlMaster for this host (sends -O exit).
    /// Safe to call even if the socket doesn't exist.
    /// </summary>
    public static void Disconnect(string host)
    {
        try
        {
            var socketPath = SocketPath(host);
            if (!File.Exists(socketPath))
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-O");
            startInfo.ArgumentList.Add("exit");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            process?.WaitForExit(3000);
        }
        catch
        {
            // Best-effort
        }
    }

    private static bool IsAlive(string host)
    {
        try
        {
            var socketPath = SocketPath(host);
            if (!File.Exists(socketPath))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-O");
            startInfo.ArgumentList.Add("check");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            if (process == null) return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartControlMaster(string host)
    {
        var sem = _locks.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        if (!sem.Wait(0)) // If another caller is already starting, don't block — just report not connected yet
            return false;

        try
        {
            // Re-check after acquiring — another caller may have connected while we waited
            if (IsAlive(host))
                return true;

            Directory.CreateDirectory(_socketDir);
            // Restrict socket dir to owner only
            var chmodInfo = new ProcessStartInfo { FileName = "chmod", UseShellExecute = false };
            chmodInfo.ArgumentList.Add("700");
            chmodInfo.ArgumentList.Add(_socketDir);
            Process.Start(chmodInfo)?.WaitForExit(1000);

            var socketPath = SocketPath(host);

            // Remove stale socket file — ssh -M refuses to start if the path exists
            // but the master process behind it is dead (VPN drop, network change, etc.)
            if (File.Exists(socketPath))
            {
                try { File.Delete(socketPath); }
                catch { /* best-effort */ }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // -M = master mode, -N = no command, -f = daemonize after auth
            startInfo.ArgumentList.Add("-M");
            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("BatchMode=yes");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ConnectTimeout=3");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ControlPersist=no");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ServerAliveInterval=3");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ServerAliveCountMax=2");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _lastFailure[host] = DateTime.UtcNow;
                return false;
            }

            process.WaitForExit(5000);
            var connected = process.ExitCode == 0;

            if (!connected)
                _lastFailure[host] = DateTime.UtcNow;
            else
                _lastFailure.TryRemove(host, out _);

            return connected;
        }
        catch
        {
            _lastFailure[host] = DateTime.UtcNow;
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    private static string SocketPath(string host)
    {
        // Replace characters that are invalid in socket filenames
        var safeName = string.Concat(host.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_'));
        return Path.Combine(_socketDir, $"{safeName}.sock");
    }

    internal static string ShellQuote(string arg)
    {
        // If the arg is safe, return as-is
        if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '/' || c == '~' || c == '=' || c == ':'))
            return arg;
        // Wrap in single quotes, escaping any existing single quotes
        return $"'{arg.Replace("'", "'\\''")}'";
    }
}
