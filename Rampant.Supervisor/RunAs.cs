using System.Diagnostics;

namespace Rampant.Supervisor;

/// <summary>
/// Runs a subprocess as somebody other than the supervisor. Three uids share this container and
/// the split between them is the security model, so every privilege drop goes through here rather
/// than being open-coded at each call site.
///
/// <list type="bullet">
/// <item>root (pid 1) - the supervisor. Owns the Signal socket, the spend ledger and the gates,
/// and owns nothing inside /workspace.</item>
/// <item><see cref="Builder"/> - Claude Code, git, and dotnet build. Owns /workspace/agent,
/// /workspace/build and the NuGet cache.</item>
/// <item><see cref="Agent"/> - the agent process. Owns inbox, outbox, data and logs.</item>
/// </list>
///
/// The builder uid exists for two reasons that turned out to be the same reason. Claude Code
/// refuses --dangerously-skip-permissions outright when it detects root, which is how this was
/// found; and running it as root would have let it rewrite /opt/supervisor, quietly voiding the
/// first of the four boundaries in PLAN.md. Under a uid of its own it can write the agent's
/// source and nothing else.
///
/// setpriv rather than su: it is not setuid (so no-new-privileges does not block it) and it execs
/// in place rather than forking, so the pid returned is the real one - which matters for anything
/// we later need to signal.
/// </summary>
public static class RunAs
{
    public const int Builder = 1656;
    public const int Agent = 1655;

    /// <summary>A minimal environment for build tooling. Explicitly excludes ANTHROPIC_API_KEY:
    /// git and dotnet have no use for it, and `dotnet build` in particular executes MSBuild targets
    /// that arrive inside NuGet packages, which is not something to hand a credential to.</summary>
    public static void ApplyBuildEnvironment(ProcessStartInfo psi)
    {
        psi.EnvironmentVariables.Clear();
        psi.EnvironmentVariables["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        psi.EnvironmentVariables["HOME"] = "/home/builder";
        psi.EnvironmentVariables["NUGET_PACKAGES"] = "/workspace/.nuget/packages";
        psi.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
        psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    }

    public static ProcessStartInfo Command(int uid, string fileName, string? workingDirectory, params IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("setpriv") { UseShellExecute = false };

        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        psi.ArgumentList.Add($"--reuid={uid}");
        psi.ArgumentList.Add($"--regid={uid}");
        psi.ArgumentList.Add("--clear-groups");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(fileName);

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }

    /// <summary>Hands ownership of a freshly created tree to another uid. Needed because the
    /// supervisor creates some directories as root before the user that has to write them exists
    /// in the picture - a file created by root inside a builder-owned directory is still
    /// root-owned, and the builder cannot touch it.</summary>
    public static void Chown(int uid, string path)
    {
        using var chown = Process.Start(new ProcessStartInfo("chown")
        {
            ArgumentList = { "-R", $"{uid}:{uid}", path },
            UseShellExecute = false,
        });

        chown?.WaitForExit(30_000);
    }
}
