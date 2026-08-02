using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampant.Supervisor;

/// <summary>What the agent is allowed to know about its own budget. Written by the supervisor to
/// a directory the agent can read but not write, so it can reason about whether a request is worth
/// filing without being able to change the answer. The conditions here are advisory to the agent
/// and enforced by the supervisor: it decides what to ask for, it never decides what it may
/// spend.</summary>
public sealed record AgentStatus(
    DateTimeOffset GeneratedUtc,
    decimal DailyBudgetUsd,
    decimal SpentTodayUsd,
    decimal RemainingTodayUsd,
    int BuildsToday,
    int CooldownMinutes,
    int CooldownRemainingMinutes,
    bool CanRequestNow,
    string? BlockedReason,
    DateTimeOffset? LastBuildUtc,
    double? HoursSinceLastBuild,
    string? PendingApprovalToken,
    string? PendingApprovalSummary);

public sealed class StatusWriter(SupervisorConfig _config, SpendLedger _ledger, ApprovalQueue _approvals)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task WriteAsync(CancellationToken ct)
    {
        var day = await _ledger.LoadTodayAsync(ct);
        var verdict = await _ledger.EvaluateAsync(ct);
        var pending = await _approvals.GetAsync(ct);

        var status = new AgentStatus(
            GeneratedUtc: DateTimeOffset.UtcNow,
            DailyBudgetUsd: _config.DailyBudgetUsd,
            SpentTodayUsd: decimal.Round(day.SpentUsd, 4),
            RemainingTodayUsd: decimal.Round(Math.Max(0m, _config.DailyBudgetUsd - day.SpentUsd), 4),
            BuildsToday: day.Invocations,
            CooldownMinutes: (int)_config.BuildCooldown.TotalMinutes,
            CooldownRemainingMinutes: (int)Math.Ceiling(verdict.CooldownRemaining.TotalMinutes),
            CanRequestNow: verdict.Allowed && pending is null,
            BlockedReason: pending is not null
                ? "A previous change is waiting on the owner's approval."
                : verdict.Reason,
            LastBuildUtc: day.LastInvocationUtc,
            HoursSinceLastBuild: day.LastInvocationUtc is { } last
                ? Math.Round((DateTimeOffset.UtcNow - last).TotalHours, 2)
                : null,
            PendingApprovalToken: pending?.Token,
            PendingApprovalSummary: pending is null ? null : $"{pending.Request.Subject} ({pending.TouchedSummary})");

        Directory.CreateDirectory(Workspace.RequestsOut);
        var tmp = Workspace.StatusFile + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(status, JsonOptions), ct);
        File.Move(tmp, Workspace.StatusFile, overwrite: true);
    }
}
