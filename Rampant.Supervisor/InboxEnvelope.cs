using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampant.Supervisor;

public enum InboxKind
{
    /// <summary>The owner said something over Signal.</summary>
    Message,

    /// <summary>The hourly tick. Nobody is waiting for an answer; silence is the expected
    /// response most of the time.</summary>
    Wake,

    /// <summary>A capability request the agent's *predecessor* filed has reached a terminal state.
    /// Carries the original conversation back across the restart boundary.</summary>
    Outcome,
}

/// <summary>
/// One file in /workspace/inbox = one thing for the agent to react to. Written only by the
/// supervisor, which is the sole owner of the Signal socket - so an inbound message has already
/// passed the sender allowlist before it exists as a file, and approval replies never appear here
/// at all.
///
/// That placement is a change from V1, where the agent held the socket and did its own allowlist
/// check. Moving it makes access control structural rather than a default the agent could rewrite
/// (tenet 5), and it means the owner's yes/no on a core change cannot be forged by the process the
/// change is being made to.
/// </summary>
public sealed record InboxEnvelope(
    InboxKind Kind,
    DateTimeOffset ReceivedUtc,
    string? Sender,
    string Text,
    RequestOutcome? Outcome = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Writes atomically (temp file then rename) so the agent's directory scan can never
    /// pick up a half-written envelope. The filename is sortable and carries the kind, so
    /// `ls /workspace/inbox` is a readable account of what happened without opening anything.</summary>
    public async Task WriteAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Workspace.Inbox);
        var name = $"{ReceivedUtc:yyyyMMdd-HHmmss-fffffff}-{Kind.ToString().ToLowerInvariant()}.json";
        var path = Path.Combine(Workspace.Inbox, name);
        var tmp = Path.Combine(Workspace.Inbox, "." + name + ".tmp");

        await File.WriteAllTextAsync(tmp, ToJson(), ct);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>What the agent writes into /workspace/outbox to speak. The supervisor picks it up,
/// sends it, and files it under .sent/. <see cref="To"/> may be null, meaning "the owner" - the
/// agent does not need to know or keep track of Signal identifiers to answer.</summary>
public sealed record OutboxEnvelope(string Text, string? To)
{
    public static OutboxEnvelope? TryParse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<OutboxEnvelope>(json);
            return string.IsNullOrWhiteSpace(parsed?.Text) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
