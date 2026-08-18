using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

public interface IAgentProcess
{
    bool IsRunning { get; }
    void Start(string dllPath);
    Task StopAsync(TimeSpan gracePeriod, CancellationToken ct);
}

/// <summary>
/// Starts the agent as a different user, with a different environment, than the process starting
/// it. This is the isolation boundary in about thirty lines: the supervisor runs as root and holds
/// ANTHROPIC_API_KEY; the agent runs as agentrunner with an environment constructed here from an
/// allowlist. It cannot read /proc/1/environ across the uid boundary, so there is no route back to
/// the key - the split is enforced by the kernel rather than by anyone's good behaviour.
///
/// setpriv rather than su: it is not setuid (so it is unaffected by no-new-privileges), and it
/// execs in place rather than forking, so the pid we get back is the agent's own - which matters,
/// because that is the pid we later signal.
/// </summary>
public sealed class AgentProcess(ILogger<AgentProcess> _logger) : IAgentProcess
{
    /// <summary>An allowlist, not a scrub list. A new secret added to .env for the supervisor's
    /// own use must not reach the agent by default, and a deny list gets that wrong the first time
    /// someone forgets to update it.</summary>
    private static readonly string[] PassThroughVariables =
    [
        "OPENAI_API_KEY",
        "RAMPANT_OPENAI_MODEL",
        "TZ",
        "LANG",
    ];

    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string dllPath)
    {
        if (IsRunning)
            throw new InvalidOperationException("Agent process is already running.");

        var psi = RunAs.Command(RunAs.Agent, "dotnet", Workspace.Root, dllPath);

        psi.EnvironmentVariables.Clear();
        // Deliberately excludes /home/builder/.local/bin, where Claude Code lives. The agent could
        // not run it anyway (that home is 0700 and belongs to another uid, and this process holds
        // no API key), but leaving it on the PATH would suggest otherwise.
        psi.EnvironmentVariables["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        psi.EnvironmentVariables["HOME"] = "/home/agentrunner";
        psi.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        foreach (var name in PassThroughVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                psi.EnvironmentVariables[name] = value;
        }

        _process = Process.Start(psi);
        _logger.LogInformation(
            "Started agent process (pid {Pid}) as uid {Uid} from {DllPath}",
            _process?.Id, RunAs.Agent, dllPath);
    }

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
            return;

        var pid = _process.Id;
        _logger.LogInformation("Stopping agent process (pid {Pid}), grace period {Grace}", pid, gracePeriod);

        TrySendSigTerm(pid);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(gracePeriod);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Agent process (pid {Pid}) did not exit within grace period; killing", pid);
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(ct);
        }
    }

    private void TrySendSigTerm(int pid)
    {
        // System.Diagnostics.Process has no portable "send SIGTERM" (Process.Kill sends SIGKILL on
        // Unix), so shell out to `kill`. Root needs CAP_KILL to signal a process under a different
        // uid - see docker-compose.yml. The grace period only means anything if the agent is given
        // a signal it can handle. A grace period that is documented but never actually granted
        // lets a self-triggered restart kill the very cycle that requested it, which has happened.
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill")
            {
                ArgumentList = { "-TERM", pid.ToString() },
                UseShellExecute = false,
            });
            kill?.WaitForExit(1000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SIGTERM to pid {Pid}; will rely on hard kill after grace period", pid);
        }
    }
}
