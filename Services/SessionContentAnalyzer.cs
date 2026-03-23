using System.Text.RegularExpressions;

namespace CodeCommandCenter.Services;

/// <summary>
/// Shared content analysis logic used by TmuxBackend
/// for detecting idle prompts and status bar boundaries.
/// </summary>
internal static partial class SessionContentAnalyzer
{
    /// <summary>
    /// Number of consecutive stable polls before marking a session as "waiting for input".
    /// 4 polls × 500ms = 2 seconds — avoids false positives from short pauses between tool calls.
    /// </summary>
    public const int StableThreshold = 4;

    private static readonly Regex AnsiEscapePattern = AnsiEscapeRegex();

    /// <summary>
    /// Strips all ANSI escape sequences from a string.
    /// Required because VtScreenBuffer.GetContent() embeds SGR codes that
    /// interfere with content analysis (status bar detection, idle prompt detection).
    /// </summary>
    public static string StripAnsi(string text) =>
        AnsiEscapePattern.Replace(text, "");
    /// <summary>
    /// Detects the Claude Code idle prompt: a ❯ line between two ─ separator lines.
    /// Returns false if Claude's last message ends with '?' (asking a question).
    /// </summary>
    public static bool IsIdlePrompt(string content)
    {
        var lines = StripAnsi(content).Split('\n');

        int bottomSep = -1, prompt = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            if (bottomSep < 0)
                bottomSep = i;
            else
            {
                prompt = i;
                break;
            }
        }

        if (prompt < 0)
            return false;

        var rule = lines[bottomSep].Trim();
        if (rule.Length < 3 || rule.Any(c => c != '─'))
            return false;

        if (!lines[prompt].TrimStart().StartsWith('❯'))
            return false;

        for (var i = prompt - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var trimmed = lines[i].Trim();
            if (trimmed.Length >= 3 && trimmed.All(c => c == '─'))
            {
                for (var j = i - 1; j >= 0; j--)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]))
                        continue;
                    var line = lines[j].TrimStart();
                    if (line.StartsWith('⎿') || line.StartsWith('…') || line.StartsWith('❯')
                        || line.StartsWith('●') || line.StartsWith('✻'))
                        continue;
                    return !line.TrimEnd().EndsWith('?');
                }

                return true;
            }

            return true;
        }

        return true;
    }

    private static readonly Regex StatusBarTimerPattern = StatusBarTimerRegex();

    /// <summary>
    /// Strips the Claude Code status bar (identified by a timer pattern like "1h23m")
    /// from the bottom of pane output, returning only the content above it.
    /// </summary>
    public static string GetContentAboveStatusBar(string paneOutput)
    {
        var lines = StripAnsi(paneOutput).Split('\n');

        var statusBarIndex = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            if (StatusBarTimerPattern.IsMatch(lines[i]))
            {
                statusBarIndex = i;
                break;
            }
        }

        if (statusBarIndex >= 0)
            return string.Join('\n', lines.AsSpan(0, statusBarIndex));

        var lastNonEmpty = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            lastNonEmpty = i;
            break;
        }

        var end = lastNonEmpty >= 0 ? lastNonEmpty : lines.Length;
        return string.Join('\n', lines.AsSpan(0, end));
    }

    /// <summary>
    /// Checks if the 'claude' CLI is available on the system PATH.
    /// </summary>
    public static bool CheckClaudeAvailable()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"\d+[hms]\d*[ms]?\s*$", RegexOptions.Compiled)]
    private static partial Regex StatusBarTimerRegex();

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]|\x1b\][^\a]*(?:\a|\x1b\\)|\x1b[()][A-Z0-9]", RegexOptions.Compiled)]
    private static partial Regex AnsiEscapeRegex();
}
