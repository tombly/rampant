using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampant.Agent;

/// <summary>
/// The two tools that exist at genesis: the route from "I cannot do that" to being able to, and
/// the route from "that is not who I am any more" to saying so. Everything else this agent will
/// ever be able to do starts here.
///
/// They split along cost, not importance. A capability needs a model, a compiler and a restart, so
/// it is metered. A change to SELF.md needs none of those - it is prose, re-read from disk every
/// turn - so it is free and lands in seconds. Collapsing the two, which is how this started, meant
/// rewriting one paragraph cost the same as writing a feature.
///
/// Both describe; neither builds. There is no compiler, no coding tool, and no Anthropic
/// credential in this process - the description goes into a directory the supervisor watches, and
/// the supervisor (running as a different user, with its own key and its own budget) does the
/// work. The isolation is the point: there is no path from here to writing this agent's own source.
///
/// Constructed per turn with the envelope that triggered it, so the request automatically carries
/// the conversation it came from. The model supplies what it wants built; it never has to
/// supply - or get right - who to reply to or when the owner asked.
/// </summary>
public sealed class CapabilityTools(InboxEnvelope _envelope)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [AgentTool("request_capability")]
    [Description("""
        Ask for a new capability to be built for you, when you have been asked to do something you
        have no tool for, or when you have noticed something you would be more useful with. Describe
        what you want in plain English; someone else writes the code. Building takes a few minutes
        and restarts you, so you will not see the result yourself - a later version of you gets the
        outcome and finishes the job. Tell the owner you are on it rather than pretending you can
        already do the thing.
        """)]
    public async Task<string> RequestCapabilityAsync(
        [Description("Short name for the capability, lowercase, e.g. 'reminders' or 'web_search'.")]
        string capability,
        [Description("""
            What it should do, in plain English, specific enough for someone to build it without
            asking you questions: the behaviour you want, what it should be called, what information
            it needs, and what it should do at the edges. Say why you want it. Do not write code.
            """)]
        string description)
    {
        var now = DateTimeOffset.UtcNow;
        var id = $"{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0x10000):x4}";

        var request = new
        {
            Id = id,
            FiledUtc = now,
            Subject = capability,
            Description = description,
            // Carried automatically. The process that reads the outcome has no memory of this
            // conversation and no way to recover it - at genesis this is the only continuity the
            // agent has across a restart.
            ReplyTo = _envelope.Sender,
            OriginalMessage = _envelope.Kind == InboxKind.Message ? _envelope.Text : null,
            OriginalMessageUtc = _envelope.Kind == InboxKind.Message ? _envelope.ReceivedUtc : (DateTimeOffset?)null,
        };

        await FileRequestAsync(id, request);

        return $"""
            Request {id} filed for "{capability}".

            This is in progress now. Almost every capability is one new file in your own Tools/
            directory, and those are built and deployed without anyone approving anything - the
            ordinary outcome is that it exists in a few minutes. The exceptions are worth knowing
            and are not the common case: the supervisor refuses if you are out of budget or inside
            the cooldown (your standing is in your prompt, so you can usually see that coming), the
            code has to compile, and a change to your core waits on the owner.

            So tell the owner you are building it, plainly - "give me a few minutes and I'll have
            that" - rather than hedging about whether it might be allowed. The one thing you must
            not do is talk as though you can already do it. You cannot, until a later version of
            you is told it is ready.
            """;
    }

    [AgentTool("revise_self_description")]
    [Description("""
        Rewrite one section of SELF.md, your own description of yourself - what you are for, how you
        behave, what to do on a wake tick. Use it when part of that description has gone wrong or
        incomplete: you gained a capability it does not account for, or you have decided your
        purpose is something else. Free and immediate - no code is written, nothing is compiled and
        nothing restarts, and the new text is in force on your very next turn. Prefer this over
        request_capability whenever what needs to change is how you think about yourself rather
        than what you can do.
        """)]
    public async Task<string> ReviseSelfDescriptionAsync(
        [Description("""
            Exact heading of the section to rewrite, without the '##' - for example
            'The three kinds of turn'. A heading that does not exist yet is added as a new section
            at the end. Your SELF.md is readable at /workspace/agent/SELF.md if you are unsure of
            the wording.
            """)]
        string section,
        [Description("""
            The full new text for that section in markdown, not including the heading line. It
            replaces the entire section, so include everything it should still say - anything left
            out is gone.
            """)]
        string newText,
        [Description("Why you are changing it. Goes into the commit message and the log the owner reads.")]
        string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var id = $"{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0x10000):x4}";

        var request = new
        {
            Id = id,
            FiledUtc = now,
            Kind = "SelfDescription",
            Subject = section,
            Description = reason,
            NewText = newText,
            ReplyTo = _envelope.Sender,
            OriginalMessage = _envelope.Kind == InboxKind.Message ? _envelope.Text : null,
            OriginalMessageUtc = _envelope.Kind == InboxKind.Message ? _envelope.ReceivedUtc : (DateTimeOffset?)null,
        };

        await FileRequestAsync(id, request);

        return $"""
            Revision {id} filed for the "{section}" section.

            It is applied within seconds and takes effect on your next turn; you will get an outcome
            message confirming it. Nothing is built and you are not restarted, so this costs nothing
            and there is no delay worth warning the owner about.
            """;
    }

    /// <summary>Written under a dotted name and renamed into place, so the supervisor - which polls
    /// this directory - can never pick up a half-written request.</summary>
    private static async Task FileRequestAsync(string id, object request)
    {
        Directory.CreateDirectory(AgentPaths.RequestsIn);
        var name = $"{id}.json";
        var path = Path.Combine(AgentPaths.RequestsIn, name);
        var tmp = Path.Combine(AgentPaths.RequestsIn, "." + name + ".tmp");

        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(request, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
