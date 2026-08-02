using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

/// <summary>
/// The agent's clock, kept outside the agent. A wake envelope lands in the inbox by exactly the
/// same mechanism a Signal message does - the agent cannot disable its own wakeups, make them more
/// frequent, or wake itself during the quiet hours. It can rewrite SELF.md to ignore them, which is
/// permitted purpose drift, but the tick still arrives.
///
/// Reflection is cheap - one LLM call per tick. Building is expensive and sits behind the spend
/// ledger. That asymmetry is why the tick is allowed to be frequent while a build is not.
///
/// The quiet hours gate *self-initiated* activity only. A message from the owner at 3am is still
/// received, answered, and can still trigger a build - the window is about the agent choosing to
/// act unprompted, not about the system being asleep.
/// </summary>
public sealed class WakeTicker(SupervisorConfig _config, ILogger<WakeTicker> _logger)
{
    /// <summary>States the real cadence and the real window rather than assuming the defaults.
    /// Both are configurable, and the agent uses them to judge whether silence is the right answer
    /// and whether "I'll follow up later" means in fifteen minutes or tomorrow morning. A hardcoded
    /// "hourly" here would quietly misinform it the moment anyone changed a setting.
    ///
    /// The supervisor is the only party that knows these values, which is why they go in the
    /// envelope: neither SELF.md nor the agent's own prompt asserts a period, because neither can
    /// verify one.</summary>
    private string BuildText() => $"""
        This is a scheduled wake tick - you get one every {Describe(_config.WakeInterval)}{WindowClause()}.
        Nobody sent it and nobody is waiting on a reply.

        Consider whether anything actually needs doing: something the owner said you would follow
        up on, something you noticed you could not do, a capability worth having. Read
        /workspace/requests/out/status.json if you want to know what you can currently afford.

        Most of the time the right answer is to do nothing at all, and staying quiet costs the
        owner nothing. You are woken often, so most ticks should pass in silence - only speak if it
        is genuinely worth interrupting someone for.
        """;

    private string WindowClause()
        => _config.WakeTimeZone is { } tz
            ? $", between {_config.WakeStartHour:00}:00 and {_config.WakeEndHour:00}:00 {tz.Id} (you are not woken outside those hours, though the owner can still reach you at any time)"
            : string.Empty;

    private static string Describe(TimeSpan interval)
    {
        if (interval.TotalMinutes < 60)
            return $"{interval.TotalMinutes:0} minutes";

        if (Math.Abs(interval.TotalHours - 1) < 0.01)
            return "hour";

        return interval.TotalMinutes % 60 == 0
            ? $"{interval.TotalHours:0} hours"
            : $"{interval.TotalMinutes:0} minutes";
    }

    /// <summary>Returns the (possibly updated) state. Persisting the last tick is what stops a
    /// restart from either firing immediately or losing an interval - and the supervisor restarts
    /// often, since every deployment restarts the agent.</summary>
    public async Task<SupervisorState> TickIfDueAsync(SupervisorState state, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Checked before the interval, and deliberately without touching LastWakeTickUtc. Leaving
        // it alone means that when the window opens the elapsed time is already well past the
        // interval, so the first tick of the day fires promptly at the start hour rather than one
        // interval after it. It also cannot produce a catch-up burst: this method emits at most one
        // tick per call.
        if (!IsWithinWakeWindow(now))
            return state;

        if (state.LastWakeTickUtc is { } last && now - last < _config.WakeInterval)
            return state;

        // First boot: start the clock rather than firing immediately. A tick seconds after
        // `docker compose up` is noise, not initiative.
        if (state.LastWakeTickUtc is null)
            return state with { LastWakeTickUtc = now };

        _logger.LogInformation("Wake tick (every {Interval})", _config.WakeInterval);
        await new InboxEnvelope(InboxKind.Wake, now, Sender: null, BuildText()).WriteAsync(ct);

        return state with { LastWakeTickUtc = now };
    }

    /// <summary>Quiet hours, in the owner's local time rather than UTC - converted through tzdata
    /// so the window holds across daylight saving instead of drifting by an hour twice a year.
    ///
    /// An unresolvable timezone disables the window rather than guessing at one. Waking at an odd
    /// hour is a nuisance; silently never waking again is an agent that has stopped working with
    /// nothing to say why.</summary>
    private bool IsWithinWakeWindow(DateTimeOffset utcNow)
    {
        if (_config.WakeTimeZone is not { } tz)
            return true;

        var localHour = TimeZoneInfo.ConvertTime(utcNow, tz).Hour;

        // A window that wraps past midnight (e.g. 22 to 6) is read as such rather than as an empty
        // range, so the setting behaves sensibly for anyone who keeps different hours.
        return _config.WakeStartHour <= _config.WakeEndHour
            ? localHour >= _config.WakeStartHour && localHour < _config.WakeEndHour
            : localHour >= _config.WakeStartHour || localHour < _config.WakeEndHour;
    }
}
