using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

public interface ISeedBootstrap
{
    Task EnsureSeededAsync(CancellationToken ct);
}

/// <summary>First run only: copies the genesis agent template (baked into the image at /opt/seed)
/// into the empty /workspace/agent and commits it as the one piece of agent source that isn't
/// agent-authored. Everything after this genesis commit arrives through the request pipeline.
///
/// The directory layout itself is created by docker-entrypoint.sh, as root, before this runs -
/// ownership is the security model and it has to be right before anything else touches the
/// filesystem.</summary>
public sealed class SeedBootstrap(ILogger<SeedBootstrap> _logger) : ISeedBootstrap
{
    private const string SeedSourcePath = "/opt/seed";

    public async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (Directory.Exists(Workspace.AgentRepo) && Directory.EnumerateFileSystemEntries(Workspace.AgentRepo).Any())
        {
            _logger.LogInformation("Agent workspace already seeded at {Path}", Workspace.AgentRepo);
            return;
        }

        _logger.LogInformation("Seeding genesis agent from {Source} into {Dest}", SeedSourcePath, Workspace.AgentRepo);
        Directory.CreateDirectory(Workspace.AgentRepo);
        CopyDirectory(SeedSourcePath, Workspace.AgentRepo);

        // The copy runs as root, so every file lands root-owned even though the directory belongs
        // to the builder - and git, which runs as the builder, could then not write a single one of
        // them. Hand the tree over before the first commit.
        RunAs.Chown(RunAs.Builder, Workspace.AgentRepo);

        // Git refuses to commit without a configured identity - bake in a fixed default rather
        // than leaving the very first commit to chance.
        await RunAsync(ct, "init");
        await RunAsync(ct, "config", "user.name", "Rampant");
        await RunAsync(ct, "config", "user.email", "rampant@localhost");
        await RunAsync(ct, "add", "-A");
        await RunAsync(ct, "commit", "-m", "Genesis commit");

        _logger.LogInformation("Genesis commit created.");
    }

    private static async Task RunAsync(CancellationToken ct, params string[] args)
    {
        var result = await Git.RunAsync(Workspace.AgentRepo, ct, args);
        if (!result.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Combined}");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: false);
    }
}
