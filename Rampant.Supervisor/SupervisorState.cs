using System.Text.Json;

namespace Rampant.Supervisor;

/// <summary>
/// Persisted outside /workspace/agent so it's never in the agent's own normal reach.
/// LastBuiltSha tracks the last commit we *attempted* a build for, successful or not - this
/// avoids hot-looping a rebuild of the same broken commit every poll cycle. A build failure
/// leaves the previously-running (last-known-good) binary untouched; the agent learns about the
/// failure from its own next cycle reading the build-failure log, not from this state file.
/// </summary>
public sealed record SupervisorState(string? LastBuiltSha, int ConsecutiveFailureCount)
{
    private const string StatePath = "/workspace/supervisor-state.json";

    public static SupervisorState Empty { get; } = new(null, 0);

    public static async Task<SupervisorState> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(StatePath))
            return Empty;

        await using var stream = File.OpenRead(StatePath);
        return await JsonSerializer.DeserializeAsync<SupervisorState>(stream, cancellationToken: ct)
            ?? Empty;
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await using var stream = File.Create(StatePath);
        await JsonSerializer.SerializeAsync(stream, this, cancellationToken: ct);
    }
}
