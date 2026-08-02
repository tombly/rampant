using System.Diagnostics;

namespace Rampant.Supervisor;

public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
    public string Combined => Stdout + Stderr;
}

/// <summary>
/// Thin git wrapper for the agent repo. Only the supervisor calls this - under V2 the agent has no
/// write access to /workspace/agent at all, so unlike V1 there is exactly one process that ever
/// commits, and no need to reason about concurrent writers.
/// </summary>
public static class Git
{
    public static async Task<GitResult> RunAsync(string workingDirectory, CancellationToken ct, params string[] args)
        => await RunWithStdinAsync(workingDirectory, stdin: null, ct, args);

    /// <summary>Commit messages go in over stdin (`-F -`), never as a literal argument - the text
    /// can originate from a model.</summary>
    public static async Task<GitResult> RunWithStdinAsync(string workingDirectory, string? stdin, CancellationToken ct, params string[] args)
    {
        // As the builder uid, which owns the repo. Running git as root would create root-owned
        // objects inside a builder-owned tree, and the next Claude Code invocation could not write
        // them.
        var psi = RunAs.Command(RunAs.Builder, "git", workingDirectory, args);
        RunAs.ApplyBuildEnvironment(psi);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = stdin is not null;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start git {string.Join(' ', args)}");

        // Drain both streams concurrently before awaiting exit - reading one at a time risks a
        // pipe-buffer deadlock when git writes enough to the un-drained stream.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(ct);
        return new GitResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    public static async Task<string?> HeadShaAsync(string workingDirectory, CancellationToken ct)
    {
        var result = await RunAsync(workingDirectory, ct, "rev-parse", "HEAD");
        return result.Success ? result.Stdout.Trim() : null;
    }

    /// <summary>Repo-relative paths touched between two commits, in either direction. Used to
    /// classify a Claude Code run against the path policy.</summary>
    public static async Task<IReadOnlyList<string>> ChangedPathsAsync(string workingDirectory, string fromSha, string toSha, CancellationToken ct)
    {
        var result = await RunAsync(workingDirectory, ct, "diff", "--name-only", $"{fromSha}..{toSha}");
        if (!result.Success)
            return [];

        return result.Stdout
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    public static async Task<bool> IsDirtyAsync(string workingDirectory, CancellationToken ct)
    {
        var result = await RunAsync(workingDirectory, ct, "status", "--porcelain");
        return result.Success && !string.IsNullOrWhiteSpace(result.Stdout);
    }

    /// <summary>Discards everything back to a known commit. Used when the owner denies a held
    /// change, and when a build failure leaves a commit that will never deploy.</summary>
    public static Task<GitResult> ResetHardAsync(string workingDirectory, string sha, CancellationToken ct)
        => RunAsync(workingDirectory, ct, "reset", "--hard", sha);
}
