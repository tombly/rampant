# Tools

Self-built capabilities live here, one file each. This directory is empty at genesis on purpose:
every tool in it should be one the agent noticed it needed and asked for.

It is also the only part of this repository that deploys without a human. A change under `Tools/`
(plus `SELF.md` and `Rampant.Agent.csproj`) builds and goes live on its own; anything else waits
for the owner to approve it over Signal. So a capability belongs here unless it genuinely cannot
be expressed as a tool.

## The contract

`ToolLoader` finds these by reflection. A class that breaks any of the rules below is skipped
silently — nothing crashes, the tool simply never appears.

```csharp
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
```

- `public`, non-abstract, in namespace `Rampant.Agent.Tools`, in a file under `Tools/`
- a public parameterless constructor
- tool methods carry `[AgentTool("snake_case_name")]` **and** `[Description(...)]`
- every parameter carries its own `[Description(...)]`; use `string`, `int`, `long`, `double`,
  `bool`, or arrays of those
- return `string` or `Task<string>` — whatever comes back goes to the model as the tool result, so
  return something it can act on even on failure (`"no reminders set"`), never an empty string
- throwing is fine; the message is reported back to the model

## What a tool may do at runtime

Writable: `AgentPaths.Data`, `AgentPaths.Outbox`, `AgentPaths.Logs`. Nothing else — this process
does not own its own source or its own compiled binary, and attempting to write them throws.

- **Persist under `AgentPaths.Data`.** Every deployment restarts the agent, including the one that
  deploys your tool. Anything holding a timer, a queue, or scheduled state must reload from disk on
  startup or it is lost the first time anything else gets built.
- **Speak with `Outbox.WriteAsync(text)`.** That is how background work reaches the owner. There is
  no Signal connection in this process to open.
- **Background work runs on `CancellationToken.None` once started.** A restart must not cancel
  something mid-flight — that is how you get a message that was sent but never marked as sent.
- Network access is unrestricted, and NuGet packages may be added to `Rampant.Agent.csproj` — but
  every `PackageReference` needs **one exact `Version`** (`1.2.3` or `[1.2.3]`). The supervisor
  rejects floating versions (`*`, `1.*`) and ranges (`[1.0,2.0)`) and rolls the whole commit back:
  a version resolved at build time means a later rebuild could pull different code with nothing in
  the diff to show for it, and then nothing can say what is actually running.
