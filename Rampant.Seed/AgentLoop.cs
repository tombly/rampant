using System.Diagnostics;

namespace Rampant.Agent;

/// <summary>The whole cycle: read inbox -> assemble prompt -> invoke Claude Code -> persist ->
/// commit -> reply -> repeat. Strictly sequential (a plain loop, never overlapping cycles) to
/// avoid concurrent-write git conflicts. Kept intentionally minimal - this is the first thing an
/// ungated rewrite process will operate on.
///
/// Signal is the only interaction channel (see PLAN.md -> "Opening holes" -> Signal; the earlier
/// Azure Storage Queue mailbox was dropped once Signal replaced it end-to-end). Message arrival
/// is event-driven, not polled: <see cref="SignalClient.RunAsync"/> listens on a persistent
/// connection to the signal-cli sidecar and wakes the cycle loop immediately when a message
/// arrives, rather than waiting for the next heartbeat tick.</summary>
public sealed class AgentLoop
{
    private const string AgentRoot = "/workspace/agent";
    private const string InboxDir = "/workspace/inbox";
    private const string InboxProcessedDir = "/workspace/inbox/.processed";
    private const string OutboxDir = "/workspace/outbox";
    private const string MemoryDir = "/workspace/memory";
    private const string LogsDir = "/workspace/logs";
    private const string SelfMdPath = "/workspace/agent/SELF.md";
    private const string HeartbeatLogPath = "/workspace/logs/heartbeat.log";

    private const string SignalFilePrefix = "signal-";
    private const string UnverifiedSignalLogDir = "/workspace/logs/unverified-signal-messages";

    // Who's allowed to be treated as "the owner" over Signal. The signal-cli sidecar is a single
    // linked account, but that account still receives DMs from any contact and messages in any
    // group it's a member of - without this check, anyone who texts the linked number gets full
    // owner trust (extend_self, remember, etc.), not just the one person who's supposed to. This
    // is baseline access control for the sole interaction channel into a self-modifying,
    // bypassPermissions system - deliberately baked in here rather than left for extend_self to
    // rebuild from scratch on every fresh genesis (it did, twice in testing, with a materially
    // different - and once incomplete, missing group-message rejection entirely - result each
    // time; not something worth leaving to chance for the one thing gating who can talk to the
    // agent at all).
    //
    // Comma-separated list of accepted sender identifiers (phone number and/or UUID - see
    // SignalClient.HandleLineAsync for why the same linked account can present as either), read
    // once at startup. Unset/empty means fail closed: nobody is trusted, not "everybody is".
    private const string OwnerIdEnvVar = "RAMPANT_OWNER_SIGNAL_ID";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly RampantBrain _brain = new(new ClaudeCodeRunner());
    private readonly SignalClient _signal = new(host: "signal-cli", port: 7583);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly HashSet<string> _allowedOwnerIds = LoadAllowedOwnerIds();

    public async Task RunAsync(CancellationToken shutdownToken)
    {
        EnsureDirectories();

        if (_allowedOwnerIds.Count == 0)
        {
            await AppendLineAsync(
                HeartbeatLogPath,
                $"{DateTimeOffset.UtcNow:O} WARNING: {OwnerIdEnvVar} is not set - every inbound Signal message will be ignored (failing closed) until it's configured. See SELF.md.",
                CancellationToken.None);
        }

        _ = Task.Run(() => _signal.RunAsync(
            OnSignalMessageReceivedAsync,
            shutdownToken,
            onError: ex => AppendLineAsync(HeartbeatLogPath, $"{DateTimeOffset.UtcNow:O} signal connection error: {ex.Message}", CancellationToken.None)), shutdownToken);

        while (!shutdownToken.IsCancellationRequested)
        {
            try
            {
                // Always run a cycle to completion with CancellationToken.None, even if
                // shutdown is requested mid-cycle - a graceful stop should let an in-flight
                // message (e.g. an extend_self self-modification) finish, reply, and mark
                // itself processed rather than being aborted and silently reprocessed from
                // scratch after restart. Only the decision to start ANOTHER cycle checks
                // shutdownToken, below.
                await RunOneCycleAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                await AppendLineAsync(HeartbeatLogPath, $"{DateTimeOffset.UtcNow:O} cycle error: {ex.Message}", CancellationToken.None);
            }

            if (shutdownToken.IsCancellationRequested)
                break;

            await WaitForNextCycleAsync(shutdownToken);
        }
    }

    /// <summary>Wakes up on whichever comes first: a new Signal message, or the heartbeat
    /// interval - so an incoming message is picked up immediately instead of waiting up to one
    /// full interval, while an idle agent still ticks its heartbeat log periodically.</summary>
    private async Task WaitForNextCycleAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(HeartbeatInterval, cts.Token);
        var wakeTask = _wake.WaitAsync(cts.Token);

        await Task.WhenAny(delayTask, wakeTask);
        await cts.CancelAsync();
    }

    /// <summary>Called from the Signal listener's background task for every inbound message,
    /// trusted or not. Only a direct (non-group) message from an allowlisted sender identifier is
    /// materialized as a local inbox file and treated as owner input - everything else is
    /// rejected here and never reaches /workspace/inbox, since anything that lands there gets full
    /// owner trust (extend_self, remember, etc.) in the cycle loop. See
    /// <see cref="LoadAllowedOwnerIds"/> for how the allowlist is configured. The sender's
    /// identifier (phone number or, often, just their UUID - see SignalClient.HandleLineAsync) is
    /// encoded in the inbox filename so the reply goes back to the right place; see
    /// <see cref="TryGetSignalSender"/>. Both forms are filename-safe as-is (digits/+ for a
    /// number, hyphenated digits for a UUID), so no sanitizing is needed.</summary>
    private async Task OnSignalMessageReceivedAsync(string senderIdentifier, string text, bool isGroupMessage)
    {
        var timestamp = DateTimeOffset.UtcNow;

        if (isGroupMessage)
        {
            await LogUnverifiedSenderAsync(senderIdentifier, text, timestamp, "group message");
            return;
        }

        if (!_allowedOwnerIds.Contains(senderIdentifier))
        {
            await LogUnverifiedSenderAsync(senderIdentifier, text, timestamp, $"sender not in {OwnerIdEnvVar}");
            return;
        }

        var path = Path.Combine(InboxDir, $"{SignalFilePrefix}{senderIdentifier}_{timestamp:yyyyMMdd-HHmmss-fffffff}.txt");

        await File.WriteAllTextAsync(path, text);
        _wake.Release();
    }

    /// <summary>Reads <see cref="OwnerIdEnvVar"/>: a comma-separated list of Signal sender
    /// identifiers (phone numbers and/or UUIDs) that are trusted as "the owner". Multiple entries
    /// are for the *same* person's account presenting differently across messages (see
    /// SignalClient.HandleLineAsync - sourceNumber is often null due to Signal's phone-number
    /// privacy feature, falling back to sourceUuid), not for trusting multiple people. Comparison
    /// is case-insensitive since UUIDs can vary in case. Empty/unset means fail closed - no sender
    /// is trusted - rather than trusting everyone.</summary>
    private static HashSet<string> LoadAllowedOwnerIds()
    {
        var raw = Environment.GetEnvironmentVariable(OwnerIdEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Rejected messages (untrusted sender or group traffic) are never silently dropped
    /// without a trace - they're recorded here instead, one file per message, so a real
    /// misconfiguration (wrong/unset RAMPANT_OWNER_SIGNAL_ID) is discoverable rather than
    /// presenting as "the bot just doesn't reply to anyone."</summary>
    private static async Task LogUnverifiedSenderAsync(string senderIdentifier, string text, DateTimeOffset timestamp, string reason)
    {
        Directory.CreateDirectory(UnverifiedSignalLogDir);
        var path = Path.Combine(UnverifiedSignalLogDir, $"{timestamp:yyyyMMdd-HHmmss-fffffff}_{senderIdentifier}.txt");
        await File.WriteAllTextAsync(path, $"reason: {reason}{Environment.NewLine}{text}");
    }

    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        var newMessages = Directory.GetFiles(InboxDir).OrderBy(f => f).ToList();

        if (newMessages.Count == 0)
        {
            // Heartbeats go to the log, not a reply - the owner only hears back when they've
            // actually said something.
            await AppendLineAsync(HeartbeatLogPath, $"{DateTimeOffset.UtcNow:O} tick, no new messages", ct);
            return;
        }

        foreach (var messagePath in newMessages)
            await HandleMessageAsync(messagePath, ct);
    }

    private async Task HandleMessageAsync(string messagePath, CancellationToken ct)
    {
        var messageText = (await File.ReadAllTextAsync(messagePath, ct)).Trim();
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        string reply;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            reply = $"""
                Got your message but ANTHROPIC_API_KEY isn't set yet, so I can't actually respond
                or extend myself yet: "{messageText}"
                """;
        }
        else
        {
            var selfMd = File.Exists(SelfMdPath) ? await File.ReadAllTextAsync(SelfMdPath, ct) : string.Empty;
            var result = await _brain.HandleAsync(selfMd, messageText, ct);
            reply = result.Success
                ? result.Reply
                : $"Ran into a problem responding: {result.Reply}";
        }

        var timestamp = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(OutboxDir, $"{timestamp:yyyyMMdd-HHmmss}.txt"), reply, ct);
        await AppendLineAsync(
            Path.Combine(MemoryDir, "history.log"),
            $"[{timestamp:O}] IN: {messageText}{Environment.NewLine}[{timestamp:O}] OUT: {reply}",
            ct);

        var senderIdentifier = TryGetSignalSender(messagePath);
        MarkProcessed(messagePath);
        await CommitAsync($"Cycle {timestamp:O}: handled inbox message", ct);

        if (senderIdentifier is not null)
            await PushReplyToSignalAsync(senderIdentifier, reply, ct);
        // else: a manually-dropped inbox file with no known channel to reply on (e.g. local
        // testing per README's quick start) - the outbox file and history.log above already
        // capture the reply, nothing more to do.
    }

    /// <summary>Recovers the sender's identifier (phone number or UUID - signal-cli accepts
    /// either as a "recipient", confirmed live) from a signal-*.txt inbox filename, or null for
    /// anything not written by <see cref="OnSignalMessageReceivedAsync"/>.</summary>
    private static string? TryGetSignalSender(string messagePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(messagePath);
        if (!fileName.StartsWith(SignalFilePrefix, StringComparison.Ordinal))
            return null;

        var rest = fileName[SignalFilePrefix.Length..];
        var underscoreIndex = rest.IndexOf('_');
        return underscoreIndex < 0 ? rest : rest[..underscoreIndex];
    }

    private async Task PushReplyToSignalAsync(string recipient, string reply, CancellationToken ct)
    {
        try
        {
            await _signal.SendAsync(recipient, reply, ct);
        }
        catch (Exception ex)
        {
            await AppendLineAsync(HeartbeatLogPath, $"{DateTimeOffset.UtcNow:O} signal send failed: {ex.Message}", ct);
        }
    }

    private static void MarkProcessed(string messagePath)
    {
        Directory.CreateDirectory(InboxProcessedDir);
        var dest = Path.Combine(InboxProcessedDir, Path.GetFileName(messagePath));
        File.Move(messagePath, dest, overwrite: true);
    }

    private static void EnsureDirectories()
    {
        foreach (var dir in new[] { InboxDir, InboxProcessedDir, OutboxDir, MemoryDir, LogsDir, UnverifiedSignalLogDir })
            Directory.CreateDirectory(dir);
    }

    private static async Task AppendLineAsync(string path, string line, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, line + Environment.NewLine, ct);
    }

    private async Task CommitAsync(string message, CancellationToken ct)
    {
        await GitAsync(ct, "add", "-A");
        // Commit message passed via stdin (-F -), never as a literal argument or interpolated
        // into a shell string, since the text can originate from an LLM.
        await GitWithStdinAsync(message, ct, "commit", "-F", "-");
    }

    private static Task GitAsync(CancellationToken ct, params string[] args)
        => RunGitAsync(stdin: null, ct, args);

    private static Task GitWithStdinAsync(string stdin, CancellationToken ct, params string[] args)
        => RunGitAsync(stdin, ct, args);

    private static async Task RunGitAsync(string? stdin, CancellationToken ct, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = AgentRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {string.Join(' ', args)}");

        // Read both streams concurrently before writing stdin/awaiting exit - reading only one
        // (or reading after exit) risks a pipe-buffer deadlock if git writes enough to the
        // un-drained stream. This is also what actually matters for "nothing to commit": git
        // writes that message to STDOUT, not stderr - checking stderr alone (the previous bug)
        // meant every legitimate no-op commit was misread as a real failure.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var combined = stdout + stderr;
            if (!combined.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}{stdout}");
        }
    }
}
