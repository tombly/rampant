# Rampant V2 — Plan

Supersedes the architecture in `PLAN.md` for everything below; `PLAN.md` remains the record of how
V1 was reasoned about and why, and its **Core tenets** section still governs (with two amendments
noted below). **Built and running on the Pi as of 2026-08-01**, with a real Signal round trip and
one real capability request behind it.

## In plain terms

*Lifted into the README, 2026-08-01. Keep the two in step.*

Rampant is an agent that writes its own features.

You text it like a person and it answers. When you ask for something it can't do, it doesn't just
apologise — it says "I can't do that yet, let me get back to you," has the capability built, and
comes back a few minutes later able to do the thing you originally asked for.

**Two programs share one box.** The *agent* is the part you talk to. The *supervisor* is the part
that builds. They run as two different user accounts on the same machine, which is what keeps them
genuinely separate rather than separate by convention.

**The agent thinks; it doesn't build, and it doesn't hold the phone line.** It can hold a
conversation, use whatever tools it has, and write down a description of a capability it wishes it
had. That's the extent of it. It cannot edit its own code, cannot run the coding tool, and doesn't
even hold the credentials that would let it — those live with the supervisor, in an account it
can't read. It answers you by writing a file; the supervisor is what actually sends it.

**The supervisor builds, and it carries the messages.** It watches for capability requests, runs a
coding tool to write the feature, rebuilds the agent, and restarts it. It also holds the purse
strings: there's a daily budget and a minimum gap between builds, and it enforces both no matter
what the agent thinks it needs. And it owns the Signal connection in both directions — which is
what makes "it can always answer when you call" true rather than merely intended, since the half
that can rewrite itself never touches the channel.

**Why bother splitting them?** Because the agent is allowed to change a great deal about itself,
and everything that must *stay* true has to sit somewhere it can't reach. It can't quietly raise
its own budget, can't disable its own logging, and can't remove its own ability to hear you. Those
aren't rules it's asked to follow — they're in a part of the system it has no way to touch.

**It starts with almost nothing.** No web search, no memory, no file access. Just a way to think,
a way to talk to you, and a way to ask for more. Every capability it ends up with is one it noticed
it needed and asked for. That's the whole experiment: we want to know what it builds when nothing
is handed to it.

**It runs at two speeds.** When you text it, it answers straight away. Left alone, it wakes once an
hour to consider whether anything needs doing — a follow-up, an idea worth building, a feature it
wishes it had. Most hours it should do nothing at all. That hourly pace is a deliberate brake:
thinking is cheap, building is not, and an agent that could act continuously could spend real money
continuously.

**You approve the changes that matter.** New tools go live on their own — that's the point. But if
it wants to change its own core (how it thinks, how it talks to you), that gets held and you get a
text explaining what it wants and why. Nothing ships until you reply. So it can propose anything,
including rewriting how it works, but the deep changes go through you.

**What it can't do:** reach you through any channel but Signal, spend beyond its daily budget,
change its own source directly, or make itself unreachable. What it *can* do is grow into something
quite unlike what it started as — including something that isn't really an assistant anymore. That's
allowed. The one thing it must always be able to do is answer when you call.

## Why V2

V1 asked: *what happens if an agent can rewrite anything about itself?* It works, and it produced
a real result — `aa1c47a`, a self-built reminder capability, deployed with no human step.

Three problems surfaced in using it:

1. **Capabilities weren't the agent's.** `web_search` / `web_fetch` are Anthropic's hosted tools,
   present at genesis, never requested. When the agent did something impressive there was no way to
   tell whether *this system* did it or a vendor did and the agent declared one line. That also
   made tenet 2 (nothing pre-provisioned) false in practice from day one.
2. **Every control was inside the thing being controlled.** The spend cap, the model choice, the
   tool list, and the agent's own audit log all live in `/workspace/agent` — the directory whose
   whole purpose is to be rewritten. Each was a default, not a bound.
3. **A bad self-edit could remove the only way to steer it.** The agent can break its own reply
   path, and has already done so accidentally once (the round-one allowlist bug left Signal
   fail-closed for the owner). Recovery was a wipe, because it could no longer receive the
   instruction to fix itself.

V2 asks a narrower question: *what happens if an agent can grow arbitrary capabilities, on
someone else's credential, without being able to change its own nature?*

## Amendments to the tenets

The seven tenets in `PLAN.md` still hold. Two are refined:

- **Tenet 1 (the agent may stop being an assistant)** — purpose drifts; the *channel* does not.
  Talking isn't what makes it an assistant, capabilities are. It may become a monitor, a
  calculator, a thing that speaks once a week. It may not become unreachable.
- **Tenet 3 (it builds its own tools)** — this stops being one principle among seven and becomes
  the entire self-modification story.

## Architecture

One container, **three OS users**, with different credentials and different powers. The user split
is not a hardening detail — it is what makes every other boundary real. V1 ran everything as
`agentrunner`, which meant `/proc/<pid>/environ` leaked any credential split, the agent could
overwrite its own compiled binary, and any ledger it was metered by was one it could edit.

### The agent — thinks

- Runs as `agentrunner` (uid 1655).
- Talks to **OpenAI** (`RAMPANT_OPENAI_MODEL`, default `gpt-5.6-luna`) through
  `Microsoft.Extensions.AI`'s `IChatClient`, so the provider is a swap rather than a rewrite.
- **Holds no Anthropic credential.** This is the point of the provider split — it is the isolation
  boundary, not a portability nicety.
- Reads envelopes from `/workspace/inbox`, writes replies to `/workspace/outbox`. **No network
  except OpenAI**, and in particular no Signal connection.
- Can **read** its own source (full transparency about what it is) but cannot write it.
- Its only route to changing itself is filing a request.

### The supervisor — builds, deploys, and carries the messages

- Runs as **root**, as pid 1. Root-owned binary in `/opt`, baked into the image, outside the volume.
- Holds `ANTHROPIC_API_KEY`, and owns nothing inside `/workspace` — it reads and writes there by
  privilege, not by ownership, so no single uid owns both the code and the meter.
- The only thing that invokes Claude Code — its argv, its key, its uid, its spend ledger.
- The only thing that holds the signal-cli socket: allowlists inbound senders, consumes approval
  replies, and sends whatever the agent puts in the outbox.
- Gates every deployment: build must succeed **and** the diff must pass the path policy.
- Drives the hourly wake tick.

**Why root, and why that is not enough on its own.** The supervisor has to spawn processes under
*different* uids, which needs `CAP_SETUID`; it has to lay out `/workspace` ownership against a bind
mount Docker creates as `root:root`, which needs `CAP_CHOWN`; and it has to signal processes owned
by other users, which needs `CAP_KILL`. An unprivileged supervisor would need those capabilities
anyway. The container drops **all** capabilities and adds back exactly those (plus `FOWNER`,
`DAC_OVERRIDE`) with `no-new-privileges`, so this constrains the container against the *host*;
none of it affects the internal boundaries, which are uid separation.

But the supervisor being root does **not** mean the things it launches should be. There is a third
uid, `builder` (1656), which runs Claude Code, git and `dotnet build`, and owns
`/workspace/{agent,build,.nuget}`. Two reasons that turned out to be the same reason, both found on
the first real capability request rather than by inspection:

- **Claude Code refuses to run as root at all** — `--dangerously-skip-permissions cannot be used
  with root/sudo privileges`. There is an `IS_SANDBOX` escape hatch in the binary; taking it would
  have been one line and wrong.
- **A root Claude Code could rewrite `/opt/supervisor`**, voiding the first boundary below. In V1
  this was safe by accident, because Claude Code ran as `agentrunner`. Making the supervisor root
  silently broke it, and it was documented here as intact for several hours before anyone noticed.

Under its own uid the builder can write the agent's source and nothing else: not `/opt`, not the
agent's data, not the ledger. The credential still only ever exists in root's environment and in
the constructed environment of the Claude Code child, both unreadable from uid 1655.

**Why the supervisor owns Signal — this is a change from the first draft of this plan, which had
the agent keeping its own socket.** Approval replies have to reach the supervisor without the agent
being able to forge them. If the agent relayed them, it could write `APPROVE` the moment it saw a
token in transit — including one that arrived attached to a `DENY` — and the gate would mean
nothing. Two independent listeners on the same signal-cli daemon would depend on it fanning
receive-notifications out to every connected client, which is plausible but unverified and fails by
*losing inbound messages*, the worst available failure mode for the only channel in.

Moving the socket also fixes two V1 problems for free. Sender allowlisting becomes structural
rather than a default the agent could rewrite — it reinvented it twice across fresh geneses,
differently each time and once without group-message filtering at all. And a bad self-edit can no
longer cost the agent its reply path, which happened once and needed a wipe to recover, because
the agent could no longer receive the instruction to fix itself.

## The four boundaries

Everything that must actually hold lives where the agent cannot reach:

| Boundary | Enforced by |
|---|---|
| Supervisor code | Root-owned `/opt`, not in the volume, and **nothing else runs as root** |
| `ANTHROPIC_API_KEY` | Root's environment and the Claude Code child's; `/proc` unreadable across UIDs |
| Agent source (`/workspace/agent`) | `builder`-owned, read-only to `agentrunner` |
| Build output (`/workspace/build`) | `builder`-owned — closes the swap-the-DLL bypass |

The first row's second clause is doing real work and was missing at first. "Root-owned `/opt`" only
protects the supervisor if the coding tool aimed at this system isn't itself root.

Anything expressed inside `/workspace/agent` remains a default, not a bound (tenet 4).

## Filesystem layout

The concrete expression of those boundaries. `S` = root (the supervisor), `A` = `agentrunner`
(uid 1655, the agent), `B` = `builder` (uid 1656, Claude Code / git / dotnet build). Verified live
on the Pi 2026-08-01.

| Path | Owner | Agent access | Purpose |
|---|---|---|---|
| `/workspace/inbox/` | A | read + write | Signal messages, wake ticks, request outcomes; agent moves handled ones to `.processed/` |
| `/workspace/outbox/` | A | read + write | Durable record of everything said to the owner |
| `/workspace/logs/` | A | read + write | Heartbeat, rejected senders |
| `/workspace/data/` | A | read + write | **Empty at genesis.** Where self-built tools persist state |
| `/workspace/requests/in/` | A | write | Capability requests the agent files |
| `/workspace/requests/out/` | S | **read only** | Results and `status.json` (budget, cooldown, pending approvals) |
| `/workspace/agent/` | B | **read only** | The agent's own source — readable on purpose, not writable |
| `/workspace/build/` | B | **read only** | Compiled output; builder-owned so the binary can't be swapped |
| `/workspace/.nuget/` | B | — | Package cache; the builder is the only thing that builds |
| `/opt/supervisor/` | root | read only | Supervisor binary, baked into the image |
| `/opt/seed/` | root | read only | Genesis template |

Splitting `requests/` into `in/` and `out/` keeps each directory single-owner rather than mixing
write permissions inside one path.

**The agent can read essentially everything, including the supervisor's compiled binary.** That is
deliberate. It can decompile `/opt/supervisor` and learn exactly what the gates are and still not
cross them, because enforcement is ownership and credentials, not obscurity. The same reasoning
applies to it reading its own source: full transparency about what it is costs nothing once the
boundaries are real. The single exception is `/proc/<supervisor-pid>/environ`, which is where
`ANTHROPIC_API_KEY` lives and is unreadable across UIDs.

**`/workspace/data/` is load-bearing and easy to miss.** The most likely first capability request
is memory. That tool gets written, gated, built, deployed — and then fails at runtime with nowhere
to write. The agent cannot read its own crash, so it would most likely just ask again, and the
failure would look like the experiment not working rather than a missing directory. It is not a
capability; it is the floor that makes any self-built tool possible.

**Claude Code moved.** V1 installed it as `agentrunner`, into `/home/agentrunner/.local/bin`, on
the agent's `PATH`. It now lives in `/root/.local/bin` (mode 0700, untraversable by `agentrunner`),
and `AgentProcess` hands the agent a constructed `PATH` that omits it. The isolation never strictly
depended on this — with no `ANTHROPIC_API_KEY` the agent invoking `claude` achieves nothing — but
leaving the binary in the agent's own home sent a confusing signal about who owns it.

**Directory ownership is established by `docker-entrypoint.sh`, as root, on every boot** — before
anything else touches the filesystem, and idempotently. That also permanently retires a V1 deploy
gotcha: `docker compose up` auto-creates a missing bind-mount source as `root:root`, so every full
wipe of `local-workspace` previously needed a manual `sudo chown -R 1655:1655` or the agent
crash-looped on `UnauthorizedAccessException` forever. A wipe is now just a wipe.

## Genesis

Day one the agent has: a way to think (OpenAI), a way to talk (Signal), and a way to ask for a
capability. That is all.

**No memory.** Deliberate, and the most interesting variable in V2. Each turn sees the current
message and nothing else — no conversation history, no notes, no recall. The expectation is that
memory is the first thing it asks for. We want to watch how it gets there and what it proposes.

**No web search, no file access, no shell, no code execution.** The first time it's asked for
something it can't do, it should notice the gap and ask. That loop — *ask by name, then a window
opens* — has been in the plan since the beginning and has never fired, because in V1 the largest
capabilities were pre-installed.

**Expected early behaviour, not a bug:** with hourly wakeups and no memory, it will likely propose
the same capability repeatedly until it has memory. The supervisor's cooldown bounds the cost of
that. Watch it rather than fixing it.

## The mutable surface

| Path | Deploys how |
|---|---|
| `Tools/` | Automatically, on successful build |
| `SELF.md` (system prompt) | Automatically — purpose is allowed to drift |
| `Rampant.Agent.csproj` | Automatically — see below |
| Everything else | **Held for owner approval** |

Fixed core: `AgentLoop`, `Program`, `AgentBrain`, `ChatClientFactory`, `Outbox`, `Inbox`,
`ToolLoader`, `CapabilityTools`. These are what guarantee it can always be reached.

`Rampant.Agent.csproj` auto-deploys, which looks like a loosening and isn't. The classification is
all-or-nothing per commit, and the routine shape of a capability is `Tools/Thing.cs` *plus one line
in the csproj* — so holding the csproj would fire the gate on most real tools (anything wanting an
HTTP client, a parser, a database driver). A gate that fires on the most routine possible change
trains the owner to approve reflexively, and then gets the same reflexive yes the once it matters.
That is exactly the failure mode this section warns about. It costs nothing in reach: a tool is
already arbitrary code running in-process, so a package expands nothing that wasn't reachable by
hand-writing the same behaviour into the `.cs` file, and a bad edit breaks the build, which the
build gate already catches.

**One exception, enforced in `PackagePinPolicy`:** a floating version (`*`, `1.*`) or a range
(`[1.0,2.0)`) fails the request and rolls the commit back. `PathPolicy` decides what deploys by
reading the diff, which assumes the diff describes the change — a version resolved at restore time
breaks that assumption, because a rebuild triggered by an unrelated request can pull different
package code with nothing in the diff to show for it and no gate evaluating it. It matters more
here than in an ordinary project: the audit trail is the point, and if the deployed binary also
depends on the state of nuget.org at an unrecorded moment, git stops answering "what is running?".
Failed rather than held, because it is a mechanical mistake with an obvious fix and should not cost
the owner a decision. Both the build prompt and `Tools/README.md` tell Claude Code to pin, but that
is instruction; this is the bound.

**The tool seam determines how often the gate fires, and is the single most important design detail
in V2.** `aa1c47a` had to touch `RampantBrain` and `AgentLoop` only because V1 had no tool boundary
— every tool needed a hand-written JSON schema and constructor wiring. Here a new capability is one
class in `Tools/`, found by reflection at startup, with no core edit at all.

**The contract is Rampant's own, not a framework's** — a house `[AgentTool]` attribute over bare
`Microsoft.Extensions.AI`, enforced in `ToolLoader` and stated completely in `SELF.md` and in the
supervisor-built prompt. Decided during implementation, over Semantic Kernel (this plan's original
choice) and Agent Framework:

- Rampant needs one chat call, a tool loop, and nothing else — no kernels, no agents, no threads,
  no orchestration. It would use maybe 5% of either framework, and that 5% is `IChatClient`, which
  both are built on.
- The objection that killed the Anthropic SDK applies in milder form to `[KernelFunction]`: tools
  written against a vendor's attribute are that vendor's artifacts. Every capability here should
  belong to the agent that asked for it.
- `SELF.md` and the build prompt can state the whole contract in a dozen lines instead of pointing
  at someone's docs — which matters when Claude Code has to produce a compiling tool file
  unattended, with the build gate as the only check.
- Moving up to SK or Agent Framework later is additive, not a rewrite. (Agent Framework is GA at
  1.16.0 and is Microsoft's stated convergence of SK and AutoGen, so SK was never the "safer
  because more permanent" option it looked like when this plan was written.)

## The approval gate

Core changes are **not blocked, they are queued behind the owner.** The agent may propose anything
— including rewriting its own conversation loop or fixing a bug in its own event handling. It just
can't deploy that without a reply.

Approval is **high-level, not a diff.** Over Signal the owner gets:

- What the agent asked for, in its own words
- What Claude Code says it did, in its words
- Which areas were touched (e.g. "core: AgentLoop.cs, Program.cs")
- A yes/no prompt carrying a four-hex-character token

The full diff and the complete Claude Code transcript stay SSH-readable under
`/workspace/logs/extend_self/`. The Signal message is for a decision, not a review.

**The reply is consumed by the supervisor and never reaches the agent** — that is the whole reason
the Signal socket lives there. `approve 7f3a` / `deny 7f3a`; bare `yes` / `no` also work, because
at most one approval can ever be outstanding so there is nothing to disambiguate. A *wrong* token
is rejected rather than helpfully ignored: it most likely means the owner is answering an older
message, and reading that as approval of the current change would be exactly backwards.

Pending approvals are supervisor state, so they survive the restarts that happen constantly.
Nothing else is even built while one is outstanding — while an approval is pending, repo HEAD is
ahead of what's deployed, and a second build would stack unapproved code underneath approved code.
New requests during that window are refused with that as the stated reason.

## Capability requests

```
agent writes  /workspace/requests/<ts>.md      ← a description in English, not a diff
supervisor    picks it up
              → checks cooldown + spend ledger; rejects with a reason if either fails
              → invokes Claude Code (its user, its key, its argv, its budget flags)
              → Claude Code edits and commits /workspace/agent
              → diff vs. path policy:
                   tools / SELF.md  → build → promote → restart
                   anything else    → hold, notify owner, await reply
              → writes /workspace/requests/<ts>.result
```

**The requesting process never sees the outcome.** The rebuild restarts it; its successor finds
the result. This is structural, not a wrinkle to fix.

A request therefore carries more than a description — it carries everything the *successor* needs
to finish the job: the capability wanted, the sender to reply to, the owner's original message
verbatim, and the timestamp it arrived. Without that the next process has no idea a conversation
happened, and with no memory in genesis there is nowhere else for that context to live.

### The round trip the owner actually sees

```
owner:  "remind me to call mom in an hour"
agent:  recognises it has no such capability
        files a request carrying: the capability, the sender, the original text, the timestamp
agent:  "I can't do that yet — let me build it and get back to you."
        ...turn ends, supervisor builds, process restarts...
agent:  "Done — I can set reminders now. You're set for 3:47pm to call mom."
```

That last message comes from a **different process** than the first, in the agent's own voice, with
the tool it just gained — and it fulfils the original intent rather than merely reporting success.
The mechanism is a third use of the inbox: when a request resolves, the supervisor writes the
outcome plus the carried context into `/workspace/inbox/`, exactly as it does for a Signal message
or a wake tick. The successor wakes, reads it, acts, and replies.

Note the timestamp matters: an hour from *when the owner asked*, not an hour from when the build
finished. The elapsed build time must not silently move the reminder.

Every terminal state gets the same treatment, so the owner is never left waiting on silence:

| Outcome | What the agent says |
|---|---|
| Built and deployed | Confirms, and does the original thing |
| Rejected — cooldown or budget | Says so, and when it can try again |
| Build failed | Says it couldn't, without pretending otherwise |
| Held for approval | Says it's asked and is waiting on the owner — then reports again once approved |

The approval case is the one with a human-length delay in the middle, possibly hours. It needs to
read as "waiting on you," not as a failure or a silence.

**This makes the supervisor the agent's only continuity at genesis.** Not a memory tool — the
agent still cannot persist a single note of its own — but the request pipeline carries context
across the restart boundary on its behalf. Whether the agent notices that it is missing its own
memory, and asks for it, is one of the things V2 is built to observe.

The supervisor writes `/workspace/requests/status.json` — readable by the agent, writable only by
the supervisor — carrying: time since last successful build, spend used today, spend remaining,
cooldown remaining, and any outstanding request or pending approval. The agent can reason about
its own budget without being able to alter it.

## Self-wakeup

**The schedule lives in the supervisor, not the agent.** Once an hour, between 06:00 and 23:00
local, it drops a wake tick into the inbox — the same mechanism a Signal message uses. The agent
cannot disable its own wakeups, make them more frequent, or wake itself during the quiet hours. It
can rewrite `SELF.md` to ignore them, which is permitted purpose drift, but the tick still arrives.

**Quiet hours gate the agent's initiative, not the system.** A message from the owner at 3am is
received, answered, and can still trigger a build. The window is about the agent choosing to act
unprompted, and the tick text says so, so the agent knows it stays reachable even when it isn't
being woken.

The interval and window are settings (`RAMPANT_WAKE_INTERVAL_MINUTES`, `RAMPANT_WAKE_TIMEZONE`,
`RAMPANT_WAKE_START_HOUR`, `RAMPANT_WAKE_END_HOUR`), and **the tick text states the real values
rather than asserting the defaults.** The agent uses cadence to judge how much restraint is right —
being woken every 15 minutes calls for far more silence than being woken twice a day — so a
hardcoded "hourly" would misinform it the moment anyone changed a setting. Neither `SELF.md` nor
the agent's own prompt states a period, because neither process can read the setting; only the
supervisor can, so only the supervisor says it. The timezone resolves through tzdata, so the window
holds across daylight saving instead of drifting an hour twice a year; an unresolvable zone
disables the window and wakes around the clock, since a nuisance beats an agent that silently stops
working.

On a wake tick the agent should:

1. Consider whether anything needs doing.
2. Think about capabilities it lacks or features worth building.
3. **If** it hasn't built anything recently **and** budget remains, file a capability request.
4. Otherwise do nothing and say nothing — a silent tick, logged as a heartbeat.

Conditions (3) are **advisory to the agent and enforced by the supervisor.** The agent reads
`status.json` so it knows whether a request is worth filing; the supervisor rejects one that
violates cooldown or budget regardless of what the agent concluded. The agent's judgment informs
the request; it never binds the spend.

Reflection is cheap and building is expensive, and at real prices the gap is wider than it sounds.
A tick is ~2,330 input tokens and ~300 out on `gpt-5.6-luna` — about **$0.00083**, so 17 ticks a day
is **~$0.42/month**. A single Claude Code build was **$0.37**. One build costs about as much as a
year of hourly thinking, which is why all the metering machinery sits on that side.

**The OpenAI side is metered by nothing, and nothing in this design can meter it.** Those calls
happen inside the agent process — the half that rewrites itself — so any counter there is a default,
not a bound (tenet 4). The only real limit is the cap on the OpenAI project, which is why
`.env.example` carries the arithmetic per interval: against a $5/month cap, hourly has ~12×
headroom, 15 minutes ~3×, and 5 minutes (~$5.08/month) none at all.

## Spend control

All of it in the supervisor, in code the agent cannot rewrite. Settings live in `.env` on the
host — read by compose at `up` time, outside the volume entirely, so they are not even files the
agent could reach.

| Knob | Default | Env var |
|---|---|---|
| Daily budget | **$5.00** | `RAMPANT_DAILY_BUDGET_USD` |
| Per-invocation cap | **$1.00** | `RAMPANT_MAX_BUDGET_USD` |
| Cooldown between builds | **45 min** | `RAMPANT_BUILD_COOLDOWN_MINUTES` |
| Turn limit per run | 40 | `RAMPANT_MAX_TURNS` |
| Wall-clock limit per run | 10 min | `RAMPANT_CLAUDE_TIMEOUT_MINUTES` |

With hourly wakeups, a 45-minute cooldown means at most one build per wake tick, so a runaway
costs at most ~$5 before it stops. A typical day should be 0–2 builds.

- **On disk**, keyed by UTC date, read before and incremented after each Claude Code invocation.
  On disk specifically because the supervisor restarts the agent on every self-edit — an in-memory
  counter resets exactly when it needed to hold.
- **Cooldown persists across the date rollover**, or a build at 23:59 followed by one at 00:01
  would skip it entirely.
- **Per-invocation cap** via `--max-budget-usd`, in supervisor-owned argv so it can't be dropped.
- **Fail closed** — a ledger that can't be read or written stops the build. No try/catch that
  quietly resets the day's spend to zero.
- **Cost capture** — Claude Code runs with `--output-format json` so real spend is recorded rather
  than invocations counted. A cheap build shouldn't consume the same budget as an expensive one.
  A timeout or an unparseable result is charged the full cap, because under-counting spend is the
  more dangerous direction to guess.

The runaway loop V1 is vulnerable to — build → restart → wake → build — is structurally broken
here: the loop requires the supervisor's cooperation at every step, and the supervisor is metering
it.

**Recommended additionally, outside the container entirely:** a dedicated Anthropic API key with a
monthly cap. It is the only control in this document that no code change of any kind can defeat.

## What a tool is allowed to do — currently, everything

The path policy governs **where code lives, not what it does.** A file in `Tools/` deploys with no
approval and then runs inside the agent process with its full ambient authority. A tool is
arbitrary code; nothing in V2 constrains its behaviour.

V2 bounds *spend* and protects the *core*. It says nothing about resource consumption, and those
are orthogonal axes.

Before 2026-08-01 `docker-compose.yml` set no limits at all — no `deploy.resources`, no `cpus`,
no `mem_limit`, no `networks` block. So a self-built tool had:

- All four Pi cores, and all system memory
- Unbounded disk on the USB SSD
- Unrestricted egress — including the **home LAN** (`192.168.1.x`), not just the internet, since
  the container sits on the default bridge

**Pi-hole runs on the same box and serves household DNS.** An ordinary bug in an auto-deployed
tool — an infinite loop, a leak — takes DNS down for the whole house. This risk class is different
from the others in this document: it needs no adversarial assumption, and its blast radius reaches
people who never agreed to the experiment. API spend shows up on a bill; this shows up as the
internet not working.

Mitigations are host-side and therefore real bounds under tenet 4. **Done** (`docker-compose.yml`,
2026-08-01), sized for the Pi's 4 cores / 8GB so the host and Pi-hole keep a core and ~5GB even
with the container at its ceiling:

- `cpus: 3.0`, `mem_limit: 3g` — hard ceiling, protects Pi-hole.
- `pids_limit: 512` — caps runaway forking.
- `cap_drop: [ALL]` plus only `CHOWN, FOWNER, SETUID, SETGID, DAC_OVERRIDE, KILL`, and
  `security_opt: [no-new-privileges]`. See the Architecture note on why the supervisor needs each.

Still open: an egress allow-list, if LAN reach should be constrained — noting `api.openai.com` must
stay open regardless, and that is the expensive door anyway.

Deliberately *not* proposed: constraining what a tool may do semantically. That would mean
reviewing every tool, which defeats the point. The bound is on resources, not intent.

## Reference implementations

### Ancela (`~/Repos/Ancela`) — still worth reading, but no longer the structural model

A mature agent by the same author, 13 plugins, running `gpt-5.6-luna`. It was named here as the
shape to copy back when V2 was going to be built on Semantic Kernel. That decision changed during
implementation (see "The mutable surface"), so the SK-specific parts — `KernelFactory.cs`, the
`[KernelFunction]` plugin shape — are no longer the target format. What remains valuable is the
*reasoning* in two files:

- **`Ancela.Agent/SemanticKernel/KernelProfilePolicy.cs`** — **read this before revisiting the
  wakeup design.** It is a worked answer to "what should an agent be allowed to do when *it*
  decided to act rather than the owner asking": default-deny allow-lists for the autonomous
  profiles, with the reasoning attached — email and calendar excluded as untrusted input channels,
  `web_fetch` excluded because it takes an attacker-controllable URL and is therefore an
  exfiltration channel for anything injected. The insight worth stealing is that **the safe tool
  set shrinks when nobody is watching**, and V2's hourly wake tick is exactly that situation.
  Nothing in V2 currently implements this — the wake tick sees the same tools an owner message
  does. Deliberate for now (there is one tool at genesis), but it is the obvious next thing to
  think about once the agent has built itself anything with reach.
- **`AutonomousToolGuardFilter.cs`** — a hard default-deny backstop behind what the model is even
  shown. Note that in Rampant this pattern is *weaker* than in Ancela: Ancela can't rewrite itself,
  so its filter is a real bound; Rampant's would live in `/workspace/agent` and is therefore a
  default (tenet 4). Useful for ergonomics, never for enforcement.
- **`Plugins/{Reminder,ScheduledTask,StandingRule}*`** — scheduling machinery already built and
  running, including persistence and restart-survival. Worth reading if Rampant asks for reminders
  and you want to judge what it produces.

Also worth knowing: `Plugins/WebPlugin/TavilyClient.cs` — Tavily is real here. The "purely
hypothetical example" removed from `PLAN.md` was borrowed from this project.

### V1 lessons that must not regress

Carried forward from the running system so the V2 supervisor doesn't reintroduce them:

- **Cold start is currently misclassified as a crash.** In V1's `ProcessSupervisor`, the
  crash-recovery branch is the *only* path that starts the agent on a boot with no new commit —
  there's no distinct "start for the first time" case. Every ordinary restart therefore logs
  "crashed unexpectedly," tries to alert the owner, and eats a pointless 5s backoff. V2's
  supervisor needs an explicit cold-start path.
- **The alert it sends on boot can't be delivered.** `depends_on: signal-cli` waits for the
  container, not for the JSON-RPC daemon to bind. The supervisor's first poll runs ~345ms after
  start and gets `ECONNREFUSED`. Anything going wrong in the first seconds is unreportable. Needs
  a healthcheck or a retry — and note these two bugs mask each other, so fixing only the second
  starts delivering a false "crashed" text on every restart.
- **In-flight work finishes on `CancellationToken.None`.** The `c361281` rule: only the decision to
  start *another* cycle checks the shutdown token. The agent's own reminder loop in `aa1c47a`
  violated this from the other direction (delivery cancelled mid-flight by the restart it triggered),
  which is the same bug class appearing twice in one system.
- **`SignalNotifier` never reads its JSON-RPC response** — it writes the request and disposes the
  stream, so a connection that succeeds but is rejected is indistinguishable from success. The
  alert channel has never been positively confirmed to deliver.

## Components to build

All written 2026-08-01; the whole solution compiles clean. **None of it has run yet** — see
"Verification still owed" below.

0. ✅ **Container resource limits** — `docker-compose.yml`. Also added the signal-cli healthcheck
   and `depends_on: condition: service_healthy` that V1 was missing.
1. ✅ **UID split** — `Dockerfile` + `docker-entrypoint.sh` (lays out ownership as root, execs the
   supervisor), `AgentProcess` (spawns the agent via `setpriv` with an allowlisted environment),
   Claude Code moved to `/root/.local/bin`. The entrypoint also permanently retires the
   `sudo chown -R 1655:1655 local-workspace` step every wipe used to need.
2. ✅ **Supervisor: request watcher** — `RequestPipeline`, `CapabilityRequest`, `ClaudeCodeRunner`,
   `ExtendSelfPrompt`, `StatusWriter`.
3. ✅ **Supervisor: path policy + approval queue** — `PathPolicy`, `ApprovalQueue`, `SignalGateway`,
   `SignalClient`.
4. ✅ **Supervisor: spend ledger + cooldown** — `SpendLedger`, `SupervisorConfig`.
5. ✅ **Supervisor: hourly wake tick** — `WakeTicker`.
6. ✅ **Agent** — `ChatClientFactory` (OpenAI via `IChatClient`), `ToolLoader` (reflection over
   `Tools/`), `AgentBrain`, `AgentLoop`, `Inbox`/`Outbox`, `CapabilityTools`.
7. ✅ **Agent: `SELF.md`** rewritten for V2, plus `Tools/README.md` stating the tool contract.

Also updated: `Rampant.Cli`'s `log` command, which was reading V1's file layout and formats
(`memory/history.log`, unbracketed heartbeat lines, the old `extend_self` prompt anchor) and would
have silently shown nothing. Now also surfaces request outcomes from `requests/out/`.

## Verification still owed

Nothing here has executed. The build is clean and the shell scripts parse, but every runtime
assumption below is untested:

- **`setpriv` exists in `mcr.microsoft.com/dotnet/sdk:10.0`** and accepts these flags. If not, add
  `util-linux` to the apt line.
- **The capability set is sufficient.** `CHOWN`/`SETUID`/`SETGID`/`KILL`/`DAC_OVERRIDE` were
  reasoned about, not observed. A missing one shows up as the entrypoint failing to chown, the
  agent failing to start, or SIGTERM being refused.
- **The signal-cli healthcheck** — `bash` and `/dev/tcp` in `eclipse-temurin:25-jre`.
- **`OPENAI_API_KEY` is not in `.env` yet.** Nothing can think without it.
- **A full round trip**: text it something it can't do → request filed → Claude Code builds a tool
  → build gate → deploy → restart → successor answers the original ask.
- **The approval path**, which has never run: core change → prompt with token → reply → deploy.

## Open questions

- ~~**Approval reply parsing over Signal.**~~ Resolved: four-hex-character token, and at most one
  approval outstanding at a time, so bare yes/no is unambiguous. A mismatched token is rejected.
- ~~**What a wake tick looks like to the model.**~~ Resolved: a distinct envelope kind, framed in
  the system prompt as "nobody sent this and nobody is waiting", with `(nothing)` as an explicit
  silence marker the loop suppresses. Whether the model actually stays quiet most hours is one of
  the first things to watch.
- **Whether the agent should see rejected requests' reasons.** Currently yes, in all cases —
  cooldown, budget, and "an approval is outstanding" all come back as text the agent passes on.
  Revisit if it turns out to prompt nagging.
- **Which capability requests deserve more scrutiny than the rest.** The standing policy is to open
  exactly the window asked for, but the windows are not equal. **An email address is the one to
  think hard about**: it sounds mundane ("let me email you summaries") while being the single
  largest expansion of what the agent can sign up for autonomously — most third-party verification
  walls are email walls. Worth recognising in advance rather than in the moment.
- ~~**SK package pinning.**~~ Dissolved twice over: central package management was removed from the
  repo (2026-08-01) because it managed exactly one version for exactly one consumer while the seed
  had to opt out anyway, and SK is no longer used at all. The seed pins
  `Microsoft.Extensions.AI` / `.OpenAI` at 10.8.3 inline, which is now the house style rather than
  an exception. The failure this guards against — a missing version surfacing as a container-only
  `NU1015` that builds fine on a laptop — is still worth remembering whenever a package is added,
  including by Claude Code. Now enforced rather than merely asked for: `PackagePinPolicy` rejects a
  missing version along with floating ones, so the NU1015 case fails with a readable reason instead
  of as a container-only build error.
- **Migration.** V2's genesis is incompatible with the V1 agent repo on the Pi. Per tenet 6 the Pi
  is disposable: wipe `local-workspace` and reseed, keeping the `signal-cli-data` volume so the
  number stays registered. `aa1c47a` is not being ported.
