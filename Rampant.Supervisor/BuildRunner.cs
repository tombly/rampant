using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

public sealed record BuildResult(bool Success, string Output, string ErrorOutput);

public interface IBuildRunner
{
    Task<BuildResult> BuildAsync(string projectDirectory, string outputPath, CancellationToken ct);
}

/// <summary>dotnet build subprocess wrapper - the free correctness gate: a commit that doesn't
/// compile simply never takes effect, with no human review required to get that protection.</summary>
public sealed class BuildRunner(ILogger<BuildRunner> _logger) : IBuildRunner
{
    public async Task<BuildResult> BuildAsync(string projectDirectory, string outputPath, CancellationToken ct)
    {
        _logger.LogInformation("Building {ProjectDirectory} -> {OutputPath}", projectDirectory, outputPath);

        // As the builder uid, which owns the source, the output directory and the NuGet cache.
        // Also on a scrubbed environment: `dotnet build` executes MSBuild targets that arrive
        // inside NuGet packages, and there is no reason for those to see an API key.
        var psi = RunAs.Command(RunAs.Builder, "dotnet", workingDirectory: null,
            "build", projectDirectory, "-o", outputPath);
        RunAs.ApplyBuildEnvironment(psi);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet build process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var success = process.ExitCode == 0;
        if (!success)
            _logger.LogWarning("Build failed (exit {ExitCode}):\n{Stderr}", process.ExitCode, stderr);

        return new BuildResult(success, stdout, stderr);
    }
}
