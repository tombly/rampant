using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

public sealed record SpendDay(string DateUtc, decimal SpentUsd, int Invocations, DateTimeOffset? LastInvocationUtc);

public sealed record SpendVerdict(bool Allowed, string? Reason, TimeSpan CooldownRemaining, decimal RemainingUsd);

/// <summary>
/// The hard spend bound. On disk, keyed by UTC date, under /workspace/state - specifically not in
/// memory, because the supervisor restarts the agent on every self-edit and an in-memory counter
/// would reset at exactly the moment it needed to hold. Not in /workspace/agent either, for the
/// obvious reason.
///
/// Fails closed: if the ledger cannot be read or written, no build happens. A metering system that
/// silently degrades to "unmetered" on an I/O error is not a bound.
/// </summary>
public sealed class SpendLedger(SupervisorConfig _config, ILogger<SpendLedger> _logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Whether a Claude Code invocation may start right now, and if not, why - the reason
    /// is handed back to the agent verbatim so a refusal is actionable ("you have $0.40 left,
    /// try tomorrow") rather than silence.</summary>
    public async Task<SpendVerdict> EvaluateAsync(CancellationToken ct)
    {
        var day = await LoadTodayAsync(ct);
        var remaining = _config.DailyBudgetUsd - day.SpentUsd;

        if (remaining <= 0)
        {
            return new SpendVerdict(
                false,
                $"Daily build budget of ${_config.DailyBudgetUsd:0.00} is spent (${day.SpentUsd:0.00} across {day.Invocations} build{(day.Invocations == 1 ? "" : "s")}). Resets at 00:00 UTC.",
                TimeSpan.Zero,
                0m);
        }

        if (day.LastInvocationUtc is { } last)
        {
            var elapsed = DateTimeOffset.UtcNow - last;
            if (elapsed < _config.BuildCooldown)
            {
                var wait = _config.BuildCooldown - elapsed;
                return new SpendVerdict(
                    false,
                    $"Build cooldown active - {(int)wait.TotalMinutes + 1} more minute(s) before another build can start.",
                    wait,
                    remaining);
            }
        }

        return new SpendVerdict(true, null, TimeSpan.Zero, remaining);
    }

    /// <summary>Records real dollars spent, taken from Claude Code's own JSON output, not a count
    /// of invocations - a cheap build should not consume the same budget as an expensive one. Runs
    /// on CancellationToken.None on purpose: the money is already spent by the time this is called,
    /// and a shutdown racing the write would lose the record but not the charge.</summary>
    public async Task RecordAsync(decimal costUsd, DateTimeOffset atUtc)
    {
        var day = await LoadTodayAsync(CancellationToken.None);
        var updated = day with
        {
            SpentUsd = day.SpentUsd + Math.Max(0m, costUsd),
            Invocations = day.Invocations + 1,
            LastInvocationUtc = atUtc,
        };

        Directory.CreateDirectory(Workspace.LedgerDir);
        var path = PathFor(updated.DateUtc);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(updated, JsonOptions), CancellationToken.None);
        File.Move(tmp, path, overwrite: true);

        _logger.LogInformation(
            "Spend recorded: ${Cost:0.0000} (today ${Total:0.00} of ${Budget:0.00}, {Count} invocations)",
            costUsd, updated.SpentUsd, _config.DailyBudgetUsd, updated.Invocations);
    }

    public async Task<SpendDay> LoadTodayAsync(CancellationToken ct)
    {
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = PathFor(today);

        if (!File.Exists(path))
            return new SpendDay(today, 0m, 0, await LastInvocationAcrossDaysAsync(ct));

        // No try/catch: a corrupt or unreadable ledger must propagate and stop the build, not
        // quietly reset the day's spend to zero. See the class comment on failing closed.
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<SpendDay>(json)
            ?? throw new InvalidOperationException($"Spend ledger at {path} deserialized to null.");
    }

    /// <summary>Cooldown has to survive the UTC date rolling over, or a build at 23:59 followed by
    /// one at 00:01 would skip it entirely. Looks back at yesterday's file for the last invocation
    /// when today's does not exist yet.</summary>
    private static async Task<DateTimeOffset?> LastInvocationAcrossDaysAsync(CancellationToken ct)
    {
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = PathFor(yesterday);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<SpendDay>(json)?.LastInvocationUtc;
    }

    private static string PathFor(string dateUtc) => Path.Combine(Workspace.LedgerDir, $"{dateUtc}.json");
}
