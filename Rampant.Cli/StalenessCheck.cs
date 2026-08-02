using System.Diagnostics;

namespace Rampant.Cli;

/// <summary>
/// Warns when the installed binary is older than the source it was built from.
///
/// This tool is a self-contained ARM64 binary living at /usr/local/bin/rampant on the Pi host, not
/// in a container. `docker compose up --build` does not rebuild it and `git pull` does not replace
/// it, so a change to Rampant.Cli/ leaves a stale binary running with nothing to say so.
///
/// That is not theoretical. V2 changed the workspace layout and every log format, and the stale
/// binary kept running against V1's - so `rampant log` printed nothing at all and exited 0. A
/// wrong answer that looks like "there is nothing to report" is worse than a crash, and it went
/// unnoticed until somebody thought to ask whether the tool still worked.
///
/// Note the bootstrapping limit: a binary predating this check cannot warn about itself. It only
/// protects against the *next* drift, which is the one worth protecting against.
/// </summary>
public static class StalenessCheck
{
    public static void WarnIfStale(string cwd)
    {
        try
        {
            if (Environment.ProcessPath is not { } binaryPath || !File.Exists(binaryPath))
                return;

            if (LastCommitTouchingCli(cwd) is not { } sourceChanged)
                return;

            var installed = File.GetLastWriteTimeUtc(binaryPath);
            if (sourceChanged <= installed)
                return;

            // stderr, so it never pollutes output being piped or grepped.
            Console.Error.WriteLine(
                $"warning: this binary was installed {installed:yyyy-MM-dd HH:mm} UTC but Rampant.Cli/ "
                + $"changed {sourceChanged:yyyy-MM-dd HH:mm} UTC - what follows may be wrong or empty. "
                + "Re-publish with scripts/publish-cli.sh.");
        }
        catch
        {
            // Never let a diagnostic break the command it is diagnosing. Running outside the repo,
            // without git, or from a tarball are all normal and simply mean no check is possible.
        }
    }

    private static DateTime? LastCommitTouchingCli(string cwd)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "log", "-1", "--format=%cI", "--", "Rampant.Cli" })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(5_000);

        if (process.ExitCode != 0 || output.Length == 0)
            return null;

        return DateTimeOffset.TryParse(output, out var when) ? when.UtcDateTime : null;
    }
}
