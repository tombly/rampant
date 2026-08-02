using System.Globalization;
using System.Text.RegularExpressions;

namespace Rampant.Cli;

/// <summary>Pulls every log source under local-workspace/{logs,requests} and prints one
/// chronologically interleaved view. Each event is a one-line summary with a path to the source
/// file for anything truncated - this is a quick "what happened, in what order" scan, not a
/// replacement for reading a specific file directly when full detail is needed. Read-only, runs
/// as a native binary directly on the Pi host (not containerized) against the bind-mounted
/// local-workspace/ directory - see Program.cs for how workspaceRoot is resolved.</summary>
public static class LogCommand
{
    public static int Run(string cwd)
    {
        var workspaceRoot = Path.Combine(cwd, "local-workspace");
        if (!Directory.Exists(workspaceRoot))
        {
            Console.Error.WriteLine($"No local-workspace found under {cwd} - run this from ~/rampant.");
            return 1;
        }

        var events = new List<LogEvent>();

        events.AddRange(ReadHistoryLog(workspaceRoot));
        events.AddRange(ReadHeartbeatLog(workspaceRoot));
        events.AddRange(ReadTimestampedFiles(workspaceRoot, "logs/build-failures", "build-fail",
            (content, _) => SummarizeBuildFailure(content)));
        events.AddRange(ReadTimestampedFiles(workspaceRoot, "logs/unverified-signal-messages", "rejected",
            SummarizeRejected));
        events.AddRange(ReadTimestampedFiles(workspaceRoot, "logs/extend_self", "extend_self",
            (content, _) => SummarizeExtendSelf(content)));
        events.AddRange(ReadRequestOutcomes(workspaceRoot));

        foreach (var line in Render(events.OrderBy(e => e.Timestamp).ToList()))
            Console.WriteLine(line);

        return 0;
    }

    private static IEnumerable<LogEvent> ReadHistoryLog(string workspaceRoot)
    {
        // Under logs/, not memory/ - V2's agent has no memory directory at all, and this was never
        // memory in the first place: it is an operator log the agent has no way to read back.
        var path = Path.Combine(workspaceRoot, "logs", "history.log");
        if (!File.Exists(path))
            yield break;

        // IN/OUT entries can span many lines (LLM replies are often multi-paragraph) - only the
        // line carrying the "[<timestamp>] IN (<kind>):"/"OUT:" marker becomes the one-line summary
        // here; see history.log itself for full content, this is a scan tool, not a viewer.
        var pattern = new Regex(@"^\[(?<ts>[^\]]+)\] (?<dir>IN|OUT)(?: \((?<kind>\w+)\))?: (?<text>.*)$", RegexOptions.Multiline);
        foreach (Match m in pattern.Matches(File.ReadAllText(path)))
        {
            if (!DateTimeOffset.TryParse(m.Groups["ts"].Value, out var ts))
                continue;

            var kind = m.Groups["kind"].Value;
            var category = m.Groups["dir"].Value == "IN" && kind.Length > 0 ? $"IN/{kind}" : m.Groups["dir"].Value;
            yield return new LogEvent(ts, category, Truncate(m.Groups["text"].Value));
        }
    }

    private static IEnumerable<LogEvent> ReadHeartbeatLog(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, "logs", "heartbeat.log");
        if (!File.Exists(path))
            yield break;

        var pattern = new Regex(@"^\[(?<ts>[^\]]+)\] (?<text>.*)$");
        foreach (var line in File.ReadLines(path))
        {
            var m = pattern.Match(line);
            if (!m.Success || !DateTimeOffset.TryParse(m.Groups["ts"].Value, out var ts))
                continue;

            var text = m.Groups["text"].Value;

            // Three buckets, not two. An ordinary start/stop is neither an idle tick nor a problem,
            // and filing it under "heartbeat-issue" trains the reader to skim past a label whose
            // only job is to be worth stopping on - the deploy restarts the agent constantly, so
            // those would have been most of the "issues" on screen.
            var category = text switch
            {
                _ when text.StartsWith("tick,", StringComparison.Ordinal) => "heartbeat",
                _ when text.StartsWith("agent started", StringComparison.Ordinal)
                    || text.StartsWith("agent stopping", StringComparison.Ordinal) => "lifecycle",
                _ => "heartbeat-issue",
            };

            yield return new LogEvent(ts, category, text);
        }
    }

    /// <summary>Every capability request's terminal state, from the supervisor's own record rather
    /// than from what the agent said about it - which is the point, given that the agent has been
    /// wrong about its own self-modifications before.</summary>
    private static IEnumerable<LogEvent> ReadRequestOutcomes(string workspaceRoot)
    {
        var dir = Path.Combine(workspaceRoot, "requests", "out");
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
        {
            if (Path.GetFileName(file) == "status.json")
                continue;

            var content = File.ReadAllText(file);
            if (!DateTimeOffset.TryParse(Field(content, "completedUtc"), out var ts))
                continue;

            var relativePath = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
            var cost = Field(content, "costUsd");
            var costSuffix = cost is null or "0" or "0.0" ? string.Empty : $" ${cost}";
            yield return new LogEvent(
                ts,
                "request",
                $"{Field(content, "status")}{costSuffix} \"{Field(content, "subject")}\" ({relativePath})");
        }
    }

    /// <summary>Deliberately a regex rather than a deserialize: the CLI is read-only tooling for
    /// files another process owns, and it should degrade to a partial line rather than throw if
    /// that shape ever changes underneath it.</summary>
    private static string? Field(string json, string name)
        => Regex.Match(json, $@"""{name}"":\s*""?(?<v>[^"",}}\r\n]*)""?", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Groups["v"].Value.Trim()
            : null;

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
            if (!TryParseFileTimestamp(name, out var ts))
                continue;

            var content = File.ReadAllText(file);
            var relativePath = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
            yield return new LogEvent(ts, category, $"{summarize(content, name)} ({relativePath})");
        }
    }

    /// <summary>Matches on the timestamp *prefix* rather than the whole name, because every writer
    /// appends its own discriminator afterwards and they don't agree on a separator - a request id
    /// (itself hyphenated) for extend_self and build failures, an underscore and a sender for
    /// rejected messages.</summary>
    private static bool TryParseFileTimestamp(string fileName, out DateTimeOffset ts)
    {
        // The two formats used across the codebase: "yyyyMMdd-HHmmss-fffffff" (rejected senders,
        // 23 chars) and "yyyyMMdd-HHmmss" (build failures, extend_self, 15 chars). Longest first,
        // or every 23-char stamp would match the shorter format and lose its sub-second part.
        foreach (var (format, length) in new[] { ("yyyyMMdd-HHmmss-fffffff", 23), ("yyyyMMdd-HHmmss", 15) })
        {
            if (fileName.Length >= length
                && DateTimeOffset.TryParseExact(fileName[..length], format, null, DateTimeStyles.AssumeUniversal, out ts))
            {
                return true;
            }
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

        var costMatch = Regex.Match(content, @"^COST_USD: (?<v>[\d.]+)$", RegexOptions.Multiline);
        var cost = costMatch.Success ? $" ${costMatch.Groups["v"].Value}" : string.Empty;

        // The file opens with the request JSON, then the full prompt. The prompt is mostly a fixed
        // template, so its first line is identical across every entry and useless as a summary -
        // the capability name from the request is what actually distinguishes one run from another.
        var capability = Regex.Match(content, @"""Capability"":\s*""(?<v>[^""]*)""");
        var preview = capability.Success ? Truncate(capability.Groups["v"].Value) : "(capability not found)";

        return $"success={success}{cost} \"{preview}\"";
    }

    private static string SummarizeBuildFailure(string content)
    {
        // The file is always literally "STDOUT:\n<output>\n\nSTDERR:\n<output>" (see
        // Deployer.LogBuildFailureAsync) - "first line" would always just be the "STDOUT:" label.
        // Prefer an actual compiler/MSBuild error line if one is present.
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
