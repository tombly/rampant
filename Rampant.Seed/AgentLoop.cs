using Microsoft.Extensions.AI;

namespace Rampant.Agent;

/// <summary>
/// The whole cycle: read an envelope from the inbox, think, write a reply to the outbox, mark it
/// handled, repeat. Strictly sequential - one envelope at a time, never overlapping turns.
///
/// Note what is absent. There is no Signal connection, no git, no build, no credential beyond the
/// one needed to talk to a model. This process reads files and writes files; the supervisor does
/// everything else. That is what makes the promise "it can always answer when you call" structural
/// rather than aspirational: nothing this agent can change about itself reaches the channel.
/// </summary>
public sealed class AgentLoop
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;

    public async Task RunAsync(CancellationToken shutdownToken)
    {
        EnsureDirectories();

        IChatClient chat;
        try
        {
            chat = ChatClientFactory.Create();
        }
        catch (Exception ex)
        {
            // Without a model there is nothing to be. Say so where an operator will see it, and
            // exit rather than spinning - the supervisor will report the process as down, over a
            // channel that does not depend on this process working.
            await Log.AppendAsync(AgentPaths.HeartbeatLog, $"FATAL: {ex.Message}", CancellationToken.None);
            return;
        }

        var brain = new AgentBrain(chat);
        await Log.AppendAsync(AgentPaths.HeartbeatLog, "agent started", CancellationToken.None);

        while (!shutdownToken.IsCancellationRequested)
        {
            try
            {
                // Always run a turn to completion on CancellationToken.None, even if shutdown was
                // requested mid-turn. A deployment stops this process, and a turn aborted halfway
                // leaves its envelope unprocessed - so the successor silently redoes it from
                // scratch, with no idea it is the second attempt. That exact failure is why this
                // rule exists; only the decision to start ANOTHER turn checks the token, below.
                await RunOneCycleAsync(brain, CancellationToken.None);
            }
            catch (Exception ex)
            {
                await Log.AppendAsync(AgentPaths.HeartbeatLog, $"cycle error: {ex.Message}", CancellationToken.None);
            }

            if (shutdownToken.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(PollInterval, shutdownToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await Log.AppendAsync(AgentPaths.HeartbeatLog, "agent stopping", CancellationToken.None);
    }

    private async Task RunOneCycleAsync(AgentBrain brain, CancellationToken ct)
    {
        var pending = Directory.GetFiles(AgentPaths.Inbox)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (pending.Count == 0)
        {
            if (DateTimeOffset.UtcNow - _lastHeartbeat >= HeartbeatInterval)
            {
                _lastHeartbeat = DateTimeOffset.UtcNow;
                await Log.AppendAsync(AgentPaths.HeartbeatLog, "tick, nothing in the inbox", ct);
            }

            return;
        }

        foreach (var path in pending)
            await HandleAsync(brain, path, ct);
    }

    private static async Task HandleAsync(AgentBrain brain, string path, CancellationToken ct)
    {
        var envelope = await InboxEnvelope.ReadAsync(path, ct);

        string reply;
        try
        {
            reply = await brain.HandleAsync(envelope, ct);
        }
        catch (Exception ex)
        {
            // A model or tool failure is reported rather than swallowed. Silence and failure look
            // identical from the owner's side, and a wake tick is the one case where silence is
            // legitimate - so an error there must not be mistaken for one.
            await Log.AppendAsync(AgentPaths.HeartbeatLog, $"turn failed ({envelope.Kind}): {ex.Message}", ct);
            reply = envelope.Kind == InboxKind.Wake
                ? AgentBrain.SilenceMarker
                : $"Something went wrong while I was working on that: {ex.Message}";
        }

        var silent = reply.Equals(AgentBrain.SilenceMarker, StringComparison.OrdinalIgnoreCase);

        if (!silent)
            await Outbox.WriteAsync(reply, envelope.Sender, ct);

        await Log.AppendAsync(
            AgentPaths.HistoryLog,
            $"IN ({envelope.Kind}): {Collapse(envelope.Text)}{Environment.NewLine}[{DateTimeOffset.UtcNow:O}] OUT: {(silent ? "(stayed quiet)" : Collapse(reply))}",
            ct);

        MarkProcessed(path);
    }

    private static void MarkProcessed(string path)
    {
        Directory.CreateDirectory(AgentPaths.InboxProcessed);
        File.Move(path, Path.Combine(AgentPaths.InboxProcessed, Path.GetFileName(path)), overwrite: true);
    }

    private static void EnsureDirectories()
    {
        // The container entrypoint creates and owns these; this is belt and braces for running the
        // agent outside a container, and it is deliberately limited to the writable ones.
        foreach (var dir in new[] { AgentPaths.Inbox, AgentPaths.InboxProcessed, AgentPaths.Outbox, AgentPaths.Logs, AgentPaths.Data })
            Directory.CreateDirectory(dir);
    }

    private static string Collapse(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 500 ? single : single[..500] + "…";
    }
}
