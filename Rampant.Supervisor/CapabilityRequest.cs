using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampant.Supervisor;

/// <summary>
/// What the agent writes into /workspace/requests/in when it wants a capability it doesn't have.
/// A description in English, never a diff - the agent describes, the supervisor builds.
///
/// It carries more than the description on purpose. The process that files a request is never the
/// process that sees the outcome: building restarts the agent. So the request has to hold
/// everything the *successor* needs to finish the job - who to reply to, what the owner actually
/// said, and when they said it. Without that the next process has no idea a conversation happened,
/// and with no memory at genesis there is nowhere else for that context to live.
/// </summary>
public sealed record CapabilityRequest(
    string Id,
    DateTimeOffset FiledUtc,
    string Capability,
    string Description,
    string? ReplyTo,
    string? OriginalMessage,
    DateTimeOffset? OriginalMessageUtc)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CapabilityRequest? TryParse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<CapabilityRequest>(json);
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
}

public sealed record RequestOutcome(
    string RequestId,
    string Capability,
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
