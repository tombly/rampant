using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

/// <summary>
/// Build, promote, restart - the three steps between a commit and a running agent, and the free
/// correctness gate in the middle of them. A commit that does not compile simply never takes
/// effect, with no human review needed to get that protection, and the last-known-good build keeps
/// running while a broken one is rolled back.
/// </summary>
public sealed class Deployer(
    IBuildRunner _buildRunner,
    IAgentProcess _agentProcess,
    ILogger<Deployer> _logger)
{
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(10);

    public bool IsAgentRunning => _agentProcess.IsRunning;

    public bool HasBuild => Directory.Exists(Workspace.BuildCurrent);

    public Task<BuildResult> BuildAsync(CancellationToken ct)
        => _buildRunner.BuildAsync(Workspace.AgentRepo, Workspace.BuildStaging, ct);

    public Task StopAgentAsync(CancellationToken ct)
        => _agentProcess.StopAsync(StopGracePeriod, ct);

    /// <summary>Swaps staging into place, keeping the outgoing build as `previous`. Not a rollback
    /// mechanism - a bad build never gets here - but it makes "what was running before this
    /// deploy" answerable over SSH.</summary>
    public void Promote()
    {
        if (Directory.Exists(Workspace.BuildPrevious))
            Directory.Delete(Workspace.BuildPrevious, recursive: true);

        if (Directory.Exists(Workspace.BuildCurrent))
            Directory.Move(Workspace.BuildCurrent, Workspace.BuildPrevious);

        Directory.Move(Workspace.BuildStaging, Workspace.BuildCurrent);
        _logger.LogInformation("Promoted staging build to current");
    }

    public void StartAgent()
    {
        var dllPath = Directory.GetFiles(Workspace.BuildCurrent, "*.dll")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                .Equals("Rampant.Agent", StringComparison.OrdinalIgnoreCase));

        if (dllPath is null)
        {
            _logger.LogError("Could not find Rampant.Agent.dll under {Path}", Workspace.BuildCurrent);
            return;
        }

        _agentProcess.Start(dllPath);
    }

    public async Task LogBuildFailureAsync(BuildResult result, string requestId, CancellationToken ct)
    {
        Directory.CreateDirectory(Workspace.BuildFailureLogs);
        var path = Path.Combine(Workspace.BuildFailureLogs, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{requestId}.log");
        await File.WriteAllTextAsync(path, $"STDOUT:\n{result.Output}\n\nSTDERR:\n{result.ErrorOutput}", ct);
    }
}
