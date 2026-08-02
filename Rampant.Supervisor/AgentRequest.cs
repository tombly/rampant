using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampant.Supervisor;

/// <summary>What the agent is asking for. Both kinds arrive by the same file drop and travel the
/// same pipeline, because both need the same context carried across the gap between asking and
/// being answered - but only one of them costs anything.</summary>
public enum RequestKind
{
    /// <summary>Something the agent cannot do. Costs a Claude Code session, a build and a restart,
    /// and is bounded by the spend ledger.</summary>
    Capability,

    /// <summary>A rewrite of one section of the agent's own SELF.md. No model is invoked, nothing
    /// is compiled, and nothing restarts - SELF.md is read from disk on every turn, so the change
    /// is live on the next one.</summary>
    SelfDescription,
}

/// <summary>
/// What the agent writes into /workspace/requests/in when it wants a capability it doesn't have,
/// or when it wants to change how it describes itself. A description in English, never a diff -
/// the agent describes, the supervisor writes.
///
/// It carries more than the description on purpose. The process that files a capability request is
/// never the process that sees the outcome: building restarts the agent. So the request has to hold
/// everything the *successor* needs to finish the job - who to reply to, what the owner actually
/// said, and when they said it. Without that the next process has no idea a conversation happened,
/// and with no memory at genesis there is nowhere else for that context to live.
/// </summary>
public sealed record AgentRequest(
    string Id,
    DateTimeOffset FiledUtc,
    /// <summary>What this request is about, in a few words. The name of the capability wanted, or
    /// the SELF.md heading being rewritten - one field rather than two, because it is the same
    /// thing in both cases: the short label this request is known by, in the log, in the outcome,
    /// and in anything the owner reads.</summary>
    string Subject,
    string Description,
    string? ReplyTo,
    string? OriginalMessage,
    DateTimeOffset? OriginalMessageUtc,
    RequestKind Kind = RequestKind.Capability,
    /// <summary>SelfDescription only. What that section should now say.</summary>
    string? NewText = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Kind arrives from the agent as a string, and System.Text.Json will not bind a string to
        // an enum without this. Missing it would silently leave every revision on the default -
        // turning a free prose edit into a $1 Claude Code run.
        Converters = { new JsonStringEnumConverter() },
    };

    public static AgentRequest? TryParse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<AgentRequest>(json, JsonOptions);
            return string.IsNullOrWhiteSpace(parsed?.Description) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Terminal states a request can reach. Every one of them produces a message back to the
/// owner - the round trip is never allowed to end in silence, because from the owner's side a
/// silent failure and a slow success look identical.</summary>
public enum RequestStatus
{
    /// <summary>Built, gated, deployed. The agent now has the capability.</summary>
    Deployed,

    /// <summary>Refused before spending anything: cooldown, daily budget, or an approval already
    /// outstanding.</summary>
    Refused,

    /// <summary>Claude Code ran but what it produced doesn't compile. The previously running build
    /// is untouched.</summary>
    BuildFailed,

    /// <summary>Claude Code failed, timed out, committed nothing, or produced something that
    /// cannot be deployed as written (see PackagePinPolicy). The detail says which.</summary>
    Failed,

    /// <summary>Touched something outside the auto-deploy surface. Held pending the owner's
    /// reply - the one outcome with a human-length delay in the middle.</summary>
    AwaitingApproval,

    /// <summary>The owner said no. The commit was rolled back.</summary>
    Denied,

    /// <summary>A section of SELF.md was rewritten. Committed, live on the agent's next turn, and
    /// free - distinct from Deployed because nothing was built and nothing restarted.</summary>
    Revised,
}

public sealed record RequestOutcome(
    string RequestId,
    string Subject,
    RequestStatus Status,
    string Detail,
    DateTimeOffset CompletedUtc,
    decimal CostUsd,
    IReadOnlyList<string> ChangedPaths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
