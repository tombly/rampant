using System.Globalization;
using System.Text.RegularExpressions;

namespace Rampant.Cli;

/// <summary>Pulls every log source under local-workspace/{memory,logs} and prints one
/// chronologically interleaved view. Each event is a one-line summary with a path to the source
/// file for anything truncated - this is a quick "what happened, in what order" scan, not a
/// replacement for reading a specific file directly when full detail is needed. Read-only, runs
/// as a native binary directly on the Pi host (not containerized) against the bind-mounted
/// local-workspace/ directory - see Program.cs for how workspaceRoot is resolved.</summary>
public static class LogCommand
{
    public static int Run(string workspaceRoot)
    {
        var events = new List<LogEvent>();

        events.AddRange(ReadHistoryLog(workspaceRoot));
        events.AddRange(ReadHeartbeatLog(workspaceRoot));
        events.AddRange(ReadTimestampedFiles(workspaceRoot, "logs/build-failures", "build-fail",
            (content, _) => SummarizeBuildFailure(content)));
        events.AddRange(ReadTimestampedFiles(workspaceRoot, "logs/unverified-signal-messages", "rejected",
            SummarizeRejected));
        events.AddRange(ReadTimestampedFiles(workspaceRoot, "logs/extend_self", "extend_self",
            (content, _) => SummarizeExtendSelf(content)));

        foreach (var line in Render(events.OrderBy(e => e.Timestamp).ToList()))
            Console.WriteLine(line);

        return 0;
    }

    private static IEnumerable<LogEvent> ReadHistoryLog(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, "memory", "history.log");
        if (!File.Exists(path))
            yield break;

        // IN/OUT entries can span many lines (LLM replies are often multi-paragraph) - only the
        // line carrying the "[<timestamp>] IN:"/"OUT:" marker becomes the one-line summary here;
        // see history.log itself for full content, this is a scan tool, not a viewer.
        var pattern = new Regex(@"^\[(?<ts>[^\]]+)\] (?<dir>IN|OUT): (?<text>.*)$", RegexOptions.Multiline);
        foreach (Match m in pattern.Matches(File.ReadAllText(path)))
        {
            if (DateTimeOffset.TryParse(m.Groups["ts"].Value, out var ts))
                yield return new LogEvent(ts, m.Groups["dir"].Value, Truncate(m.Groups["text"].Value));
        }
    }

    private static IEnumerable<LogEvent> ReadHeartbeatLog(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, "logs", "heartbeat.log");
        if (!File.Exists(path))
            yield break;

        var pattern = new Regex(@"^(?<ts>\S+) (?<text>.*)$");
        foreach (var line in File.ReadLines(path))
        {
            var m = pattern.Match(line);
            if (!m.Success || !DateTimeOffset.TryParse(m.Groups["ts"].Value, out var ts))
                continue;

            var text = m.Groups["text"].Value;
            var category = text.StartsWith("tick, no new messages", StringComparison.Ordinal)
                ? "heartbeat"
                : "heartbeat-issue";
            yield return new LogEvent(ts, category, text);
        }
    }

    /// <summary>Shared reader for the three log sources that write one file per event, named
    /// starting with a yyyyMMdd-HHmmss[-fffffff] timestamp: build-failures, unverified Signal
    /// senders, and extend_self invocations. <paramref name="summarize"/> gets the file's content
    /// and bare file name (no directory/extension) to build a one-line summary.</summary>
    private static IEnumerable<LogEvent> ReadTimestampedFiles(
        string workspaceRoot, string relativeDir, string category, Func<string, string, string> summarize)
    {
        var dir = Path.Combine(workspaceRoot, relativeDir);
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.GetFiles(dir).OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!TryParseFileTimestamp(name.Split('_')[0], out var ts))
                continue;

            var content = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
            yield return new LogEvent(ts, category, $"{summarize(content, name)} ({relativePath})");
        }
    }

    private static bool TryParseFileTimestamp(string value, out DateTimeOffset ts)
    {
        // The two formats actually used across the codebase: "yyyyMMdd-HHmmss" (build failures,
        // extend_self) and "yyyyMMdd-HHmmss-fffffff" (unverified-sender filenames).
        foreach (var format in new[] { "yyyyMMdd-HHmmss-fffffff", "yyyyMMdd-HHmmss" })
        {
            if (DateTimeOffset.TryParseExact(value, format, null, DateTimeStyles.AssumeUniversal, out ts))
                return true;
        }
        ts = default;
        return false;
    }

    private static string SummarizeRejected(string content, string fileName)
    {
        // Filename is "<timestamp>_<senderIdentifier>"; the timestamp itself has no underscores
        // (hyphens only), so everything after the first underscore is the sender.
        var underscoreIndex = fileName.IndexOf('_');
        var sender = underscoreIndex < 0 ? "?" : fileName[(underscoreIndex + 1)..];
        return $"{FirstLine(content)}, sender={sender}";
    }

    private static string SummarizeExtendSelf(string content)
    {
        var successMatch = Regex.Match(content, @"^SUCCESS: (?<v>\w+)$", RegexOptions.Multiline);
        var success = successMatch.Success ? successMatch.Groups["v"].Value : "?";

        // The real prompt is SELF.md's full text followed by a fixed wrapper template - "the
        // first line of the prompt" is always just SELF.md's own header, identical across every
        // entry and useless as a summary. Anchor on "Here's the task:" instead (see
        // RampantBrain.ExtendSelfAsync), which precedes the actual task text in that wrapper.
        var taskMatch = Regex.Match(content, @"Here's the task:\r?\n\r?\n(?<line>.*)");
        var preview = taskMatch.Success ? Truncate(taskMatch.Groups["line"].Value) : "(task not found in prompt)";

        return $"success={success} \"{preview}\"";
    }

    private static string SummarizeBuildFailure(string content)
    {
        // The file is always literally "STDOUT:\n<output>\n\nSTDERR:\n<output>" (see
        // ProcessSupervisor.LogBuildFailureAsync) - "first line" would always just be the
        // "STDOUT:" label. Prefer an actual compiler/MSBuild error line if one is present.
        var errorLine = content.Split('\n').FirstOrDefault(l => l.Contains("error ", StringComparison.OrdinalIgnoreCase));
        return Truncate((errorLine ?? content.Split('\n', 2)[0]).Trim());
    }

    private static string FirstLine(string content)
        => Truncate(content.Split('\n', 2)[0].TrimEnd('\r'));

    private static string Truncate(string text, int maxLength = 90)
        => text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>Consecutive plain "heartbeat" events (idle ticks) collapse into one summary line
    /// so hundreds of routine ticks don't bury what actually matters; "heartbeat-issue" entries
    /// (cycle errors, signal connection errors, startup warnings) are never collapsed.</summary>
    private static IEnumerable<string> Render(List<LogEvent> sorted)
    {
        var i = 0;
        while (i < sorted.Count)
        {
            if (sorted[i].Category != "heartbeat")
            {
                yield return FormatLine(sorted[i]);
                i++;
                continue;
            }

            var start = i;
            while (i < sorted.Count && sorted[i].Category == "heartbeat")
                i++;

            yield return i - start == 1
                ? FormatLine(sorted[start])
                : $"{sorted[start].Timestamp:yyyy-MM-dd HH:mm:ss}  heartbeat   {i - start} heartbeats collapsed, idle (through {sorted[i - 1].Timestamp:HH:mm:ss})";
        }
    }

    private static string FormatLine(LogEvent e)
        => $"{e.Timestamp:yyyy-MM-dd HH:mm:ss}  {e.Category,-11} {e.Summary}";
}
