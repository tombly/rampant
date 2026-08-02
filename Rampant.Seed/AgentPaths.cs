namespace Rampant.Agent;

/// <summary>
/// Every path this process touches. Worth knowing before writing a tool: almost all of the
/// container is readable and almost none of it is writable. This process runs as `agentrunner`;
/// its own source, its compiled binary, and the supervisor that builds both are owned by root.
/// That isn't a rule to follow, it's an ownership boundary - attempting to write outside the three
/// writable directories below throws.
/// </summary>
public static class AgentPaths
{
    /// <summary>Things to react to, one JSON envelope per file, written only by the supervisor:
    /// messages from the owner, hourly wake ticks, and the outcome of capability requests.</summary>
    public const string Inbox = "/workspace/inbox";

    public const string InboxProcessed = "/workspace/inbox/.processed";

    /// <summary>WRITABLE. The only way to say anything. The supervisor picks entries up and sends
    /// them over Signal - this process has no network access to Signal itself.</summary>
    public const string Outbox = "/workspace/outbox";

    /// <summary>WRITABLE. Operational logs, for whoever is looking after this system over SSH.</summary>
    public const string Logs = "/workspace/logs";

    /// <summary>WRITABLE. Empty at genesis. Where a self-built tool keeps anything that has to
    /// survive a restart - and restarts are frequent, since every deployment is one.</summary>
    public const string Data = "/workspace/data";

    /// <summary>WRITE-ONLY in practice: capability requests are dropped here and the supervisor
    /// moves them out immediately.</summary>
    public const string RequestsIn = "/workspace/requests/in";

    /// <summary>Read-only. Budget, cooldown, and any outstanding approval - the supervisor's
    /// account of what this process can currently afford to ask for.</summary>
    public const string Status = "/workspace/requests/out/status.json";

    /// <summary>Read-only. This process's own source. Reading it is fine and encouraged; writing
    /// to it is impossible.</summary>
    public const string Source = "/workspace/agent";

    public const string SelfMd = "/workspace/agent/SELF.md";

    public const string HeartbeatLog = "/workspace/logs/heartbeat.log";

    public const string HistoryLog = "/workspace/logs/history.log";
}
