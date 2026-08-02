namespace Rampant.Supervisor;

/// <summary>
/// Every path in the system, in one place, because the ownership of these directories *is* the
/// security model (PLAN-V2.md -> "Filesystem layout"). Splitting them across the files that happen
/// to use them makes it impossible to check the layout against the plan at a glance.
///
/// The rule: agent-writable directories are the ones under <see cref="Inbox"/>,
/// <see cref="Outbox"/>, <see cref="Logs"/>, <see cref="Data"/> and <see cref="RequestsIn"/>.
/// Everything else here is root-owned. The agent can read almost all of it - transparency about
/// what it is costs nothing once the boundary is ownership rather than obscurity.
/// </summary>
public static class Workspace
{
    public const string Root = "/workspace";

    // --- Agent-writable -----------------------------------------------------------------------

    /// <summary>Where the supervisor delivers everything the agent should react to: Signal
    /// messages, hourly wake ticks, and the outcome of its own capability requests. One JSON
    /// envelope per file; see the agent's InboxEnvelope for the shape.</summary>
    public const string Inbox = "/workspace/inbox";

    public const string InboxProcessed = "/workspace/inbox/.processed";

    /// <summary>The agent's only way to speak. It writes an envelope here; the supervisor sends
    /// it over Signal and moves it to <see cref="OutboxSent"/>. Deliberately a file drop rather
    /// than a socket the agent owns - it means no self-edit and no self-built tool can cost the
    /// agent its ability to answer when the owner calls.</summary>
    public const string Outbox = "/workspace/outbox";

    public const string OutboxSent = "/workspace/outbox/.sent";

    public const string Logs = "/workspace/logs";

    /// <summary>Empty at genesis. The floor that makes self-built tools possible: a tool that
    /// needs to persist anything has somewhere to put it, so it doesn't deploy cleanly and then
    /// fail at runtime in a way the agent has no way to observe.</summary>
    public const string Data = "/workspace/data";

    /// <summary>Capability requests the agent files. Agent-owned, so the supervisor moves each one
    /// out to <see cref="StateProcessing"/> the moment it picks it up - a request must not be
    /// mutable while it is being acted on.</summary>
    public const string RequestsIn = "/workspace/requests/in";

    // --- Root-owned, agent-readable ------------------------------------------------------------

    /// <summary>Request results and status.json. Read-only to the agent: it can reason about its
    /// own budget without being able to alter it.</summary>
    public const string RequestsOut = "/workspace/requests/out";

    public const string StatusFile = "/workspace/requests/out/status.json";

    /// <summary>The agent's own source. Readable on purpose; writable only by the supervisor's
    /// Claude Code invocation.</summary>
    public const string AgentRepo = "/workspace/agent";

    public const string BuildStaging = "/workspace/build/staging";
    public const string BuildCurrent = "/workspace/build/current";
    public const string BuildPrevious = "/workspace/build/previous";

    // --- Root-owned bookkeeping ----------------------------------------------------------------

    public const string State = "/workspace/state";
    public const string StateFile = "/workspace/state/supervisor.json";
    public const string LedgerDir = "/workspace/state/ledger";

    /// <summary>Where a request lives between pickup and completion.</summary>
    public const string StateProcessing = "/workspace/state/processing";

    // --- Logs (root-writes, agent-readable) -----------------------------------------------------

    public const string BuildFailureLogs = "/workspace/logs/build-failures";

    /// <summary>Full prompt and complete Claude Code transcript for every invocation. SSH-readable
    /// only in practice: the agent has no file-reading tool at genesis, and if it builds one, this
    /// is a log of what was done on its behalf rather than a channel it can talk through.</summary>
    public const string ExtendSelfLogs = "/workspace/logs/extend_self";

    /// <summary>Inbound Signal traffic that failed the allowlist. Rejected messages are never
    /// silently dropped - a real misconfiguration should be discoverable rather than presenting
    /// as "it just doesn't reply to anyone."</summary>
    public const string RejectedSenderLogs = "/workspace/logs/unverified-signal-messages";
}
