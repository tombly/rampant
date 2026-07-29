using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

/// <summary>
/// The single most important file in the system. Detects a new git HEAD in /workspace/agent,
/// rebuilds, and only swaps/restarts on a successful build - a bad self-edit that doesn't
/// compile simply never takes effect, with no human review required to get that protection.
/// Deliberately does not depend on the agent's own code cooperating (no "please restart" marker
/// file): a broken self-edit must still be recoverable even if the agent's process is currently
/// crash-looping or unable to run at all.
/// </summary>
public sealed class ProcessSupervisor(
    ISeedBootstrap _seedBootstrap,
    IBuildRunner _buildRunner,
    IAgentProcess _agentProcess,
    SignalNotifier _notifier,
    ILogger<ProcessSupervisor> _logger) : BackgroundService
{
    private const string AgentRepoPath = "/workspace/agent";
    private const string BuildStagingPath = "/workspace/build/staging";
    private const string BuildCurrentPath = "/workspace/build/current";
    private const string BuildPreviousPath = "/workspace/build/previous";
    private const string BuildFailureLogDir = "/workspace/logs/build-failures";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CrashBackoff = TimeSpan.FromSeconds(5);

    // Tracks whether the current down-state has already been reported, so a persistent crash
    // loop sends one alert per episode rather than one every PollInterval forever.
    private bool _crashAlreadyNotified;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _seedBootstrap.EnsureSeededAsync(stoppingToken);

        var state = await SupervisorState.LoadAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var currentSha = await GetHeadShaAsync(stoppingToken);

            if (currentSha is not null && currentSha != state.LastBuiltSha)
            {
                _logger.LogInformation("New commit detected ({Sha}); rebuilding", currentSha);
                var result = await _buildRunner.BuildAsync(AgentRepoPath, BuildStagingPath, stoppingToken);

                if (result.Success)
                {
                    PromoteStaging();
                    await RestartAgentAsync(stoppingToken);
                    state = state with { LastBuiltSha = currentSha, ConsecutiveFailureCount = 0 };
                }
                else
                {
                    await LogBuildFailureAsync(result, stoppingToken);
                    await _notifier.NotifyAsync(
                        $"Self-edit failed to build (commit {currentSha[..7]}). Previous version is still running - details in /workspace/logs/build-failures/.",
                        stoppingToken);
                    // Mark this sha as attempted so we don't hot-loop retrying the same broken
                    // commit every poll cycle. The running last-known-good binary is untouched;
                    // the agent's own next cycle reads the failure log and can fix it forward.
                    state = state with
                    {
                        LastBuiltSha = currentSha,
                        ConsecutiveFailureCount = state.ConsecutiveFailureCount + 1,
                    };
                }

                await state.SaveAsync(stoppingToken);
            }

            if (!_agentProcess.IsRunning && Directory.Exists(BuildCurrentPath))
            {
                _logger.LogWarning("Agent process is not running; restarting last-known-good after backoff");
                if (!_crashAlreadyNotified)
                {
                    await _notifier.NotifyAsync(
                        "Agent process crashed unexpectedly and is being restarted from the last-known-good build.",
                        stoppingToken);
                    _crashAlreadyNotified = true;
                }

                await Task.Delay(CrashBackoff, stoppingToken);
                StartAgentFromCurrent();
            }
            else if (_agentProcess.IsRunning)
            {
                _crashAlreadyNotified = false;
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RestartAgentAsync(CancellationToken ct)
    {
        await _agentProcess.StopAsync(StopGracePeriod, ct);
        StartAgentFromCurrent();
    }

    private void StartAgentFromCurrent()
    {
        var dllPath = Directory.GetFiles(BuildCurrentPath, "*.dll")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                .Equals("Rampant.Agent", StringComparison.OrdinalIgnoreCase));

        if (dllPath is null)
        {
            _logger.LogError("Could not find Rampant.Agent.dll under {Path}", BuildCurrentPath);
            return;
        }

        _agentProcess.Start(dllPath);
    }

    private static void PromoteStaging()
    {
        if (Directory.Exists(BuildPreviousPath))
            Directory.Delete(BuildPreviousPath, recursive: true);

        if (Directory.Exists(BuildCurrentPath))
            Directory.Move(BuildCurrentPath, BuildPreviousPath);

        Directory.Move(BuildStagingPath, BuildCurrentPath);
    }

    private static async Task<string?> GetHeadShaAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = AgentRepoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0 ? output.Trim() : null;
    }

    private static async Task LogBuildFailureAsync(BuildResult result, CancellationToken ct)
    {
        Directory.CreateDirectory(BuildFailureLogDir);
        var path = Path.Combine(BuildFailureLogDir, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
        await File.WriteAllTextAsync(path, $"STDOUT:\n{result.Output}\n\nSTDERR:\n{result.ErrorOutput}", ct);
    }
}
