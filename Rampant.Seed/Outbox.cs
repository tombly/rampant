using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampant.Agent;

/// <summary>
/// How this process speaks - by writing a file. The supervisor watches this directory and sends
/// what it finds over Signal.
///
/// It is a file drop rather than a socket for a reason worth understanding before changing it. An
/// agent holding the Signal connection itself can lose its only reply path to a bad self-edit -
/// which has happened, and recovery was wiping the machine, because there was no longer any way to
/// send it the instruction to fix itself. Writing a file is something no self-edit and no
/// self-built tool can take away.
///
/// Available to tools as well as to the message loop: a background timer, a scheduled job, or
/// anything else that needs to reach the owner outside of a reply calls
/// <see cref="WriteAsync"/>.
/// </summary>
public static class Outbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int _sequence;

    /// <summary>Queues a message to the owner. Returns as soon as it is on disk; delivery is the
    /// supervisor's job and happens within a second or so.</summary>
    /// <param name="to">Leave null for the owner, which is almost always what you want - the
    /// agent does not need to track Signal identifiers to answer.</param>
    public static async Task WriteAsync(string text, string? to = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        Directory.CreateDirectory(AgentPaths.Outbox);

        // A monotonic suffix, because two messages written inside the same 100ns tick would
        // otherwise collide, and the supervisor sends in filename order.
        var seq = Interlocked.Increment(ref _sequence);
        var name = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}-{seq:D4}.json";
        var path = Path.Combine(AgentPaths.Outbox, name);
        var tmp = Path.Combine(AgentPaths.Outbox, "." + name + ".tmp");

        // Write then rename: the supervisor polls this directory, and a half-written file would
        // be sent as a truncated message. It skips dotfiles, so the temp name is invisible to it.
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(new OutboxEnvelope(text, to), JsonOptions), ct);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed record OutboxEnvelope(string Text, string? To);
}
