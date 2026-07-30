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
    private const string LogDir = "/workspace/logs/extend_self";

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
            var timeoutResult = new ClaudeCodeResult(false, string.Empty, $"claude timed out after {Timeout}");
            await LogInvocationAsync(prompt, timeoutResult);
            return timeoutResult;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var result = new ClaudeCodeResult(process.ExitCode == 0, stdout, stderr);

        await LogInvocationAsync(prompt, result);
        return result;
    }

    private static void CopyIfSet(ProcessStartInfo psi, string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(value))
            psi.EnvironmentVariables[variable] = value;
    }

    /// <summary>Every extend_self invocation - the full prompt sent and Claude Code's complete
    /// raw stdout/stderr - is otherwise invisible: it's handed straight back into RampantBrain's
    /// tool loop and discarded the moment this call returns, leaving only whatever summary the
    /// model chooses to narrate. Persisting it here (mirroring the existing
    /// /workspace/logs/build-failures/ pattern) makes every self-modification attempt SSH-
    /// inspectable after the fact, independent of the model's own account of what happened.
    /// Deliberately CancellationToken.None and best-effort (swallows its own failures) - a
    /// logging problem must never affect the actual extend_self result already computed above.
    /// </summary>
    private static async Task LogInvocationAsync(string prompt, ClaudeCodeResult result)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
            var content = $"PROMPT:\n{prompt}\n\nSUCCESS: {result.Success}\n\nSTDOUT:\n{result.Output}\n\nSTDERR:\n{result.ErrorOutput}";
            await File.WriteAllTextAsync(path, content, CancellationToken.None);
        }
        catch
        {
            // Best-effort - see doc comment above.
        }
    }
}
