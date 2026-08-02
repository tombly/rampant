using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

/// <summary>
/// The capability round trip, end to end:
///
///   agent writes  /workspace/requests/in/&lt;id&gt;.json   (a description in English, never a diff)
///   supervisor    moves it out of the agent's reach
///                 -> cooldown + budget; refuses with a reason if either fails
///                 -> invokes Claude Code (its user, its key, its argv, its budget flags)
///                 -> diffs the commit against the path policy
///                      tools / SELF.md / csproj -> build -> promote -> restart
///                      anything else            -> hold, ask the owner, wait
///                 -> writes the outcome to requests/out AND back into the agent's inbox
///
/// The process that files a request never sees the result: deploying restarts it. That is
/// structural, not a wrinkle to fix - so the outcome envelope carries the original conversation
/// back across the restart boundary, and the *successor* is what finally answers the owner. At
/// genesis, with no memory, this pipeline is the agent's only continuity.
/// </summary>
public sealed class RequestPipeline(
    SpendLedger _ledger,
    IClaudeCodeRunner _claude,
    ApprovalQueue _approvals,
    Deployer _deployer,
    SignalGateway _signal,
    ILogger<RequestPipeline> _logger)
{
    /// <summary>One unit of work per call, so the caller's loop stays sequential - never two
    /// Claude Code runs, a build, and a restart interleaved over the same repo. Returns the commit
    /// deployed by this call, or null if nothing was, so the caller can record it: without that,
    /// every container restart after a self-modification rebuilds a commit that is already
    /// running.</summary>
    public async Task<string?> RunOnceAsync(CancellationToken ct)
    {
        var pending = await _approvals.GetAsync(ct);

        if (pending is { Decision: { } decision })
            return await ApplyDecisionAsync(pending, decision, ct);

        // While an approval is outstanding the repo HEAD is ahead of what is deployed. Building
        // anything now would stack unapproved code underneath approved code, so nothing else runs
        // until the owner answers. New requests are refused with that as the reason.
        if (pending is not null)
        {
            await RefuseWhileBlockedAsync(
                $"A previous change is waiting on the owner's approval (reply approve {pending.Token} or deny {pending.Token}). Nothing else can be built until then.",
                ct);
            return null;
        }

        var request = await TakeNextRequestAsync(ct);
        return request is null ? null : await ProcessAsync(request, ct);
    }

    private async Task<string?> ProcessAsync(CapabilityRequest request, CancellationToken ct)
    {
        var verdict = await _ledger.EvaluateAsync(ct);
        if (!verdict.Allowed)
        {
            _logger.LogInformation("Refusing request {Id}: {Reason}", request.Id, verdict.Reason);
            await CompleteAsync(request, RequestStatus.Refused, verdict.Reason!, 0m, [], ct);
            return null;
        }

        var preSha = await Git.HeadShaAsync(Workspace.AgentRepo, ct);
        if (preSha is null)
        {
            await CompleteAsync(request, RequestStatus.Failed, "The agent repository is not in a usable state.", 0m, [], ct);
            return null;
        }

        // CancellationToken.None from here on. Claude Code costs real money the moment it starts,
        // and a shutdown racing a half-finished self-modification is exactly the failure V1 hit:
        // the successor reprocessed the same work from scratch with no idea what its predecessor
        // had already done. Only the decision to start *another* unit of work checks shutdown.
        var result = await _claude.RunAsync(request, CancellationToken.None);
        await _ledger.RecordAsync(result.CostUsd, DateTimeOffset.UtcNow);

        await CommitLeftoversAsync(request, CancellationToken.None);
        var postSha = await Git.HeadShaAsync(Workspace.AgentRepo, CancellationToken.None);

        if (postSha is null || postSha == preSha)
        {
            var detail = result.Success
                ? "Claude Code reported success but changed nothing, so there is no new capability."
                : Summarize(result.Summary, "Claude Code could not complete the request.");

            await CompleteAsync(request, RequestStatus.Failed, detail, result.CostUsd, [], CancellationToken.None);
            return null;
        }

        var changed = await Git.ChangedPathsAsync(Workspace.AgentRepo, preSha, postSha, CancellationToken.None);

        // Before the deploy/hold decision, not after: an unpinned version is wrong whichever
        // branch this would have taken, and holding one for approval would mean the owner
        // approving something that cannot be reproduced.
        if (await RejectUnpinnedPackagesAsync(request, changed, preSha, result.CostUsd, CancellationToken.None))
            return null;

        var policy = PathPolicy.Evaluate(changed);

        if (!policy.AutoDeploy)
        {
            await HoldForApprovalAsync(request, result, policy, changed, preSha, CancellationToken.None);
            return null;
        }

        return await BuildAndDeployAsync(request, result.Summary, result.CostUsd, changed, preSha, CancellationToken.None);
    }

    /// <summary>Rolls the commit back if it left a package version floating, and tells the agent
    /// how to get it right next time. A mechanical mistake with an obvious fix should not cost the
    /// owner a decision, so this fails the request rather than queueing an approval.</summary>
    private async Task<bool> RejectUnpinnedPackagesAsync(
        CapabilityRequest request,
        IReadOnlyList<string> changed,
        string preSha,
        decimal costUsd,
        CancellationToken ct)
    {
        if (!PackagePinPolicy.TouchesProject(changed))
            return false;

        var unpinned = PackagePinPolicy.FindUnpinned(Path.Combine(Workspace.AgentRepo, PackagePinPolicy.ProjectFileName));
        if (unpinned.Count == 0)
            return false;

        _logger.LogWarning(
            "Request {Id} left package versions unpinned ({Packages}); rolling back to {Sha}",
            request.Id, string.Join(", ", unpinned), preSha[..7]);

        await Git.ResetHardAsync(Workspace.AgentRepo, preSha, ct);

        await CompleteAsync(
            request,
            RequestStatus.Failed,
            $"""
            This was rolled back because a package version was left floating: {string.Join(", ", unpinned)}.

            A version like "*" is resolved when the project is built rather than being fixed by the
            code, so the next build - triggered by something else entirely - could quietly pull
            different code, and nobody would be able to say what you are actually running.

            Nothing was deployed. Ask again, and say explicitly that every package reference needs
            one exact version.
            """,
            costUsd,
            changed,
            ct);

        return true;
    }

    private async Task<string?> BuildAndDeployAsync(
        CapabilityRequest request,
        string summary,
        decimal costUsd,
        IReadOnlyList<string> changed,
        string preSha,
        CancellationToken ct)
    {
        var build = await _deployer.BuildAsync(ct);
        if (!build.Success)
        {
            await _deployer.LogBuildFailureAsync(build, request.Id, ct);
            await Git.ResetHardAsync(Workspace.AgentRepo, preSha, ct);
            _logger.LogWarning("Build failed for request {Id}; rolled back to {Sha}", request.Id, preSha[..7]);

            await CompleteAsync(
                request,
                RequestStatus.BuildFailed,
                "The code that was written does not compile, so it was rolled back. The previous version is still running.",
                costUsd,
                changed,
                ct);
            return null;
        }

        // Stop, then write the outcome, then start. Order matters: the outcome envelope has to
        // land in the inbox while nothing is reading it, or the outgoing process picks it up and
        // the successor - the one that actually has the new capability - never sees it.
        await _deployer.StopAgentAsync(ct);
        _deployer.Promote();
        await CompleteAsync(request, RequestStatus.Deployed, Summarize(summary, "The capability was built and deployed."), costUsd, changed, ct);
        _deployer.StartAgent();

        return await Git.HeadShaAsync(Workspace.AgentRepo, ct);
    }

    private async Task HoldForApprovalAsync(
        CapabilityRequest request,
        ClaudeCodeResult result,
        PolicyVerdict policy,
        IReadOnlyList<string> changed,
        string preSha,
        CancellationToken ct)
    {
        var token = ApprovalQueue.NewToken();
        var touched = policy.Describe();

        await _approvals.RaiseAsync(
            new PendingApproval(token, request, result.Summary, touched, changed, preSha, result.CostUsd, DateTimeOffset.UtcNow),
            ct);

        // High-level, not a diff. The owner is making a decision, not conducting a review - the
        // full diff and the complete transcript stay under /workspace/logs/extend_self/.
        var message = $"""
            Rampant wants to change its own core and needs your OK.

            It asked for: {request.Capability}
            {Truncate(request.Description, 400)}

            Claude Code says it did:
            {Truncate(Summarize(result.Summary, "(no summary given)"), 600)}

            Touched — {touched}

            Reply "approve {token}" to deploy it, or "deny {token}" to throw it away.
            Nothing else gets built until you answer.
            """;

        await _signal.NotifyOwnerAsync(message, ct);

        await CompleteAsync(
            request,
            RequestStatus.AwaitingApproval,
            $"This change touches core files ({touched}), so it is held until the owner approves it. They have already been sent the details and the approve/deny prompt directly, so there is no need to repeat any of it.",
            result.CostUsd,
            changed,
            ct);
    }

    private async Task<string?> ApplyDecisionAsync(PendingApproval pending, ApprovalDecision decision, CancellationToken ct)
    {
        _logger.LogInformation("Applying {Decision} for approval {Token}", decision, pending.Token);

        if (decision == ApprovalDecision.Denied)
        {
            await Git.ResetHardAsync(Workspace.AgentRepo, pending.PreRequestSha, ct);
            await _approvals.ClearAsync(ct);
            await CompleteAsync(
                pending.Request,
                RequestStatus.Denied,
                "The owner declined this change and it has been rolled back. Nothing was deployed.",
                0m,
                pending.ChangedPaths,
                ct);
            return null;
        }

        await _approvals.ClearAsync(ct);
        return await BuildAndDeployAsync(
            pending.Request,
            pending.ClaudeSummary,
            // The money was charged when Claude Code ran; approving does not spend again.
            costUsd: 0m,
            pending.ChangedPaths,
            pending.PreRequestSha,
            ct);
    }

    /// <summary>Refuses everything currently queued, with one reason. Used while an approval is
    /// outstanding, so a blocked agent still gets an answer instead of silence.</summary>
    private async Task RefuseWhileBlockedAsync(string reason, CancellationToken ct)
    {
        var request = await TakeNextRequestAsync(ct);
        if (request is null)
            return;

        await CompleteAsync(request, RequestStatus.Refused, reason, 0m, [], ct);
    }

    /// <summary>Moves the oldest request out of the agent-writable directory before touching it -
    /// a request must not be mutable while it is being acted on.</summary>
    private async Task<CapabilityRequest?> TakeNextRequestAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Workspace.RequestsIn))
            return null;

        var file = Directory.GetFiles(Workspace.RequestsIn)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();

        if (file is null)
            return null;

        Directory.CreateDirectory(Workspace.StateProcessing);
        var moved = Path.Combine(Workspace.StateProcessing, Path.GetFileName(file));
        File.Move(file, moved, overwrite: true);

        var json = await File.ReadAllTextAsync(moved, ct);
        var request = CapabilityRequest.TryParse(json);

        if (request is null)
        {
            _logger.LogWarning("Discarding unparseable capability request {File}", Path.GetFileName(file));
            return null;
        }

        _logger.LogInformation("Picked up request {Id}: {Capability}", request.Id, request.Capability);
        return request;
    }

    /// <summary>Claude Code is told to commit, but a run that ends mid-edit (turn limit, budget
    /// cap, timeout) can leave the tree dirty. Committing it here means the diff the path policy
    /// sees is the complete set of changes, and the repo is never left in a state where the next
    /// request builds on top of someone else's half-finished work.</summary>
    private async Task CommitLeftoversAsync(CapabilityRequest request, CancellationToken ct)
    {
        if (!await Git.IsDirtyAsync(Workspace.AgentRepo, ct))
            return;

        _logger.LogWarning("Claude Code left uncommitted changes for request {Id}; committing them", request.Id);
        await Git.RunAsync(Workspace.AgentRepo, ct, "add", "-A");
        await Git.RunWithStdinAsync(
            Workspace.AgentRepo,
            $"Supervisor: commit uncommitted changes from request {request.Id}",
            ct, "commit", "-F", "-");
    }

    /// <summary>Writes the result twice, for two different readers: requests/out for anyone
    /// inspecting the system, and the inbox so the agent's next cycle knows what happened and can
    /// tell the owner. Every terminal state gets both - the round trip is never allowed to end in
    /// silence, because from the owner's side a silent failure and a slow success look
    /// identical.</summary>
    private async Task CompleteAsync(
        CapabilityRequest request,
        RequestStatus status,
        string detail,
        decimal costUsd,
        IReadOnlyList<string> changed,
        CancellationToken ct)
    {
        var outcome = new RequestOutcome(request.Id, request.Capability, status, detail, DateTimeOffset.UtcNow, costUsd, changed);

        Directory.CreateDirectory(Workspace.RequestsOut);
        await File.WriteAllTextAsync(Path.Combine(Workspace.RequestsOut, $"{request.Id}.json"), outcome.ToJson(), ct);

        await new InboxEnvelope(
            InboxKind.Outcome,
            DateTimeOffset.UtcNow,
            request.ReplyTo,
            BuildOutcomeText(request, outcome),
            outcome).WriteAsync(ct);

        CleanUpProcessing(request.Id);
        _logger.LogInformation("Request {Id} completed: {Status}", request.Id, status);
    }

    /// <summary>The envelope text is what the model actually reads, so it says what happened and
    /// what to do about it - the point is not to report a status but to finish the thing the owner
    /// originally asked for.</summary>
    private static string BuildOutcomeText(CapabilityRequest request, RequestOutcome outcome)
    {
        var lines = new List<string>
        {
            $"A capability you asked for has come back: \"{request.Capability}\".",
            string.Empty,
            $"Result: {outcome.Status}",
            outcome.Detail,
        };

        if (!string.IsNullOrWhiteSpace(request.OriginalMessage))
        {
            lines.Add(string.Empty);
            lines.Add($"You asked for this because the owner said, at {request.OriginalMessageUtc:yyyy-MM-dd HH:mm:ss} UTC:");
            lines.Add($"\"{request.OriginalMessage.Trim()}\"");
            lines.Add(string.Empty);
            lines.Add(outcome.Status == RequestStatus.Deployed
                ? "You have the capability now. Do the thing they actually asked for - using it, not describing it - and then tell them it's done. If their request was time-relative, it is relative to when they sent that message, not to now."
                : "Tell them where things stand, briefly and honestly. Do not claim to have done anything you could not do.");
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add("Nobody is waiting on this one - it came from your own initiative. Say something only if it is worth their attention.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void CleanUpProcessing(string requestId)
    {
        if (!Directory.Exists(Workspace.StateProcessing))
            return;

        foreach (var file in Directory.GetFiles(Workspace.StateProcessing, $"*{requestId}*"))
            File.Delete(file);
    }

    private static string Summarize(string? summary, string fallback)
        => string.IsNullOrWhiteSpace(summary) ? fallback : summary.Trim();

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
