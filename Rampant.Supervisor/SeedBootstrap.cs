using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

public interface ISeedBootstrap
{
    Task EnsureSeededAsync(CancellationToken ct);
}

/// <summary>First-run only: copies the genesis Rampant.Agent template (baked into the image at
/// /opt/seed) into the empty /workspace/agent volume and commits it as the one piece of "agent"
/// source that isn't agent-authored. Everything after this genesis commit is up to the agent.</summary>
public sealed class SeedBootstrap(ILogger<SeedBootstrap> _logger) : ISeedBootstrap
{
    private const string SeedSourcePath = "/opt/seed";
    private const string AgentRepoPath = "/workspace/agent";

    public async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (Directory.Exists(AgentRepoPath) && Directory.EnumerateFileSystemEntries(AgentRepoPath).Any())
        {
            _logger.LogInformation("Agent workspace already seeded at {Path}", AgentRepoPath);
            return;
        }

        _logger.LogInformation("Seeding genesis agent from {Source} into {Dest}", SeedSourcePath, AgentRepoPath);
        Directory.CreateDirectory(AgentRepoPath);
        CopyDirectory(SeedSourcePath, AgentRepoPath);

        foreach (var dir in new[] { "memory", "inbox", "outbox", "logs" })
            Directory.CreateDirectory(Path.Combine("/workspace", dir));

        // Git commit fails outright without a configured identity - bake in a fixed default
        // rather than leaving the very first commit to chance.
        await RunGitAsync(AgentRepoPath, ct, "init");
        await RunGitAsync(AgentRepoPath, ct, "config", "user.name", "Rampant");
        await RunGitAsync(AgentRepoPath, ct, "config", "user.email", "rampant@localhost");
        await RunGitAsync(AgentRepoPath, ct, "add", "-A");
        await RunGitAsync(AgentRepoPath, ct, "commit", "-m", "Genesis commit");

        _logger.LogInformation("Genesis commit created.");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: false);
    }

    private static async Task RunGitAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {string.Join(' ', args)}");

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }
    }
}
