using System.Xml.Linq;

namespace Rampant.Supervisor;

/// <summary>
/// Rejects floating package versions in the agent's project file.
///
/// <see cref="PathPolicy"/> decides what deploys by looking at which files changed. That works
/// because the diff describes the change - but a floating version breaks the assumption underneath
/// it. `Version="*"` resolves at restore time, so a rebuild triggered by some entirely unrelated
/// request can pull different package code, with nothing in the diff to show for it and no gate
/// evaluating it. The agent that starts up is not the one that shut down, and git no longer
/// answers "what is running?"
///
/// That matters more here than in an ordinary project, because the audit trail is the point:
/// /workspace/logs/extend_self/ records what was asked for and what Claude Code said it did, and
/// the repo records the source. If the binary also depends on the state of nuget.org at a moment
/// nobody wrote down, all of that stops adding up.
///
/// Enforced here, in supervisor code baked into a root-owned /opt, rather than only in the prompt
/// that asks Claude Code to pin - a rule expressed inside /workspace/agent is a default, not a
/// bound (PLAN.md -> principle 4).
/// </summary>
public static class PackagePinPolicy
{
    public const string ProjectFileName = "Rampant.Agent.csproj";

    public static bool TouchesProject(IReadOnlyList<string> changedPaths)
        => changedPaths.Any(p => p.Replace('\\', '/').TrimStart('.', '/')
            .Equals(ProjectFileName, StringComparison.Ordinal));

    /// <summary>Returns "PackageId: version" for every reference that isn't pinned to a single
    /// version, or an empty list if the file is fine. A malformed project file returns empty and
    /// is left to the build gate, which reports it far better than this could.</summary>
    public static IReadOnlyList<string> FindUnpinned(string projectPath)
    {
        if (!File.Exists(projectPath))
            return [];

        XDocument doc;
        try
        {
            doc = XDocument.Load(projectPath);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (
                Id: e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "(unnamed)",
                Version: e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value))
            .Where(r => !IsPinned(r.Version))
            .Select(r => $"{r.Id}: {(string.IsNullOrWhiteSpace(r.Version) ? "(no version)" : r.Version)}")
            .ToList();
    }

    /// <summary>Pinned means one version, not a range. `1.2.3` and `[1.2.3]` both qualify - the
    /// bracket form is NuGet's strict-exact syntax and the bare form resolves to that version when
    /// it exists, which is the case in practice. Rejected: anything floating (`*`, `1.*`) and any
    /// two-bound range (`[1.0,2.0)`), plus a missing version, which is the container-only NU1015
    /// this repo already has scar tissue about.</summary>
    private static bool IsPinned(string? version)
        => !string.IsNullOrWhiteSpace(version)
            && !version.Contains('*')
            && !version.Contains(',');
}
