using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

/// <summary>
/// Owns the Signal channel in both directions, on behalf of a process that cannot rewrite itself.
///
/// Inbound: reject group traffic, reject anyone not on the allowlist, take approval replies out of
/// the stream, and write whatever is left into /workspace/inbox as an envelope. Outbound: watch
/// /workspace/outbox and send what the agent put there.
///
/// This is the V2 answer to a V1 failure. The agent used to hold the socket and do its own
/// allowlist check, which meant (a) access control was a default the agent could rewrite, and it
/// twice reinvented it differently and once incompletely on a fresh genesis, and (b) a bad
/// self-edit could cost the agent its only reply path, as one actually did - recovery was a wipe,
/// because it could no longer receive the instruction to fix itself. Here the agent speaks by
/// writing a file, which no self-edit and no self-built tool can take away from it.
/// </summary>
public sealed class SignalGateway(
    SignalClient _signal,
    SupervisorConfig _config,
    ApprovalQueue _approvals,
    ILogger<SignalGateway> _logger) : BackgroundService
{
    private static readonly TimeSpan OutboxPollInterval = TimeSpan.FromSeconds(1);
    private const int MaxSendAttempts = 5;

    private readonly ConcurrentDictionary<string, int> _sendAttempts = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_config.OwnerSignalIds.Count == 0)
        {
            _logger.LogWarning(
                "{Var} is not set - every inbound Signal message will be rejected (failing closed) and no outbound message can be addressed. See .env.example.",
                SupervisorConfig.OwnerIdEnvVar);
        }

        _ = Task.Run(() => _signal.RunAsync(OnInboundAsync, stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PumpOutboxAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Outbox pump error");
            }

            await Task.Delay(OutboxPollInterval, stoppingToken);
        }
    }

    /// <summary>The supervisor's own voice: approval prompts, build-failure alerts, crash reports.
    /// Distinct from the agent's replies, which come through the outbox.</summary>
    public async Task NotifyOwnerAsync(string text, CancellationToken ct)
    {
        if (_config.PrimaryOwnerId is not { } owner)
        {
            _logger.LogWarning("Cannot notify owner - {Var} is unset. Message was: {Text}", SupervisorConfig.OwnerIdEnvVar, text);
            return;
        }

        try
        {
            await _signal.SendAsync(owner, text, ct);
        }
        catch (Exception ex)
        {
            // Best-effort: a notification failure must never take down the supervisor. It is
            // logged rather than swallowed, though - V1 discarded the error and the alert channel
            // was consequently never confirmed to deliver anything at all.
            _logger.LogError(ex, "Failed to send supervisor notification to owner");
        }
    }

    private async Task OnInboundAsync(SignalInbound inbound)
    {
        var now = DateTimeOffset.UtcNow;

        if (inbound.IsGroupMessage)
        {
            await LogRejectedAsync(inbound, now, "group message");
            return;
        }

        if (!_config.OwnerSignalIds.Contains(inbound.Sender))
        {
            await LogRejectedAsync(inbound, now, $"sender not in {SupervisorConfig.OwnerIdEnvVar}");
            return;
        }

        // Approval replies are consumed here and never become an inbox envelope. The process whose
        // core is being changed does not get to see, relay, or forge the owner's answer.
        if (await _approvals.TryConsumeReplyAsync(inbound.Text, CancellationToken.None))
            return;

        await new InboxEnvelope(InboxKind.Message, now, inbound.Sender, inbound.Text)
            .WriteAsync(CancellationToken.None);
    }

    /// <summary>Rejected messages are never silently dropped - one file per message, so a real
    /// misconfiguration (wrong or unset owner id) is discoverable rather than presenting as "it
    /// just doesn't reply to anyone."</summary>
    private async Task LogRejectedAsync(SignalInbound inbound, DateTimeOffset at, string reason)
    {
        _logger.LogWarning("Rejected inbound Signal message from {Sender}: {Reason}", inbound.Sender, reason);

        Directory.CreateDirectory(Workspace.RejectedSenderLogs);
        var path = Path.Combine(Workspace.RejectedSenderLogs, $"{at:yyyyMMdd-HHmmss-fffffff}_{Sanitize(inbound.Sender)}.txt");
        await File.WriteAllTextAsync(path, $"reason: {reason}{Environment.NewLine}{inbound.Text}", CancellationToken.None);
    }

    private async Task PumpOutboxAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Workspace.Outbox))
            return;

        var files = Directory.GetFiles(Workspace.Outbox)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
                return;

            var content = await File.ReadAllTextAsync(file, ct);

            // A JSON envelope when the agent wrote it, plain text when a human dropped a file in
            // for local testing (see README's quick start). Both address the owner by default.
            var envelope = OutboxEnvelope.TryParse(content)
                ?? new OutboxEnvelope(content.Trim(), null);

            if (string.IsNullOrWhiteSpace(envelope.Text))
            {
                Archive(file, Workspace.OutboxSent);
                continue;
            }

            var recipient = envelope.To ?? _config.PrimaryOwnerId;
            if (recipient is null)
            {
                _logger.LogError("Outbox entry {File} has no recipient and no owner is configured; archiving unsent", Path.GetFileName(file));
                Archive(file, Path.Combine(Workspace.Outbox, ".failed"));
                continue;
            }

            try
            {
                await _signal.SendAsync(recipient, envelope.Text, ct);
                _sendAttempts.TryRemove(file, out _);
                Archive(file, Workspace.OutboxSent);
            }
            catch (Exception ex)
            {
                var attempts = _sendAttempts.AddOrUpdate(file, 1, (_, n) => n + 1);
                _logger.LogWarning(
                    "Failed to send outbox entry {File} (attempt {Attempt}/{Max}): {Message}",
                    Path.GetFileName(file), attempts, MaxSendAttempts, ex.Message);

                if (attempts >= MaxSendAttempts)
                {
                    // Give up rather than hot-loop forever on a permanently bad recipient - but
                    // keep the text, because it is something the agent already believes it said.
                    _sendAttempts.TryRemove(file, out _);
                    Archive(file, Path.Combine(Workspace.Outbox, ".failed"));
                }

                return; // preserve ordering: don't send later messages ahead of a stuck one
            }
        }
    }

    private static void Archive(string file, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        File.Move(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
    }

    private static string Sanitize(string identifier)
        => string.Concat(identifier.Where(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '_'));
}
