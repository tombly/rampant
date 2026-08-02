using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampant.Agent;

public enum InboxKind
{
    /// <summary>The owner said something. They are waiting for an answer.</summary>
    Message,

    /// <summary>The hourly wake tick. Nobody sent it and nobody is waiting.</summary>
    Wake,

    /// <summary>A capability request has reached a terminal state. Note that the process which
    /// filed it is not this one - deploying restarts the agent - so this envelope is carrying a
    /// conversation across that boundary on a predecessor's behalf.</summary>
    Outcome,
}

public sealed record OutcomeDetail(
    string RequestId,
    string Subject,
    string Status,
    string Detail,
    DateTimeOffset CompletedUtc,
    decimal CostUsd,
    IReadOnlyList<string> ChangedPaths);

/// <summary>One thing to react to. Written only by the supervisor, which owns the Signal socket -
/// so an inbound message has already passed the sender allowlist before it exists as a file, and
/// nothing that reaches this process needs its own authentication check.</summary>
public sealed record InboxEnvelope(
    InboxKind Kind,
    DateTimeOffset ReceivedUtc,
    string? Sender,
    string Text,
    OutcomeDetail? Outcome = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<InboxEnvelope> ReadAsync(string path, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(path, ct);

        try
        {
            if (JsonSerializer.Deserialize<InboxEnvelope>(content, JsonOptions) is { Text: not null } envelope)
                return envelope;
        }
        catch (JsonException)
        {
            // Fall through: a plain text file dropped in by hand, which is how the system is
            // driven locally without Signal (see the README's quick start).
        }

        return new InboxEnvelope(InboxKind.Message, File.GetLastWriteTimeUtc(path), Sender: null, content.Trim());
    }
}
