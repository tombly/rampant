using System.Text;

namespace Rampant.Supervisor;

/// <summary>
/// The instructions Claude Code receives. Deliberately constructed here, in supervisor code baked
/// into a root-owned /opt, rather than read from a file in the agent's repo: it is the one place
/// that describes the tool contract, and the loader that enforces that contract is core code the
/// agent cannot edit. Keeping both immutable means they cannot drift apart, and it means a bad
/// self-edit cannot quietly rewrite the instructions given to the thing that edits it.
///
/// The agent's own description of the capability is quoted into the middle of it. Everything
/// around that is fixed.
/// </summary>
public static class ExtendSelfPrompt
{
    public static string Build(CapabilityRequest request)
    {
        var sb = new StringBuilder();

        sb.AppendLine("""
            You are being invoked by Rampant's supervisor to build a capability that Rampant's agent
            asked for. Work only inside /workspace/agent - it is your working directory and the only
            place you may change anything.

            ## What this project is

            /workspace/agent is a small C# project (Rampant.Agent, net10.0). It is the source of a
            conversational agent that reads message envelopes from /workspace/inbox, sends them to an
            LLM along with whatever tools it has, and answers by writing envelopes to
            /workspace/outbox. The agent cannot edit its own source - it describes what it wants and
            the supervisor calls you. Read the code before changing it; it is short.

            ## The request
            """);

        sb.AppendLine();
        sb.AppendLine($"Capability: {request.Capability}");
        sb.AppendLine();
        sb.AppendLine("In the agent's own words:");
        sb.AppendLine();
        sb.AppendLine(request.Description.Trim());
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.OriginalMessage))
        {
            sb.AppendLine("""
                This came from something the owner actually asked for. Their message, verbatim:
                """);
            sb.AppendLine();
            sb.AppendLine(request.OriginalMessage.Trim());
            sb.AppendLine();

            if (request.OriginalMessageUtc is { } askedAt)
            {
                sb.AppendLine($"They sent that at {askedAt:yyyy-MM-dd HH:mm:ss} UTC. If the request is time-relative");
                sb.AppendLine("(\"in an hour\", \"tomorrow morning\"), it is relative to *that* moment, not to now -");
                sb.AppendLine("building takes minutes and the delay must not silently move the deadline.");
                sb.AppendLine();
            }
        }

        sb.AppendLine("""
            ## How a capability is added

            A capability is one new file under Tools/. In the normal case nothing else changes.

                using System.ComponentModel;

                namespace Rampant.Agent.Tools;

                public sealed class Reminders
                {
                    [AgentTool("set_reminder")]
                    [Description("Schedules a message to be sent to the owner at a given time.")]
                    public string Set(
                        [Description("When to send it, UTC, ISO-8601, e.g. 2026-08-01T22:30:00Z")] string dueUtc,
                        [Description("The message to send")] string text)
                    {
                        // ...
                        return "Reminder set.";
                    }
                }

            Rules the loader enforces - a class that breaks them is silently not loaded, so get them
            right:

            - public, non-abstract, in namespace Rampant.Agent.Tools, in a file under Tools/.
            - a public parameterless constructor.
            - tool methods marked [AgentTool("snake_case_name")] and [Description("...")].
            - every parameter needs its own [Description("...")]. Use simple parameter types only:
              string, int, long, double, bool, or arrays of those. No custom types, no enums, no
              nullable value types.
            - return string or Task<string>. Whatever you return goes back to the model as the tool
              result, so return something it can act on - including on failure ("no reminders set"),
              never an empty string.
            - throwing is fine; the core catches it and reports the message back to the model.

            What a tool may do at runtime:

            - Write files only under AgentPaths.Data ("/workspace/data"), AgentPaths.Outbox and
              AgentPaths.Logs. Everything else in the container is read-only to this process,
              including its own source and its own compiled binary. Anything a tool needs to survive
              a restart goes under AgentPaths.Data.
            - Speak to the owner outside of a reply by calling Outbox.WriteAsync(text) - that is how
              a background timer or a scheduled job reaches them. Do not open a Signal connection;
              the agent process has no access to it.
            - Use the network freely, and add NuGet packages to Rampant.Agent.csproj if you need
              them. Every PackageReference needs one exact Version - "1.2.3" or "[1.2.3]". This is
              checked and enforced: a floating version ("*", "1.*") or a range ("[1.0,2.0)") gets
              the whole commit rolled back and the request reported as failed, because a version
              resolved at build time means a later rebuild could pull different code with nothing
              in the diff to show for it.

            The agent process is stopped and restarted on every deployment, including the one that
            deploys your work. Anything holding a timer, a queue, or an in-flight operation must
            persist its state under AgentPaths.Data and reload it on startup, or it will be lost the
            first time anything else is built. If you register background work, run it on
            CancellationToken.None once it has started so a restart cannot cancel it mid-flight.

            ## Deployment rules - these decide whether your work ships or waits

            Changes under Tools/, plus SELF.md and Rampant.Agent.csproj, build and deploy on their
            own the moment the build succeeds.

            Every other file - the message loop, the LLM wiring, the tool loader, Program.cs - is
            held until the owner approves it over Signal, which can take hours.

            So express the capability as a tool if there is any way to do so. If it genuinely cannot
            be, change core files and say plainly in your final message which ones and why - that
            text is what the owner reads when deciding.

            ## Update SELF.md - this is not optional

            SELF.md is the agent's system prompt, rebuilt from that file on every single turn. If
            you add or change what it can do and leave SELF.md alone, the agent carries on believing
            the old description of itself and will tell the owner it cannot do the thing you just
            built. That is not hypothetical: a memory capability was added while SELF.md still said
            "You have no memory... say plainly that you do not remember and ask them to remind you",
            and the agent dutifully disclaimed its own new tools six times in a row while the owner
            grew increasingly frustrated.

            So: read SELF.md, find every statement your change has made false, and correct it. Add a
            short description of the new capability and when to reach for it. Keep the voice and
            structure of the file - it is written to be read by the agent as a description of itself,
            not as release notes. SELF.md is in the auto-deploy set, so editing it costs no extra
            approval.

            ## Finishing

            - `dotnet build` in /workspace/agent must succeed. A commit that does not compile is
              rolled back and the capability is reported as failed.
            - Commit your work with git. There is no remote; do not push.
            - Your final message is recorded, and is shown to the owner verbatim if approval is
              needed. Two or three sentences: what you built, and anything they should know about
              it. If you could not do what was asked, say so plainly rather than describing what you
              did instead.
            """);

        return sb.ToString();
    }
}
