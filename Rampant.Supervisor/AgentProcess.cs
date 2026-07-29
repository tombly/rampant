using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

public interface IAgentProcess
{
    bool IsRunning { get; }
    void Start(string dllPath);
    Task StopAsync(TimeSpan gracePeriod, CancellationToken ct);
}

/// <summary>Starts/stops/monitors the compiled agent binary. Graceful stop sends SIGTERM (giving
/// an in-flight git commit a chance to finish) and only hard-kills the process tree after the
/// grace period expires.</summary>
public sealed class AgentProcess(ILogger<AgentProcess> _logger) : IAgentProcess
{
    private const string AgentWorkingDirectory = "/workspace/agent";

    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string dllPath)
    {
        if (IsRunning)
            throw new InvalidOperationException("Agent process is already running.");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AgentWorkingDirectory,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dllPath);

        _process = Process.Start(psi);
        _logger.LogInformation("Started agent process (pid {Pid}) from {DllPath}", _process?.Id, dllPath);
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
        // System.Diagnostics.Process has no portable "send SIGTERM" (Process.Kill sends SIGKILL
        // on Unix) - shell out to `kill` so the agent gets a chance to finish an in-flight commit.
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
