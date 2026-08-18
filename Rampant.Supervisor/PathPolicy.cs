namespace Rampant.Supervisor;

public sealed record PolicyVerdict(bool AutoDeploy, IReadOnlyList<string> CorePaths)
{
    /// <summary>Human-readable summary of what was touched, for the approval message. The owner is
    /// deciding, not reviewing - they get areas, not a diff. The full diff and the complete Claude
    /// Code transcript stay under /workspace/logs/extend_self/ for anyone with SSH.</summary>
    public string Describe() => CorePaths.Count == 0
        ? "tools only"
        : "core: " + string.Join(", ", CorePaths.Take(8)) + (CorePaths.Count > 8 ? $", +{CorePaths.Count - 8} more" : "");
}

/// <summary>
/// Decides whether a commit deploys on its own or waits for the owner. This governs *where code
/// lives, not what it does* - a file under Tools/ ships unreviewed and then runs inside the agent
/// process with its full ambient authority. This system bounds spend and protects the core; it says nothing
/// about what a tool is allowed to do, which is deliberate (PLAN.md -> "What a tool is allowed
/// to do - currently, everything").
///
/// The auto-deploy surface is an allowlist and everything else is held, so a path nobody thought
/// about fails towards asking rather than towards shipping.
/// </summary>
public static class PathPolicy
{
    /// <summary>Self-built tools. The entire point of the system - these are why the gate can be strict
    /// everywhere else without constantly blocking the agent from things it legitimately needs.</summary>
    private const string ToolsPrefix = "Tools/";

    /// <summary>The system prompt. Auto-deploys because purpose is allowed to drift (principle 1) -
    /// the agent may decide it is a monitor or a calculator rather than an assistant, and that is
    /// the experiment, not a failure. What it cannot rewrite is the machinery that makes it
    /// reachable.</summary>
    private const string SelfMd = "SELF.md";

    /// <summary>The project file. Auto-deploys, which looks like a loosening but isn't: a tool is
    /// already arbitrary code running in-process, so being able to add a NuGet package expands
    /// nothing that wasn't already reachable. Holding it would mean the gate fires on most real
    /// tools (anything needing an HTTP client, a parser, a database driver) - exactly the failure
    /// mode the plan warns about, where the agent is blocked from things it legitimately needs.
    /// A bad edit here breaks the build, which the build gate already catches.</summary>
    private const string AgentProject = "Rampant.Agent.csproj";

    public static PolicyVerdict Evaluate(IReadOnlyList<string> changedPaths)
    {
        var core = changedPaths.Where(p => !IsAutoDeployable(p)).ToList();
        return new PolicyVerdict(core.Count == 0, core);
    }

    private static bool IsAutoDeployable(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('.', '/');

        return normalized.StartsWith(ToolsPrefix, StringComparison.Ordinal)
            || normalized.Equals(SelfMd, StringComparison.Ordinal)
            || normalized.Equals(AgentProject, StringComparison.Ordinal);
    }
}
