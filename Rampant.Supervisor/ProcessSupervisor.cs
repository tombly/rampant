using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

/// <summary>
/// The supervisor's main loop. It does four things, in order, forever: resolve capability
/// requests, tick the agent's clock, publish what the agent is allowed to know about its budget,
/// and make sure the agent is actually running.
///
/// It deliberately does not depend on the agent's code cooperating - no "please restart" marker
/// file, no handshake. A broken self-edit has to stay recoverable even when the agent is
/// crash-looping or cannot run at all, which under V2 also means the owner can still reach it:
/// the Signal channel belongs to this process, not to the one being edited.
/// </summary>
public sealed class ProcessSupervisor(
    ISeedBootstrap _seedBootstrap,
    Deployer _deployer,
    RequestPipeline _pipeline,
    WakeTicker _wakeTicker,
    StatusWriter _statusWriter,
    SignalGateway _signal,
    ILogger<ProcessSupervisor> _logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CrashBackoff = TimeSpan.FromSeconds(5);

    /// <summary>Reported crashes are one per episode, not one per poll - a persistent crash loop
    /// should not become a persistent stream of texts.</summary>
    private bool _crashAlreadyNotified;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _seedBootstrap.EnsureSeededAsync(stoppingToken);

        var state = await SupervisorState.LoadAsync(stoppingToken);
        state = await ColdStartAsync(state, stoppingToken);
        await state.SaveAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deployedSha = await _pipeline.RunOnceAsync(stoppingToken);
                if (deployedSha is not null)
                    state = state with { LastBuiltSha = deployedSha, ConsecutiveFailureCount = 0 };

                var ticked = await _wakeTicker.TickIfDueAsync(state, stoppingToken);
                if (ticked != state || deployedSha is not null)
                {
                    state = ticked;
                    await state.SaveAsync(stoppingToken);
                }

                await _statusWriter.WriteAsync(stoppingToken);
                await EnsureAgentRunningAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The supervisor is the last thing standing when the agent is broken. It swallows
                // its own errors rather than exiting, because a supervisor that dies takes the
                // owner's only route back in with it.
                _logger.LogError(ex, "Supervisor cycle error");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    /// <summary>
    /// The explicit "start for the first time" path V1 never had. There, a boot with nothing new to
    /// build fell through to the crash-recovery branch - the only path that started the agent at
    /// all - so every ordinary restart logged a crash, alerted the owner, and ate a pointless
    /// backoff. Here a cold start is its own case, and the crash branch below means what it says.
    /// </summary>
    private async Task<SupervisorState> ColdStartAsync(SupervisorState state, CancellationToken ct)
    {
        var sha = await Git.HeadShaAsync(Workspace.AgentRepo, ct);
        var needsBuild = sha is not null && (sha != state.LastBuiltSha || !_deployer.HasBuild);

        if (needsBuild)
        {
            _logger.LogInformation("Cold start: building agent at {Sha}", sha![..7]);
            var build = await _deployer.BuildAsync(ct);

            if (build.Success)
            {
                _deployer.Promote();
                state = state with { LastBuiltSha = sha, ConsecutiveFailureCount = 0 };
            }
            else
            {
                await _deployer.LogBuildFailureAsync(build, "coldstart", ct);
                state = state with { ConsecutiveFailureCount = state.ConsecutiveFailureCount + 1 };

                if (!_deployer.HasBuild)
                {
                    // Nothing has ever built here. There is no last-known-good to fall back to, so
                    // the agent simply cannot run - which is exactly the situation the owner needs
                    // to be told about, since nothing else in the system will speak up.
                    _logger.LogError("Cold start build failed and there is no previous build to fall back on");
                    await _signal.NotifyOwnerAsync(
                        "Rampant started but its agent has never built successfully, so nothing is running. Details in /workspace/logs/build-failures/.",
                        ct);
                    return state;
                }

                _logger.LogWarning("Cold start build failed; starting last-known-good build instead");
                await _signal.NotifyOwnerAsync(
                    "Rampant restarted, but the current agent commit does not compile. It is running the last version that did. Details in /workspace/logs/build-failures/.",
                    ct);
            }
        }

        if (_deployer.HasBuild)
        {
            _deployer.StartAgent();
            _logger.LogInformation("Cold start complete");
        }

        return state;
    }

    private async Task EnsureAgentRunningAsync(CancellationToken ct)
    {
        if (_deployer.IsAgentRunning)
        {
            _crashAlreadyNotified = false;
            return;
        }

        if (!_deployer.HasBuild)
            return; // cold start already reported this and there is nothing to restart

        _logger.LogWarning("Agent process is not running; restarting last-known-good after backoff");

        if (!_crashAlreadyNotified)
        {
            await _signal.NotifyOwnerAsync(
                "Rampant's agent process stopped unexpectedly and is being restarted from the last build that worked.",
                ct);
            _crashAlreadyNotified = true;
        }

        await Task.Delay(CrashBackoff, ct);
        _deployer.StartAgent();
    }
}
