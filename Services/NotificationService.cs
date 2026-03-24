using System.Diagnostics;
using CodeCommandCenter.Models;

namespace CodeCommandCenter.Services;

public static class NotificationService
{
    private static readonly Dictionary<string, DateTime> _cooldowns = new();

    /// <summary>
    /// Sends notifications for sessions that just transitioned to waiting-for-input.
    /// Returns the notification message if any were sent, null otherwise.
    /// </summary>
    public static string? NotifyWaiting(List<Session> transitioned, NotificationConfig config)
    {
        if (!config.Enabled || transitioned.Count == 0)
            return null;

        var now = DateTime.UtcNow;
        var eligible = new List<Session>();

        foreach (var session in transitioned)
        {
            if (_cooldowns.TryGetValue(session.Name, out var lastNotified)
                && (now - lastNotified).TotalSeconds < config.CooldownSeconds)
                continue;

            eligible.Add(session);
            _cooldowns[session.Name] = now;
        }

        if (eligible.Count == 0)
            return null;

        var message = eligible.Count == 1
            ? FormatSession(eligible[0])
            : $"{eligible.Count} sessions waiting: {string.Join(", ", eligible.Select(FormatSession))}";

        if (config.Bell)
        {
            Console.Write('\a');
            Console.Out.Flush();
        }

        if (config.OscNotify)
            SendOscNotification("CCC", message);

        if (config.DesktopNotify)
            SendDesktopNotification(message);

        SendTmuxDisplayMessage($"⏳ {message}");

        return message;
    }


    public static void Cleanup(IEnumerable<string> liveSessionNames)
    {
        var live = new HashSet<string>(liveSessionNames);
        var stale = _cooldowns.Keys.Where(k => !live.Contains(k)).ToList();
        foreach (var key in stale)
            _cooldowns.Remove(key);
    }

    private static string FormatSession(Session session) =>
        string.IsNullOrEmpty(session.Description) ? session.Name : $"{session.Name} ({session.Description})";

    private static void SendOscNotification(string title, string body)
    {
        // OSC 777 (iTerm2/rxvt-unicode style)
        Console.Write($"\e]777;notify;{title};{body}\e" + $"\\");
        // OSC 9 (Windows Terminal / ConEmu style)
        Console.Write($"\e]9;{body}\e" + $"\\");
        Console.Out.Flush();
    }

    private static void SendTmuxDisplayMessage(string message)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "tmux",
                ArgumentList =
                {
                    "display-message",
                    "-d",
                    "3000",
                    message
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(1000);
        }
        catch
        {
            // tmux not available or not in a tmux context — silently ignore
        }
    }

    private static void SendDesktopNotification(string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList =
                {
                    "CCC",
                    message
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // notify-send not available — silently ignore
        }
    }
}
