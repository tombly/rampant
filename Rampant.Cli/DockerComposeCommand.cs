using System.Diagnostics;

namespace Rampant.Cli;

/// <summary>Thin wrapper around `docker compose`, run from wherever docker-compose.yml lives (the
/// same directory this CLI expects to be run from - see Program.cs). Output streams directly to
/// the console rather than being captured, since this is meant to be watched live the same way
/// running `docker compose` by hand would look - `start`/`stop` are routine lifecycle control
/// only (no `--build`); deploying new code is still the existing wipe-and-reseed procedure.
/// </summary>
public static class DockerComposeCommand
{
    public static int Run(string cwd, params string[] composeArgs)
    {
        if (!File.Exists(Path.Combine(cwd, "docker-compose.yml")))
        {
            Console.Error.WriteLine($"No docker-compose.yml found under {cwd} - run this from ~/rampant.");
            return 1;
        }

        var psi = new ProcessStartInfo("docker") { WorkingDirectory = cwd, UseShellExecute = false };
        psi.ArgumentList.Add("compose");
        foreach (var arg in composeArgs)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker.");

        process.WaitForExit();
        return process.ExitCode;
    }
}
