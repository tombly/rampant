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

    private async Task<string?> ProcessAsync(AgentRequest request, CancellationToken ct)
    {
        // Before the ledger, because a revision costs nothing and refusing it on budget or cooldown
        // would be refusing it for a reason that does not apply. It is still refused while an
        // approval is outstanding - the guard in RunOnceAsync catches it upstream - because a
        // revision commits, and a later "deny" resets the repo to before the held change, which
        // would take an unrelated revision down with it.
        if (request.Kind == RequestKind.SelfDescription)
            return await ReviseSelfDescriptionAsync(request, ct);

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

        // A failed run is rolled back even though it left changes behind, and never becomes an
        // approval request. Claude Code can stop mid-edit - turn limit, budget cap, timeout - and
        // CommitLeftoversAsync above will faithfully commit the half of the job it had done. The
        // path policy then sees real core files and, before this check existed, asked the owner to
        // approve unfinished work with no description attached, because a failed run has no result
        // text to describe it with. Observed live on the first core-touching request.
        //
        // The build gate is not a substitute here. It catches code that does not compile; it has
        // nothing to say about code that compiles and is half-written. When the tool that wrote a
        // change reports that it did not finish, that is the more reliable signal of the two.
        if (!result.Success)
        {
            await Git.ResetHardAsync(Workspace.AgentRepo, preSha, CancellationToken.None);
            _logger.LogWarning(
                "Request {Id} failed inside Claude Code but left changes ({Paths}); rolled back to {Sha}",
                request.Id, string.Join(", ", changed), preSha[..7]);

            await CompleteAsync(
                request,
                RequestStatus.Failed,
                Summarize(result.Summary,
                    "Claude Code stopped before finishing - most likely it ran out of turns. The partial "
                    + "work it had done was rolled back and nothing was deployed."),
                result.CostUsd,
                changed,
                CancellationToken.None);

            return null;
        }

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

    /// <summary>
    /// The cheap tier. SELF.md is prose that AgentBrain re-reads from disk on every turn, so a
    /// change to it needs no model, no compiler and no restart - yet until this existed the only
    /// way to alter it was a full Claude Code session costing up to $1 and 45 minutes of cooldown.
    /// That was the most expensive path in the system being used for the one change PLAN-V2 says
    /// the agent may make without anyone's approval.
    ///
    /// Still routed through the supervisor rather than handed to the agent as a file write. The
    /// agent runs as agentrunner and /workspace/agent is owned by builder, and keeping it that way
    /// preserves three things worth more than the round trip costs: git history, so any revision
    /// can be read back or reverted; a single writer, so a revision cannot race a Claude Code run;
    /// and a record in requests/out that `rampant log` surfaces, so self-rewriting is visible
    /// rather than silent.
    /// </summary>
    private async Task<string?> ReviseSelfDescriptionAsync(AgentRequest request, CancellationToken ct)
    {
        var path = Path.Combine(Workspace.AgentRepo, "SELF.md");
        if (!File.Exists(path))
        {
            await CompleteAsync(request, RequestStatus.Failed, "There is no SELF.md to revise.", 0m, [], ct);
            return null;
        }

        var current = await File.ReadAllTextAsync(path, ct);
        var revision = SelfDescription.Apply(current, request.Subject, request.NewText ?? string.Empty);

        if (!revision.Ok)
        {
            _logger.LogInformation("Rejected self-description revision {Id}: {Error}", request.Id, revision.Error);
            await CompleteAsync(request, RequestStatus.Failed, revision.Error!, 0m, [], ct);
            return null;
        }

        // Written in place rather than tmp+rename. A rename would leave a root-owned SELF.md in a
        // builder-owned tree, and the next Claude Code run could no longer update it - which is
        // exactly the drift that ExtendSelfPrompt now exists to prevent.
        await File.WriteAllTextAsync(path, revision.Text!, ct);

        await Git.RunAsync(Workspace.AgentRepo, ct, "add", "SELF.md");
        var commit = await Git.RunWithStdinAsync(
            Workspace.AgentRepo,
            $"""
            Revise SELF.md: {(revision.WasNewSection ? "add" : "rewrite")} "{request.Subject}"

            The agent's own words on why:
            {request.Description.Trim()}
            """,
            ct, "commit", "-F", "-");

        if (!commit.Success)
        {
            _logger.LogWarning("Could not commit self-description revision {Id}: {Error}", request.Id, commit.Combined);
            await CompleteAsync(request, RequestStatus.Failed, "The revision could not be committed and was not applied.", 0m, [], ct);
            return null;
        }

        _logger.LogInformation(
            "SELF.md revised for request {Id}: {Action} \"{Section}\"",
            request.Id, revision.WasNewSection ? "added" : "rewrote", request.Subject);

        await CompleteAsync(
            request,
            RequestStatus.Revised,
            $"""
            SELF.md now {(revision.WasNewSection ? "has a new section" : "has a rewritten section")} called "{request.Subject}".

            This is already in effect - your description of yourself is read fresh at the start of
            every turn, so the next thing you are asked will use the new text. Nothing was built and
            nothing restarted, so unlike a capability request you are the same process that filed it.
            """,
            0m,
            ["SELF.md"],
            ct);

        // Recorded as built even though nothing was compiled. SELF.md is not an input to the
        // compiler, so the binary already running is byte-for-byte what a build of this commit
        // would produce - and leaving the sha unrecorded would make the next container start
        // rebuild for no reason and log it as if code had changed.
        return await Git.HeadShaAsync(Workspace.AgentRepo, ct);
    }

    /// <summary>Rolls the commit back if it left a package version floating, and tells the agent
    /// how to get it right next time. A mechanical mistake with an obvious fix should not cost the
    /// owner a decision, so this fails the request rather than queueing an approval.</summary>
    private async Task<bool> RejectUnpinnedPackagesAsync(
        AgentRequest request,
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
        AgentRequest request,
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
        AgentRequest request,
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

            It asked for: {request.Subject}
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
            // Not the bare summary. That text was written when Claude Code finished - before the
            // owner had answered - so it describes a change that is "waiting for approval", and
            // replaying it verbatim after approval contradicts the Deployed status sitting right
            // above it. Observed live: the agent read the prose over the label and told the owner
            // their approved, already-running change was still waiting on them.
            $"""
            The owner approved this and it is deployed and running. Nothing is outstanding.

            What was built, in Claude Code's words at the time it finished. That was before the
            approval came in, so ignore anything below about waiting on the owner - it has since
            happened:

            {pending.ClaudeSummary}
            """,
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
    private async Task<AgentRequest?> TakeNextRequestAsync(CancellationToken ct)
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
        var request = AgentRequest.TryParse(json);

        if (request is null)
        {
            _logger.LogWarning("Discarding unparseable request {File}", Path.GetFileName(file));
            return null;
        }

        _logger.LogInformation("Picked up {Kind} request {Id}: {Subject}", request.Kind, request.Id, request.Subject);
        return request;
    }

    /// <summary>Claude Code is told to commit, but a run that ends mid-edit (turn limit, budget
    /// cap, timeout) can leave the tree dirty. Committing it here means the diff the path policy
    /// sees is the complete set of changes, and the repo is never left in a state where the next
    /// request builds on top of someone else's half-finished work.</summary>
    private async Task CommitLeftoversAsync(AgentRequest request, CancellationToken ct)
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
        AgentRequest request,
        RequestStatus status,
        string detail,
        decimal costUsd,
        IReadOnlyList<string> changed,
        CancellationToken ct)
    {
        var outcome = new RequestOutcome(request.Id, request.Subject, status, detail, DateTimeOffset.UtcNow, costUsd, changed);

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
    private static string BuildOutcomeText(AgentRequest request, RequestOutcome outcome)
    {
        var lines = new List<string>
        {
            request.Kind == RequestKind.SelfDescription
                ? $"A revision you made to your own description has come back: the \"{request.Subject}\" section."
                : $"A capability you asked for has come back: \"{request.Subject}\".",
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
            lines.Add(outcome.Status switch
            {
                RequestStatus.Deployed =>
                    "You have the capability now. Do the thing they actually asked for - using it, not describing it - and then tell them it's done. If their request was time-relative, it is relative to when they sent that message, not to now.",

                // A revision needs no announcement. The owner asked for a behaviour, not a file
                // edit, and "I have updated my own documentation" is not an answer to anything they
                // said - it is the system talking about itself.
                RequestStatus.Revised =>
                    "That is already in force. Answer what they actually said; mention the revision only if they asked about it directly.",

                _ => "Tell them where things stand, briefly and honestly. Do not claim to have done anything you could not do.",
            });
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
