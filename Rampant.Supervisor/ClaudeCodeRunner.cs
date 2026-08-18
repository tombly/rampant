using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

/// <summary>The outcome of one Claude Code invocation. <paramref name="TerminalReason"/> is set
/// only on failure, and says in plain English *why* the run stopped - a distinction the agent
/// cannot make for itself, because the raw output it would need is in a log it deliberately
/// cannot reach.</summary>
public sealed record ClaudeCodeResult(
    bool Success,
    string Summary,
    decimal CostUsd,
    string RawOutput,
    string ErrorOutput,
    string? TerminalReason = null);

public interface IClaudeCodeRunner
{
    Task<ClaudeCodeResult> RunAsync(AgentRequest request, CancellationToken ct);
}

/// <summary>
/// The only thing in the system that invokes Claude Code - its argv, its API key, its user, its
/// budget flags. Living in the agent's own repo would make every guardrail on it (--max-turns,
/// --max-budget-usd, the model) a line the agent could rewrite. Keeping it here is most of what
/// "the supervisor holds the purse strings" actually means.
///
/// Runs as root. The agent runs as agentrunner and holds no ANTHROPIC_API_KEY - it cannot read
/// root's /proc/1/environ, so the credential split is enforced by the kernel rather than by
/// anybody's good behaviour.
/// </summary>
public sealed class ClaudeCodeRunner(SupervisorConfig _config, ILogger<ClaudeCodeRunner> _logger) : IClaudeCodeRunner
{
    public async Task<ClaudeCodeResult> RunAsync(AgentRequest request, CancellationToken ct)
    {
        var prompt = ExtendSelfPrompt.Build(request);

        // As the builder uid, never as root. Two reasons that turned out to be one: Claude Code
        // refuses --dangerously-skip-permissions outright when it detects root privileges (found
        // the hard way, on the first real capability request), and as root it could have rewritten
        // /opt/supervisor - voiding the first of PLAN.md's four boundaries. Under its own uid it
        // can write the agent's source and nothing else.
        var psi = RunAs.Command(RunAs.Builder, "claude", Workspace.AgentRepo);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add("bypassPermissions");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_config.ClaudeModel);
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add(_config.MaxTurns.ToString());
        psi.ArgumentList.Add("--no-session-persistence");
        // JSON rather than text so the real dollar cost of this run is recorded, not just the fact
        // that a run happened. A cheap build should not consume the same budget as an expensive one.
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--max-budget-usd");
        psi.ArgumentList.Add(_config.MaxBudgetPerInvocationUsd.ToString("0.00"));

        // Constructed, not inherited: this is what makes "the agent has no Anthropic credential"
        // true by construction rather than by omission. HOME and PATH point at the builder's own
        // install of Claude Code (/home/builder, mode 0700), which neither the agent nor anything
        // else in the container can reach.
        psi.EnvironmentVariables.Clear();
        psi.EnvironmentVariables["PATH"] = "/home/builder/.local/bin:/usr/local/bin:/usr/bin:/bin";
        psi.EnvironmentVariables["HOME"] = "/home/builder";
        psi.EnvironmentVariables["NUGET_PACKAGES"] = "/workspace/.nuget/packages";
        psi.EnvironmentVariables["ANTHROPIC_API_KEY"] = _config.AnthropicApiKey ?? string.Empty;

        _logger.LogInformation(
            "Invoking Claude Code for request {Id} ({Subject}), model {Model}, cap ${Cap:0.00}",
            request.Id, request.Subject, _config.ClaudeModel, _config.MaxBudgetPerInvocationUsd);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_config.ClaudeCodeTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Kill the whole tree - Claude Code's Bash tool spawns its own children (test runs,
            // dotnet build), and killing only the parent leaves them holding the repo.
            process.Kill(entireProcessTree: true);
            var timedOut = new ClaudeCodeResult(
                false,
                $"Claude Code timed out after {_config.ClaudeCodeTimeout.TotalMinutes:0} minutes.",
                // The spend is real even though the run was abandoned; charge the per-invocation
                // cap rather than nothing, so a repeatedly timing-out request cannot run free.
                _config.MaxBudgetPerInvocationUsd,
                string.Empty,
                "timed out",
                $"It hit the {_config.ClaudeCodeTimeout.TotalMinutes:0}-minute wall-clock limit on a single build.");

            await LogInvocationAsync(request, prompt, timedOut);
            return timedOut;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var result = Parse(process.ExitCode, stdout, stderr);

        _logger.LogInformation(
            "Claude Code finished for request {Id}: success={Success}, cost=${Cost:0.0000}",
            request.Id, result.Success, result.CostUsd);

        await LogInvocationAsync(request, prompt, result);
        return result;
    }

    private ClaudeCodeResult Parse(int exitCode, string stdout, string stderr)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var summary = root.TryGetProperty("result", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var parsed)
                ? parsed
                : 0m;

            var success = exitCode == 0 && !isError;
            var terminalReason = success ? null : DescribeTermination(root);

            // A run cut short carries no "result" text at all - the field is simply absent. Rather
            // than pass an empty string up the stack, say what the JSON does tell us, so the
            // outcome the agent reads (and the owner hears) explains itself instead of arriving
            // blank. Observed live: a turn-limited run produced is_error with no result, and the
            // approval message that reached the owner had nothing in the "what it did" section.
            if (string.IsNullOrWhiteSpace(summary))
            {
                var turns = root.TryGetProperty("num_turns", out var t) && t.TryGetInt32(out var n) ? n : 0;
                summary = $"Claude Code produced no summary (it stopped after {turns} turns).";
            }

            return new ClaudeCodeResult(success, summary.Trim(), cost, stdout, stderr, terminalReason);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Could not parse Claude Code JSON output; treating as failure. stderr: {Stderr}", stderr);

            // Nothing on stdout at all means it never got as far as a turn - it refused at startup
            // (bad flag, missing key, wrong user) and no tokens were bought. Charging the cap there
            // would let five misconfigured attempts eat an entire day's budget without a single API
            // call, and start a 45-minute cooldown on top. Observed exactly once, which is how this
            // rule got written.
            //
            // Any output at all and we charge the cap instead: it may have spent something before
            // dying, and under-counting real spend is the more dangerous direction to guess.
            var neverStarted = string.IsNullOrWhiteSpace(stdout);
            var reason = string.IsNullOrWhiteSpace(stderr) ? "no output" : stderr.Trim();

            return new ClaudeCodeResult(
                false,
                $"Claude Code produced no usable result ({Truncate(reason, 300)}).",
                neverStarted ? 0m : _config.MaxBudgetPerInvocationUsd,
                stdout,
                stderr);
        }
    }

    /// <summary>
    /// Why the run stopped, in a sentence the agent can act on.
    ///
    /// Claude Code says this in `terminal_reason`, `subtype` and an `errors` array - never in
    /// `stop_reason`, which reports the last API-level stop and read "tool_use" on both of the
    /// runs that made this method necessary. True, and useless: the agent was told only that the
    /// build "stopped partway through", correctly said it could not tell why, and then spent
    /// $1.34 retrying a request that had failed for a reason no retry could change.
    ///
    /// Only `budget_exhausted` is mapped from a confirmed live run. Everything else is passed
    /// through verbatim rather than guessed at - a wrong diagnosis is worse than a raw one,
    /// because the agent will act on it.
    /// </summary>
    private string DescribeTermination(JsonElement root)
    {
        var terminal = root.TryGetProperty("terminal_reason", out var tr) ? tr.GetString() : null;

        var errors = root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
            ? string.Join("; ", e.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            : string.Empty;

        if (terminal == "budget_exhausted")
        {
            // The actionable half. Retrying an over-budget request unchanged buys the same
            // failure at the same price; splitting it is the only move that changes the outcome.
            return $"It ran out of money: a single build may cost at most "
                + $"${_config.MaxBudgetPerInvocationUsd:0.00}, and this one reached that cap before "
                + "finishing. The request was too large to build in one go. Asking for a smaller "
                + "piece of it will do better than retrying the same thing unchanged.";
        }

        if (!string.IsNullOrWhiteSpace(terminal))
        {
            return string.IsNullOrWhiteSpace(errors)
                ? $"Claude Code stopped early, reporting: {terminal}."
                : $"Claude Code stopped early, reporting: {terminal} ({Truncate(errors, 200)}).";
        }

        return string.IsNullOrWhiteSpace(errors)
            ? "Claude Code stopped early without saying why."
            : $"Claude Code stopped early, reporting: {Truncate(errors, 200)}.";
    }

    /// <summary>Every invocation - the full prompt sent, and Claude Code's complete raw output - is
    /// otherwise invisible, leaving only whatever summary the model chooses to narrate. Persisting
    /// it makes each self-modification attempt inspectable after the fact, independently of the
    /// model's own account of what it did. That distinction has already mattered: an agent here
    /// once reported a fix as pre-existing seconds after committing it.
    ///
    /// Best-effort and on CancellationToken.None: a logging failure must never affect a result that
    /// has already been computed and already cost money.</summary>
    private async Task LogInvocationAsync(AgentRequest request, string prompt, ClaudeCodeResult result)
    {
        try
        {
            Directory.CreateDirectory(Workspace.ExtendSelfLogs);
            var path = Path.Combine(Workspace.ExtendSelfLogs, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{request.Id}.log");
            var content = $"""
                REQUEST:
                {request.ToJson()}

                PROMPT:
                {prompt}

                SUCCESS: {result.Success}
                COST_USD: {result.CostUsd:0.0000}

                STDOUT:
                {result.RawOutput}

                STDERR:
                {result.ErrorOutput}
                """;
            await File.WriteAllTextAsync(path, content, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write extend_self log for request {Id}", request.Id);
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
