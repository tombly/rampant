using System.Diagnostics;

namespace Rampant.Agent;

public sealed record ClaudeCodeResult(bool Success, string Output, string ErrorOutput);

/// <summary>Invokes Claude Code headlessly as the agent's "hands." Concurrent async stdout/stderr
/// reading (avoids the classic single-stream-first deadlock), a hard timeout with a full
/// process-tree kill (Claude Code's Bash tool may spawn its own children, e.g. test runs), and a
/// deliberately constructed minimal environment rather than passing through everything the parent
/// process has - this is what makes "no shared credentials with Ancela" true by construction.
/// </summary>
public sealed class ClaudeCodeRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<ClaudeCodeResult> RunAsync(string prompt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("claude")
        {
            WorkingDirectory = "/workspace/agent",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add("bypassPermissions");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(Environment.GetEnvironmentVariable("RAMPANT_MODEL") ?? "claude-sonnet-5");
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add(Environment.GetEnvironmentVariable("RAMPANT_MAX_TURNS") ?? "40");
        psi.ArgumentList.Add("--no-session-persistence");

        var maxBudget = Environment.GetEnvironmentVariable("RAMPANT_MAX_BUDGET_USD");
        if (!string.IsNullOrWhiteSpace(maxBudget))
        {
            psi.ArgumentList.Add("--max-budget-usd");
            psi.ArgumentList.Add(maxBudget);
        }

        // Deliberately constructed environment, not a passthrough of everything this process has.
        psi.EnvironmentVariables.Clear();
        CopyIfSet(psi, "PATH");
        CopyIfSet(psi, "HOME");
        CopyIfSet(psi, "ANTHROPIC_API_KEY");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new ClaudeCodeResult(false, string.Empty, $"claude timed out after {Timeout}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ClaudeCodeResult(process.ExitCode == 0, stdout, stderr);
    }

    private static void CopyIfSet(ProcessStartInfo psi, string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(value))
            psi.EnvironmentVariables[variable] = value;
    }
}
