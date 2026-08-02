using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

public enum ApprovalDecision { Approved, Denied }

public sealed record PendingApproval(
    string Token,
    CapabilityRequest Request,
    string ClaudeSummary,
    string TouchedSummary,
    IReadOnlyList<string> ChangedPaths,
    string PreRequestSha,
    decimal CostUsd,
    DateTimeOffset RaisedUtc,
    ApprovalDecision? Decision = null);

/// <summary>
/// The owner's yes/no on a change that reached outside the auto-deploy surface. Core changes are
/// not blocked - they are queued behind a human. The agent may propose anything, including
/// rewriting its own conversation loop; it just cannot deploy that without a reply.
///
/// Lives here, in the supervisor, and is resolved from the Signal socket the supervisor owns -
/// which is the whole reason the socket moved. If the agent relayed approvals it could forge one
/// the moment it saw a token in transit, and the gate would mean nothing.
///
/// Persisted, because the supervisor restarts the agent constantly and an approval outstanding
/// across a restart is the normal case, not an edge case. At most one is pending at a time: while
/// one is outstanding the repo HEAD is ahead of what's deployed, so a second build would stack
/// unapproved code underneath approved code.
/// </summary>
public sealed class ApprovalQueue(ILogger<ApprovalQueue> _logger)
{
    private static readonly string StatePath = Path.Combine(Workspace.State, "approval.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Bare yes/no is accepted because only one approval can ever be outstanding, so there is
    // nothing to disambiguate. The token is offered anyway, and honoured when given, for the case
    // where the owner replies hours later to a message they have to scroll back to find.
    private static readonly Regex ApprovePattern =
        new(@"^\s*(approve[d]?|yes|y|ok|okay|go|ship|do it)\b\W*([0-9a-f]{4})?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DenyPattern =
        new(@"^\s*(den(y|ied)|no|n|reject|cancel|stop|don'?t)\b\W*([0-9a-f]{4})?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Held by two threads: the Signal read loop (resolving a reply) and the supervisor loop
    // (raising, reading, clearing). Invariant worth keeping: never hold it across a Signal send.
    // A send waits on a response that arrives via the read loop, and the read loop is the other
    // thing that wants this lock - so holding it across one would stall both until the send's
    // 30-second timeout unwound it.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<PendingApproval?> GetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await ReadAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RaiseAsync(PendingApproval approval, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WriteAsync(approval, ct);
            _logger.LogInformation(
                "Approval {Token} raised for request {Id} ({Touched})",
                approval.Token, approval.Request.Id, approval.TouchedSummary);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(StatePath))
                File.Delete(StatePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Called from the Signal gateway for every allowlisted inbound message, before it
    /// becomes an inbox envelope. Returns true if the message was an approval reply and has been
    /// consumed - in which case the agent never sees it, which is the point.</summary>
    public async Task<bool> TryConsumeReplyAsync(string text, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var pending = await ReadAsync(ct);
            if (pending is null || pending.Decision is not null)
                return false;

            var decision = Classify(text, pending.Token);
            if (decision is null)
                return false;

            await WriteAsync(pending with { Decision = decision }, ct);
            _logger.LogInformation("Approval {Token} resolved: {Decision}", pending.Token, decision);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static ApprovalDecision? Classify(string text, string token)
    {
        if (Match(ApprovePattern, text, token))
            return ApprovalDecision.Approved;

        if (Match(DenyPattern, text, token))
            return ApprovalDecision.Denied;

        return null;
    }

    /// <summary>A supplied token must be the right one. Replying "yes 1234" when 7f3a is pending
    /// is not a typo to be helpfully ignored - it most likely means the owner is answering an
    /// older message, and treating it as approval of the current change would be exactly wrong.</summary>
    private static bool Match(Regex pattern, string text, string token)
    {
        var match = pattern.Match(text);
        if (!match.Success)
            return false;

        var supplied = match.Groups[^1].Value;
        return string.IsNullOrEmpty(supplied) || supplied.Equals(token, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Four hex characters - long enough that a stray "yes 1234" in conversation won't
    /// collide, short enough to type on a phone.</summary>
    public static string NewToken() => Random.Shared.Next(0x1000, 0x10000).ToString("x4");

    private static async Task<PendingApproval?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(StatePath))
            return null;

        var json = await File.ReadAllTextAsync(StatePath, ct);
        return JsonSerializer.Deserialize<PendingApproval>(json);
    }

    private static async Task WriteAsync(PendingApproval approval, CancellationToken ct)
    {
        Directory.CreateDirectory(Workspace.State);
        var tmp = StatePath + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(approval, JsonOptions), ct);
        File.Move(tmp, StatePath, overwrite: true);
    }
}
