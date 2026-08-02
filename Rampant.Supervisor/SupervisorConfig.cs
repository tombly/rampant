using System.Globalization;

namespace Rampant.Supervisor;

/// <summary>
/// Every knob the operator controls, read once at startup from the process environment - which
/// compose populates from .env on the host, outside the volume the agent can write. That placement
/// is the whole point: in V1 the spend cap and model choice lived in /workspace/agent, i.e. in the
/// directory whose entire purpose is to be rewritten, which made them defaults rather than bounds
/// (PLAN.md -> tenet 4). Here they are bounds.
///
/// Note <see cref="ClaudeModel"/> and the agent's OpenAI model are separate settings. V1 had one
/// RAMPANT_MODEL controlling both conversation and self-modification; V2 splits them because they
/// are different jobs with different price tags - cheap reflection every hour, expensive coding
/// rarely.
/// </summary>
public sealed record SupervisorConfig(
    IReadOnlySet<string> OwnerSignalIds,
    string? AnthropicApiKey,
    string ClaudeModel,
    int MaxTurns,
    decimal MaxBudgetPerInvocationUsd,
    decimal DailyBudgetUsd,
    TimeSpan BuildCooldown,
    TimeSpan WakeInterval,
    TimeZoneInfo? WakeTimeZone,
    int WakeStartHour,
    int WakeEndHour,
    TimeSpan ClaudeCodeTimeout)
{
    public const string OwnerIdEnvVar = "RAMPANT_OWNER_SIGNAL_ID";

    /// <summary>The identifier proactive supervisor messages (approval prompts, build-failure
    /// alerts) go to, and the fallback recipient for an agent reply that names none. First entry
    /// wins: the allowlist may hold the same person's phone number *and* UUID, and sending to both
    /// would deliver everything twice, in the two separate threads Signal already shows for one
    /// account.</summary>
    public string? PrimaryOwnerId => OwnerSignalIds.FirstOrDefault();

    public static SupervisorConfig FromEnvironment() => new(
        OwnerSignalIds: LoadOwnerIds(),
        AnthropicApiKey: Get("ANTHROPIC_API_KEY"),
        ClaudeModel: Get("RAMPANT_CLAUDE_MODEL") ?? "claude-sonnet-5",
        MaxTurns: GetInt("RAMPANT_MAX_TURNS", 40),
        MaxBudgetPerInvocationUsd: GetDecimal("RAMPANT_MAX_BUDGET_USD", 1.00m),
        DailyBudgetUsd: GetDecimal("RAMPANT_DAILY_BUDGET_USD", 5.00m),
        BuildCooldown: TimeSpan.FromMinutes(GetInt("RAMPANT_BUILD_COOLDOWN_MINUTES", 45)),
        WakeInterval: TimeSpan.FromMinutes(GetInt("RAMPANT_WAKE_INTERVAL_MINUTES", 60)),
        WakeTimeZone: LoadTimeZone(),
        WakeStartHour: GetInt("RAMPANT_WAKE_START_HOUR", 6),
        WakeEndHour: GetInt("RAMPANT_WAKE_END_HOUR", 23),
        ClaudeCodeTimeout: TimeSpan.FromMinutes(GetInt("RAMPANT_CLAUDE_TIMEOUT_MINUTES", 10)));

    /// <summary>The timezone the wake window is expressed in. An IANA id, resolved through the
    /// system tzdata so daylight saving is handled rather than being frozen into an offset - a
    /// hardcoded -7 would silently shift the whole window by an hour twice a year.
    ///
    /// Returns null if the id cannot be resolved, which the ticker treats as "no window" and wakes
    /// around the clock. That is the safer of the two failure directions: a misresolved zone would
    /// otherwise either shift the quiet hours by several hours or, worse, silence the agent
    /// permanently with nothing to indicate why.</summary>
    private static TimeZoneInfo? LoadTimeZone()
    {
        var id = Get("RAMPANT_WAKE_TIMEZONE") ?? "America/Los_Angeles";
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Who is allowed to be treated as the owner over Signal. The signal-cli sidecar is a
    /// single linked account, but that account still receives DMs from any contact and messages in
    /// any group it belongs to - without this check, anyone who texts the number gets full owner
    /// trust. Multiple entries are for the *same* person's account presenting differently across
    /// messages (Signal's phone-number-privacy feature means the envelope often carries only a
    /// UUID), not for trusting multiple people. Empty means fail closed - nobody is trusted,
    /// rather than everybody.</summary>
    private static HashSet<string> LoadOwnerIds()
    {
        var raw = Get(OwnerIdEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int GetInt(string name, int fallback)
        => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static decimal GetDecimal(string name, decimal fallback)
        => decimal.TryParse(Get(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
