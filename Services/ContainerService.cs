using System.Diagnostics;

namespace CodeCommandCenter.Services;

public static class ContainerService
{
    private static string? _cachedContainerName;
    private static bool _cachedResult;
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static bool IsRunning(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return false;

        var now = DateTime.UtcNow;
        if (containerName == _cachedContainerName && now < _cacheExpiry)
            return _cachedResult;

        _cachedContainerName = containerName;
        _cachedResult = CheckContainer(containerName);
        _cacheExpiry = now + CacheTtl;
        return _cachedResult;
    }

    private static bool CheckContainer(string containerName)
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
}
