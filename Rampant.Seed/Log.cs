namespace Rampant.Agent;

/// <summary>Append-only operational logs under /workspace/logs, for whoever is looking after this
/// system over SSH. Not memory: this process has no way to read any of it back, and at genesis no
/// way to read files at all.</summary>
public static class Log
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task AppendAsync(string path, string line, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, $"[{DateTimeOffset.UtcNow:O}] {line}{Environment.NewLine}", ct);
        }
        catch
        {
            // Logging must never be the reason a cycle fails.
        }
        finally
        {
            Gate.Release();
        }
    }
}
