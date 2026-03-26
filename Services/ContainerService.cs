using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodeCommandCenter.Services;

public static class ContainerService
{
    private static readonly ConcurrentDictionary<string, (bool Result, DateTime Expiry)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static bool IsRunning(string containerName, string? remoteHost = null)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return false;

        var cacheKey = $"{remoteHost ?? "local"}:{containerName}";
        var now = DateTime.UtcNow;

        if (_cache.TryGetValue(cacheKey, out var cached) && now < cached.Expiry)
            return cached.Result;

        var result = remoteHost != null
            ? CheckRemoteContainer(remoteHost, containerName)
            : CheckLocalContainer(containerName);

        _cache[cacheKey] = (result, now + CacheTtl);
        return result;
    }

    private static bool CheckLocalContainer(string containerName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("inspect");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("{{.State.Running}}");
            startInfo.ArgumentList.Add(containerName);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            return process.ExitCode == 0 && output == "true";
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckRemoteContainer(string remoteHost, string containerName)
    {
        var command = $"docker inspect --format '{{{{.State.Running}}}}' {containerName}";
        var (success, output) = SshService.Run(remoteHost, command);
        return success && output?.Trim() == "true";
    }
}
