using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rampant.Agent;

/// <summary>
/// The one tool that exists at genesis, and the only route from "I cannot do that" to being able
/// to. Everything else this agent will ever be able to do starts here.
///
/// It describes; it does not build. There is no compiler, no coding tool, and no Anthropic
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
            Capability = capability,
            Description = description,
            // Carried automatically. The process that reads the outcome has no memory of this
            // conversation and no way to recover it - at genesis this is the only continuity the
            // agent has across a restart.
            ReplyTo = _envelope.Sender,
            OriginalMessage = _envelope.Kind == InboxKind.Message ? _envelope.Text : null,
            OriginalMessageUtc = _envelope.Kind == InboxKind.Message ? _envelope.ReceivedUtc : (DateTimeOffset?)null,
        };

        Directory.CreateDirectory(AgentPaths.RequestsIn);
        var name = $"{id}.json";
        var path = Path.Combine(AgentPaths.RequestsIn, name);
        var tmp = Path.Combine(AgentPaths.RequestsIn, "." + name + ".tmp");

        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(request, JsonOptions));
        File.Move(tmp, path, overwrite: true);

        return $"""
            Request {id} filed for "{capability}".

            The supervisor will decide whether it can be built now - it may refuse on cooldown or
            budget, and it will say so. If it does get built you will be restarted, and a later
            version of you will be told the outcome along with what the owner originally asked for.

            Say something to the owner now: that you cannot do this yet, that you have asked for it,
            and that you will come back to them. Do not claim to have the capability.
            """;
    }
}
