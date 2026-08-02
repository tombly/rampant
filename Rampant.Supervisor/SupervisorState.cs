using System.Text.Json;

namespace Rampant.Supervisor;

/// <summary>
/// Persisted under /workspace/state, which is root-owned - the agent can read it and cannot change
/// it. <see cref="LastBuiltSha"/> is the commit the current build came from, which is what makes
/// an ordinary restart recognisable as exactly that rather than as a crash. V1 had no such
/// distinction: the crash-recovery branch was the only path that started the agent on a boot with
/// nothing new to build, so every restart logged "crashed unexpectedly" and tried to text the
/// owner about it.
/// </summary>
public sealed record SupervisorState(
    string? LastBuiltSha,
    int ConsecutiveFailureCount,
    DateTimeOffset? LastWakeTickUtc)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SupervisorState Empty { get; } = new(null, 0, null);

    public static async Task<SupervisorState> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(Workspace.StateFile))
            return Empty;

        var json = await File.ReadAllTextAsync(Workspace.StateFile, ct);
        return JsonSerializer.Deserialize<SupervisorState>(json) ?? Empty;
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Workspace.State);
        var tmp = Workspace.StateFile + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(this, JsonOptions), ct);
        File.Move(tmp, Workspace.StateFile, overwrite: true);
    }
}
