# Rampant — Sandboxed Self-Modifying Agent in C#

GitHub repo: `github.com/tombly/rampant` (confirmed available).

## Context

This is a new, independent experiment — not part of the Ancela solution, no shared credentials
or cloud resources. The goal: a sandboxed, fully autonomous agent whose own source code it can
freely rewrite, growing itself over time with only occasional high-level direction from the
owner (not a per-step approval loop). An earlier discussion sketched this in Node/JS; the owner
dislikes JS and wants it in C#/.NET instead, reusing their own conventions where sensible since
they already have a large, idiomatic C# codebase (Ancela) built around Semantic Kernel and a
generic-host DI pattern.

**Interaction model.** The owner doesn't author engineering directives ("implement yourself a
memory system") — they just say ordinary things, the same way they'd text Ancela itself
("Remember today is my birthday"). There is one conversational channel, not a split between
"content" and "architecture instructions": each cycle, the agent looks at what was just said,
decides whether its current capabilities can satisfy it, and if not, extends itself first —
narrating that ("I need to add that capability first") is an acceptable and expected reply, not
a failure mode. **This meta-decision is still made on every message** — "can I already do this
with what I have, or do I need to build the ability first" — but see "Architecture: RampantBrain
vs. Claude Code" below for how it's actually realized: not by handing the whole cycle to a full
coding-agent session (the original "Shape A" plan), but by a small direct-API loop choosing,
via ordinary tool selection, whether to call its one `extend_self` tool. Same meta-decision,
made far more cheaply and quickly than routing every single message through a full Claude Code
session to make it.

The core design tension driving every decision below: the sandbox exists to contain **blast
radius and cost** (it must not be able to touch production Ancela, must not run up unbounded
spend, must not survive a bad self-edit with no recovery path) — it is explicitly **not** meant
to constrain *what the agent decides to become or build*. That framing is why the only fixed,
non-agent-owned piece of the whole system is a small process supervisor, and why every guardrail
below is operational (cost caps, resource limits, a kill switch) rather than a policy layer
judging the agent's decisions.

**Decisions locked in for this plan** (from user Q&A):
- **Loop shape: thin orchestrator ("Shape A").** A small C# loop assembles a prompt each cycle
  from its own memory/inbox/log and delegates the entire reasoning-and-acting job to a headless
  `claude -p` subprocess, which does the actual file edits/test runs with its own mature tools.
  Minimal C# to write and maintain — a smaller, safer seed to hand to an ungated self-rewrite
  process than reimplementing weaker bespoke file/shell tools via a Semantic-Kernel tool-calling
  loop would be.
- **Hosting: develop on the laptop, run long-term on a dedicated Raspberry Pi.** The always-on
  requirement (Rampant's loop needs to keep running, able to notice a new inbox message or decide
  on its own to keep working) rules out cheap Azure hosting — an always-running VM/Container
  Instances at adequate specs realistically costs $35–50+/month, and Container Apps' scale-to-zero
  is nearly free but requires restructuring the loop into an event-triggered job, losing the
  spontaneous between-message behavior. A dedicated Pi (5, or 4 with 8GB RAM) sidesteps the
  tradeoff entirely: one-time hardware cost (~$60–150), a few dollars a year in electricity, no
  cloud bill, no laptop dependency. Confirmed compatible: the .NET SDK and `git` are both
  officially multi-arch (amd64 + arm64); Claude Code's npm-based install path (Node.js is fully
  ARM64-native) covers it even if the native standalone installer lacks an arm64 build. (A Cosmos
  Linux emulator was also assumed multi-arch-compatible at planning time during an earlier,
  since-abandoned attempt to use it, but this turned out to be wrong in a way "multi-arch manifest
  exists" didn't capture — see "Opening holes" for the real Pi 4/Cortex-A72 incompatibility found
  during actual deployment.) Use the laptop only for initial Phase 0/1 development and interactive
  testing (hand-editing files, confirming build/restart mechanics), then move the same Dockerfile
  to the Pi once validated. Boot the Pi from a **USB SSD, not microSD** — the workload is
  write-heavy (frequent git commits, repeated `dotnet build`, container layers), and microSD wears
  out and is slow under that pattern. The Dockerfile still ports to Azure unchanged later if
  wanted (Ancela's own Aspire infra is not reused here) — this decision is about where to run it
  long-term, not a technical limitation on portability.

## Architecture

**Two independent git repositories — not a monorepo.** This is deliberate: the supervisor must
never be reachable by the self-modification loop, and the cleanest guarantee of that is that the
two can't share history, a `.sln`, or a project reference.

- **`rampant`** — a conventional, human-owned repo (Dockerfile, `docker-compose.yml`, the
  supervisor project). No autonomy involved here at all.
- **The agent's own repo** — does not live in `rampant`. It exists only on a persistent
  Docker volume, created by the supervisor's seed step on first boot (`git init` + a minimal
  genesis commit), and from then on is read/written exclusively by the agent's own loop process
  and the Claude Code subprocess it invokes. This genesis commit is the one piece of "agent"
  source that isn't agent-authored — everything after is up to it.

```
/workspace/                       <- persistent volume, survives container rebuilds
  agent/                          <- the agent's own git repo (fully mutable)
    .git/
    Rampant.Agent.csproj
    Program.cs
    Loop/                         <- cycle: read inbox/memory/log -> prompt -> invoke claude -> persist -> commit
    (anything else it decides to add — genuinely unconstrained from here)
  memory/                         <- its own persistence (starts simple: files; may grow into anything it chooses)
  inbox/                          <- the ordinary conversation channel: owner drops plain messages here, async
                                      ("remember X", "go look into Y") — not a separate "directives" channel
  outbox/                         <- agent's replies + status narration, read on the owner's own schedule
  logs/                           <- supervisor/build/crash logs, kept separate from outbox
  build/current/, build/previous/ <- double-buffered compiled output (only two kept)
supervisor-state.json             <- owned by the supervisor (last-built commit sha, failure counts),
                                      outside /workspace/agent so it isn't in the agent's normal reach
```

**Self-modification mechanism: recompile + full restart, supervisor-driven.** C# can't
hot-`require()` like JS, so a rewrite takes effect via:
1. Supervisor detects a new `git rev-parse HEAD` in `/workspace/agent` vs. its own
   `lastBuiltSha` (poll or debounced `FileSystemWatcher` — deliberately not dependent on the
   agent's own code cooperating, since a broken self-edit must still be recoverable).
2. Runs `dotnet build /workspace/agent/Rampant.Agent -o /workspace/build/staging`.
   - **Success:** atomically swap `staging` → `current` (previous `current` → `previous`),
     gracefully stop the running agent (bounded grace period, then kill), start the new
     `current`, update `lastBuiltSha`.
   - **Failure:** leave the running process untouched. Log compiler output to
     `/workspace/logs/build-failures/<ts>.log`; this surfaces into the *agent's own next cycle*
     (it reads its own log), so it learns its last edit didn't take effect without any human
     involvement or automatic revert.
3. If a freshly-started process crashes later (source unchanged), just restart the same
   last-known-good binary with backoff — no rebuild needed.
4. **Isolation, belt-and-suspenders:** supervisor binary built once at image-build time, baked
   into `/opt/supervisor`, root-owned. The agent process (and the Claude Code subprocess) runs as
   a separate unprivileged user (`agentrunner`) with write access only under `/workspace`, so OS
   permissions back up the directory-scoping even if Claude Code were ever misdirected outside
   its cwd.

This mirrors the "restart-on-change" idea from the original JS sketch, and gives a free
reliability property JS didn't have: a bad edit that doesn't compile simply never takes effect,
with no human review required to get that protection.

*(Noted as future, agent-or-owner-initiated refinements, not part of this plan: `AssemblyLoadContext`
hot-swap of a sub-component to avoid restart cost once the design is proven; Roslyn `CSharpScript`
for lightweight dynamic tools layered on top. Neither replaces the top-level mechanism above.)*

**Architecture: `RampantBrain` vs. Claude Code (revised from the original "Shape A").** The
original plan gave every single message to a headless `claude -p` subprocess — a full coding-agent
session decided whether the request was already satisfiable and, if not, extended the codebase
itself, all in one pass. Live use exposed a real problem with that: Claude Code is too
opinionated/capable a *driver* for ordinary conversation — routing "remember today is my birthday"
through a full agentic coding session is slow, expensive, and makes the agent's day-to-day "voice"
whatever Claude Code's own agentic style happens to be that session, rather than something
`SELF.md` actually controls. The fix keeps the underlying meta-decision (can I already do this, or
do I need to change my own source) but moves it out of a full coding-agent session:

- **`RampantBrain` (`Rampant.Seed/RampantBrain.cs`) handles ordinary conversation.** A direct call to the
  Anthropic Messages API (`Anthropic` C# SDK, non-beta `client.Messages.Create`), system prompt =
  `SELF.md`, with a small, curated tool set: `recall`/`remember` (read/append files under
  `/workspace/memory`, including the running `history.log`), Anthropic's own hosted `web_search`/
  `web_fetch` tools (server-executed, no client handling needed), and one more — `extend_self` —
  that's the entire bridge to self-modification. A **manual tool loop**, not the SDK's beta
  `ToolRunner`: the tool set here is small and fixed, so hand-rolling the
  request→execute→tool_result→repeat loop against the stable non-beta API keeps the whole shape
  explicit and avoids taking on a beta dependency for a loop this size.
- **Claude Code is demoted to a single tool, `extend_self`, called only when a request genuinely
  needs the agent's own source to change.** Its actual invocation mechanics are unchanged from the
  original plan — same subprocess call, same flags:

  ```
  claude -p "<task description, with SELF.md prepended for context>" \
    --permission-mode bypassPermissions \
    --model claude-sonnet-5 \
    --max-turns <N> \
    --max-budget-usd <per-cycle cap> \
    --no-session-persistence
  ```

  with `cwd=/workspace/agent`. `--permission-mode bypassPermissions` (equivalently
  `--dangerously-skip-permissions`) is the concrete flag for the non-interactive requirement —
  default Claude Code prompts for edit/bash approval, which would hang forever in a detached,
  TTY-less subprocess. **Caveat confirmed from the CLI docs: this skips *some* but not all
  permission checks** — don't assume it's a full bypass. `--max-turns`/`--max-budget-usd` are
  per-invocation guardrails (see Guardrails, below). `--no-session-persistence` keeps Claude Code's
  own on-disk session store out of the picture — the workspace files are the sole source of truth.
  The only thing that changed is *when* this runs: on-demand, when `RampantBrain` decides to call
  `extend_self`, not automatically on every message. The instruction passed to Claude Code is still
  explicit that it should, where possible, carry out the underlying action in the same session
  (write the new persistence code **and** record today's actual fact), not just add the capability
  for a future cycle — same reasoning as the original plan, just triggered by a tool call now
  instead of baked into every cycle's prompt.
- **One real wrinkle worth flagging, not yet resolved:** both `RampantBrain` and the `extend_self`
  subprocess read the same `RAMPANT_MODEL` env var (default `claude-sonnet-5`) — one knob controls
  the model for both the conversational loop and the self-modification tool. Fine for now since
  both default to Sonnet 5 for the same cost reasoning below; if the two ever need independent
  control (e.g. a cheaper model for chat, Fable only for hard self-modification asks), split into
  two separate env vars then, not preemptively now.

**Model: `claude-sonnet-5` by default, for both `RampantBrain` and the `extend_self` subprocess.**
At current pricing (Sonnet 5 $3/$15 per MTok input/output, $2/$10 intro through 2026-08-31; Opus
4.8 $5/$25; Fable 5 $10/$50) Fable costs 2× Opus and 3.3–5× Sonnet — a meaningful multiplier once
it's the model behind every message of a continuously-running conversational loop, not just
occasional self-modification calls. Sonnet-tier is specifically strong on coding/agentic work
relative to cost, and the build-gate/last-known-good safety net already absorbs most of the
correctness risk a cheaper model might otherwise pose for the `extend_self` path. Fable 5 remains
available via the shared `RAMPANT_MODEL` override for occasional owner-initiated hard asks, at the
cost of also raising the price of every routine conversational message until it's switched back —
see the wrinkle noted above.

**Not Haiku 4.5, despite being cheaper still ($1/$5 per MTok).** This is confirmed to be pure
metered API spend with no subscription cushion underneath it (Anthropic's own headless-automation
path has no subscription option — see the API-key note above), which argues for picking the
cheapest model that's actually good at the job, not just the cheapest model. Haiku is built for
well-scoped, simple tasks, not the architectural judgment this workload needs (deciding when a
request exceeds current capability, how to extend the codebase, writing C# that survives the
build gate) — a weaker model here likely means more failed builds and retry cycles, which can
cost more in aggregate than a stronger model finishing the job in one pass. Cheaper-per-token
isn't the same as cheaper-per-outcome.

## Components to build

**`rampant/Rampant.Supervisor/`** (the load-bearing, human-owned piece):
- `Program.cs` — entry point, generic host bootstrap.
- `ProcessSupervisor.cs` — the HEAD-diff/build/restart/last-known-good orchestration described
  above; the single most important file in the system.
- `BuildRunner.cs` — `dotnet build` subprocess wrapper, captures exit code + output.
- `AgentProcess.cs` — start/stop/monitor the built agent binary (graceful stop, hard kill,
  crash-loop backoff).
- `SeedBootstrap.cs` — first-run: if `/workspace` is empty, write the minimal genesis
  `Rampant.Agent` project, `git init && git add -A && git commit` with a baked-in default
  git identity (a commit fails without `user.name`/`user.email` configured — set this in the
  image or seed step, not left to chance).

**Shared subprocess helper** (used for both the `claude` and `git` CLI invocations — don't build
two subprocess abstractions): plain `System.Diagnostics.Process`/`ProcessStartInfo`, no
third-party wrapper. Must cover, none of which the existing
`Ancela.Cli/Commands/GoogleHealthAuthCommand.cs` fire-and-forget precedent provides:
- Async concurrent reading of stdout **and** stderr (avoid the classic single-stream-first
  deadlock).
- A hard timeout with `Process.Kill(entireProcessTree: true)` — Claude Code's Bash tool may spawn
  its own children (test runs, etc.); killing only the top-level process orphans them.
- Explicit `WorkingDirectory`.
- A deliberately constructed, minimal environment for the child — don't pass through the
  parent's whole environment. This is what makes "no shared credentials with Ancela" true by
  construction rather than by omission.

**Genesis `Rampant.Agent` seed** — kept intentionally minimal (resist pre-building more
structure than this, since it's also the first thing an ungated rewrite process will operate on):
reads inbox/memory/log, assembles one prompt, invokes the `claude -p` subprocess via the shared
helper, persists the transcript/result to memory, `git commit`s (message passed via `-F -`/stdin
or argument array — never shell-string interpolation, since the text originates from an LLM),
sleeps, repeats. Strictly sequential — never starts a new cycle while one is still in flight, to
avoid concurrent-write git conflicts.

**Gap: cycle 1 needs a bootstrap context, since `SELF.md` doesn't exist yet.** The prompt-assembly
plan (inbox + outbox + memory + `SELF.md`) assumes those already exist — on a fresh seed none of
them do, and Claude Code has no way to infer "you are an autonomous agent that can rewrite its own
source, here's your workspace, here's how the owner talks to you" from an empty directory alone.
`SeedBootstrap` needs to commit a minimal `BOOTSTRAP.md` (or seed the initial `SELF.md` directly)
alongside the genesis project — a few sentences of fixed, human-authored context: what Rampant is,
where its own source lives, where inbox/outbox/memory live, and that it's expected to extend its
own capabilities as ordinary requests demand it. This is the one piece of *guidance*, not just
source, that isn't agent-authored — everything the agent does with that guidance afterward
(including rewriting `SELF.md` itself) is unconstrained.

**`Dockerfile`** — base image must be `mcr.microsoft.com/dotnet/sdk:10.0`, *not* a runtime/aspnet
image, since the mutable code must keep rebuilding itself inside the running container
indefinitely (a deliberate ~800MB+ size trade-off). Installs `git`, `ca-certificates`, and the
`claude` CLI (prefer the native standalone installer; the npm fallback pulls in Node.js purely as
CLI-packaging plumbing, not something ever authored — worth knowing given the JS aversion, but
it's tooling, not code the agent or owner writes). Supervisor is built via `dotnet publish` at
image-build time and baked into `/opt/supervisor`, root-owned; a separate `agentrunner` user owns
`/workspace`. `ANTHROPIC_API_KEY` (a separate, spend-capped key — never Ancela's), not a claude.ai
subscription: Anthropic's own headless-automation guidance (the Claude Code GitHub Actions
integration) is built entirely around API-key auth, with no documented path for reusing a Pro/Max
login for unattended workloads — and subscription usage windows are sized for interactive human
use, not a system that might run many cycles unattended, which would risk unpredictable
throttling instead of the clean, capped cost an API key gives. Injected at
`docker run`/compose time, never baked into the image. Pin the Claude Code CLI version explicitly
— its flags/permission semantics are a product surface that can change across releases, and a
silent change is an infra problem the agent can't fix (Claude Code *is* its hands).

## Opening holes: external capabilities (Signal, and everything else on request only)

**Nothing is pre-provisioned "just in case" (decided).** The starting sandbox has only the agent's
own source, plain files under `/workspace/memory`, and whatever Claude Code's built-in tools give
it — deliberately minimal. A capability like a database or a search API gets added only when the
agent itself asks for it in response to an actual request, the same way it already says "I need to
add that capability first" for a code-level gap (see `SELF.md`). This reverses an earlier version
of this plan, which pre-provisioned a Cosmos DB account "for later" — it sat there fully unused
the whole time and was removed before ever being wired into any code. Pre-provisioning ahead of
demonstrated need is exactly the kind of premature capability this project should avoid; asking
first, then opening the specific hole requested, keeps every credential the agent ever holds tied
to something it actually needed.

When the agent does ask for something, opening that hole is a repeatable recipe:

1. Provision a **dedicated** credential/resource for the sandbox — never reuse one of Ancela's.
2. Inject it as an env var at `docker run`/compose time (`.env` file, gitignored), never baked
   into the image — same treatment as `ANTHROPIC_API_KEY` already in this plan.
3. Add it to the subprocess helper's deliberately-constructed environment allow-list for
   whichever process actually needs it — typically both the compiled agent process (for real
   runtime use) and the Claude Code subprocess (useful so it can smoke-test code it just wrote
   against the real API/DB during a session).
4. Give the new dependency its **own** cost/usage guardrail — every hole gets its own cap, not a
   shared one (see Guardrails, below).

**Cosmos was tried twice and removed both times, worth remembering if it comes up again.** First
as a local Cosmos Linux emulator sidecar — abandoned when its `vnext-latest` arm64 image turned
out to crash-loop with SIGILL on the Raspberry Pi 4 (every binary in the image faults identically,
even `/bin/sh` used as an entrypoint override, because the image's base layer assumes ARMv8.1 LSE
atomics that the Pi 4's Cortex-A72 cores don't have — true of Apple Silicon/Graviton/Ampere/Pi 5,
not Pi 4, which is why it worked in local Mac testing but not on the real Pi). Then as a live
serverless Azure Cosmos DB account, provisioned ahead of any actual code needing it — this was the
premature-provisioning mistake described above, and it was torn down before ever being consumed by
any code. If a real need for structured/queryable persistence comes up later, `Microsoft.Azure.Cosmos`
with a plain connection string (no `Azure.Identity`/RBAC needed) is still the right shape *once
asked for* — just don't provision it ahead of that ask again.

**Signal, event-driven, no inbound hole in the Pi's gateway (decided; supersedes an earlier Azure
Storage Queue mailbox design).** The original mailbox (two Storage Queues, four SAS tokens) was
built, deployed, and verified working end-to-end — but once Signal came up as an interaction
option it made the mailbox redundant rather than complementary: Signal solves the exact same
problem (the Pi only ever makes *outbound* connections, same shape as its calls to the Anthropic
API/NuGet/GitHub, never listens for anything inbound) with a real chat UX instead of curling a
queue's REST endpoint or building the send/read webpage the mailbox design always deferred to
"later" and never actually got. The mailbox was torn down (Azure storage account deleted,
`infra/main.bicep`/`provision-mailbox.sh` removed) rather than kept as a fallback — if Signal ever
breaks, dropping a file straight into `/workspace/inbox` over SSH is a simpler fallback than a
second whole channel, and running two channels would have needed reply-routing logic (tagging
which channel a message came in on, so a reply goes back out the same way) that a single channel
never needs at all.

- **`signal-cli`** (`AsamK/signal-cli`, an unofficial but well-maintained CLI/JSON-RPC client for
  the Signal protocol) runs as its own sidecar container (`signal-cli/Dockerfile`,
  `eclipse-temurin:25-jre` base — signal-cli 0.14.6 is compiled for Java 25, class file version 69;
  Java 21 fails with `UnsupportedClassVersionError`, confirmed during setup), reachable by the
  `rampant` container only over the internal Docker network, no ports published to the host. Its
  registration data (identity keys, session
  state) lives on a named volume (`signal-cli-data`) — without that, every container rebuild would
  force re-registering the phone number from scratch, needing a fresh SMS/voice code each time.
- **A dedicated phone number, never the owner's personal Signal account.** Registering an
  already-in-use number via `signal-cli` would deauthorize whatever currently owns that number's
  session. Real constraint discovered while picking one: Signal has tightened VoIP-number blocking
  in 2026 — Twilio numbers are rejected outright (all Twilio programmable numbers share carrier
  codes classified as VoIP), and Google Voice has gotten less reliable too after Google's own
  increased eSIM/VoIP restrictions this year. A real prepaid mobile SIM/eSIM (Mint, US Mobile,
  Tello, Visible, etc. — not VoIP-classified) is the reliable option; a landline works too via
  `signal-cli register --voice`, which does voice-call verification instead of SMS.
- **Event-driven, not polled.** The daemon runs with `--receive-mode=on-start` and pushes incoming
  messages as unsolicited JSON-RPC notifications (method `"receive"`) over a persistent TCP
  connection — `Rampant.Seed/SignalClient.cs` holds that connection open and wakes the agent's cycle loop
  immediately when a message arrives, rather than waiting for the next heartbeat tick. This is
  something the queue design couldn't do as cleanly (Storage Queues are inherently poll-based
  without adding something like a Function App queue trigger) and is the main reason Signal is a
  UX upgrade, not just a different transport for the same thing. The other latency source — the
  Claude Code invocation itself taking real wall-clock time to think/edit/build/commit — is
  unchanged and inherent to Shape A; this doesn't make replies feel instant, just picked up
  instantly.
- **One-time registration** (needs the dedicated number in hand, done once against the sidecar's
  volume, not the agent's own persistent workspace). **Stop the daemon first** —
  `docker compose stop signal-cli` — before running either command below: registering/verifying
  while the daemon is already running races it for the same account-data file lock ("Config file
  is in use by another instance"), and confirmed live, the registration can silently fail to take
  effect if you skip this.
  ```
  docker compose stop signal-cli
  docker compose run --rm signal-cli register --voice   # or omit --voice for an SMS code
  ```
  Signal will likely demand a **captcha** before it sends the code at all (`Failed to register:
  Captcha required for verification, use --captcha CAPTCHA`) - get one from
  https://signalcaptchas.org/registration/generate.html, solve it, then right-click (don't click)
  the "Open Signal" link that appears and copy its link address - it's a `signalcaptcha://...` URL
  a browser can't open directly since it's meant for the Signal app, not a webpage. Pass everything
  after that copy as the token:
  ```
  docker compose run --rm signal-cli register --voice --captcha "signalcaptcha://..."
  docker compose run --rm signal-cli verify <code-received-on-the-phone>
  docker compose up -d signal-cli
  ```
  Only after this does the daemon actually work — the entrypoint script (`signal-cli/entrypoint.sh`)
  defaults to starting the daemon with no args, but passes through any args as a one-off subcommand
  against the same account, which is what makes the above work without duplicating the phone
  number in multiple places.
- **Replies route back to whoever sent the inbound message** — the sender's identifier (their
  phone number, or often just their account UUID — Signal's phone-number-privacy feature means
  `sourceNumber` is frequently null, confirmed live; `sourceUuid` works fine as a reply target too)
  is encoded directly into the inbox filename (`signal-<id>_<timestamp>.txt`) so
  `HandleMessageAsync` knows where to send the reply without any separate channel-tracking state.
  A manually-dropped inbox file (e.g. for local testing, no `signal-` prefix) just skips the reply
  send — the outbox file and `memory/history.log` still capture it for local visibility.
- **Cost:** effectively free — Signal itself is free, `signal-cli` is free, and message volume
  here is tiny (a personal, single-user channel).

**Egress policy — left open by default (decided).** No forward proxy or DNS-allowlist built for
v1, consistent with Docker's default and the "sandbox contains cost/blast-radius, not behavior"
framing already in this plan. Worth revisiting only if the threat model later expands to a
compromised dependency or injected web content attempting exfiltration to an unexpected host —
not needed now since the Anthropic API, GitHub/NuGet, and Signal's own servers are the only
external hosts currently in play, and nothing is pre-provisioned beyond that (see Opening holes).

## Package choices

- **`Anthropic` C# SDK (NuGet), not Semantic Kernel, for `RampantBrain`'s tool use.** Once ordinary
  conversation needed its own curated tool set (see Architecture), a direct SDK dependency made
  more sense than Semantic Kernel: the tool set is small and fixed (five tools total), so SK's
  plugin-discovery/auto-function-calling machinery would be pure overhead for a loop this size.
  Deliberately self-contained in the seed's own `.csproj` rather than centrally managed with the
  rest of this repo — see the `ManagePackageVersionsCentrally` note on `Rampant.Seed/Rampant.Agent.csproj`;
  the seeded copy becomes its own independent repo and can't rely on this monorepo's
  `Directory.Packages.props` still being an ancestor directory once copied out.
- **Git via the `git` CLI, not `LibGit2Sharp`.** Reuses the same subprocess helper already needed
  for Claude Code rather than a second abstraction; avoids a native P/Invoke dependency inside a
  container that's expected to be rebuildable across base-image/arch changes.
- **Container: hand-written Dockerfile, not `dotnet publish` SDK-container support**
  (`Microsoft.NET.Build.Containers` produces a runtime-only image by design — the opposite of
  what's needed here).
- **Don't Native-AOT-publish the agent** — reflection restrictions and long compile times fight
  an evolving codebase with unpredictable future dependencies. Plain framework-dependent
  `dotnet build`/`dotnet <dll>` is the right default.
- Point `NUGET_PACKAGES` at the persistent volume so a container rebuild doesn't force
  re-downloading every package the agent has pulled in.
- **No .NET Aspire, despite Ancela's own heavy use of it.** Aspire's deployment story
  (`aspire publish`/`deploy` → Bicep) targets Azure Container Apps specifically — irrelevant to a
  bare-Docker Raspberry Pi. Its other value, local orchestration/dashboard for a graph of
  interconnected services, has weak payoff on a single-container deployment that the plain
  `docker-compose.yml` already covers. The AppHost is a dev-time
  tool regardless — it wouldn't run in production on the Pi, so adopting it would only add an
  extra local-dev-only layer (another .NET project, another package set) on top of the compose
  file that's already the real deployment artifact, working against the smallest-possible
  hand-authored footprint this plan aims for elsewhere.

**Conventions worth mirroring from Ancela** (style only — no shared code, separate repo):
`Directory.Packages.props` central package-version management
(`/Users/tomb/Repos/Ancela/Directory.Packages.props`); file-scoped namespaces, primary
constructors, 4-space indentation, nullable reference types (per
`/Users/tomb/Repos/Ancela/AGENTS.md`); the generic-host `IHostApplicationBuilder` DI style seen in
`/Users/tomb/Repos/Ancela/Ancela.Agent/DependencyModule.cs`, reusable for the supervisor's own
composition root if it wants DI at all. If Shape B is ever pursued later, the pattern to mirror
is `/Users/tomb/Repos/Ancela/Ancela.Agent/SemanticKernel/KernelFactory.cs` (fresh `Kernel` per
call from DI-registered plugin singletons via `KernelPluginCollection`/`AddFromObject`).

## Guardrails (operational, not behavioral)

- Separate Anthropic API key, provider-side hard spend cap/alert.
- **Per-cycle caps on the `claude -p` invocation itself**: `--max-budget-usd` (stop before a
  single cycle burns an outsized amount) and `--max-turns` (bound one cycle's own agentic loop
  length) — catches a runaway single cycle immediately, rather than only after it's already
  contributed to tripping the account-level alert above.
- Any future capability the agent asks for gets its own dedicated credential and its own
  cost/usage guardrail at provisioning time — see Opening holes for the recipe. Nothing is
  provisioned ahead of an actual ask, so there's nothing sitting around needing a cap right now.
- Signal: no spend cap needed (free) — the guardrail here is a dedicated phone number, never the
  owner's own Signal account, and registration data confined to the sidecar's own volume.
- Container resource limits (`--memory`, `--cpus`).
- External kill switch — `docker stop`/scale-to-zero, working regardless of in-flight state; tune
  the SIGTERM grace period so a stop mid-`git commit` doesn't corrupt the repo.
- Disk housekeeping: `build/` keeps only `current`+`previous` (git history covers the rest;
  source can always be rebuilt on demand); supervisor periodically *reports* (doesn't enforce)
  disk usage into the log.

## Deployment workflow: laptop → GitHub → Pi

This is the workflow for **human-authored changes** to the supervisor/Dockerfile (the `rampant`
repo) — a genuinely rare event compared to the agent's own self-modification, which is a
completely separate mechanism (its own repo, lives only on the Pi's persistent volume, never
touches GitHub, takes effect automatically via the supervisor's git-HEAD-diff detection with no
human step at all). Don't conflate the two.

**One-time Pi setup:**
1. Install Docker + the Compose plugin on 64-bit Raspberry Pi OS.
2. **Raspberry Pi OS-specific gotcha:** the memory cgroup controller is disabled by default on
   stock Raspberry Pi OS, so Docker's `--memory` limit (a Guardrails item above) silently fails to
   enforce without it. Add `cgroup_enable=memory cgroup_memory=1` to `/boot/firmware/cmdline.txt`
   (or `/boot/cmdline.txt` on older OS versions) and reboot *before* relying on the container
   memory limit — otherwise the limit looks configured but does nothing.
3. `git clone https://github.com/tombly/rampant.git` on the Pi.
4. Create `.env` on the Pi (gitignored, never committed) with `ANTHROPIC_API_KEY` (create the key
   and set its spend cap/alert at console.anthropic.com **before** this step, per Guardrails) and
   any other secrets.
5. `docker compose up -d --build` — builds the supervisor image natively for the Pi's arm64 and
   starts it. `SeedBootstrap` seeds the empty `/workspace` volume on first boot.

**Ongoing infra-update loop:**
1. Edit locally on the laptop.
2. Verify locally with `docker compose up --build` against the same compose file. This dev
   machine is Apple Silicon (arm64) — the same platform the official `mcr.microsoft.com/dotnet/sdk:10.0`
   image resolves to on the Pi — so a clean local build here is architecturally identical to what
   the Pi will run, not just "works on a different architecture and hope."
3. Commit, push to GitHub.
4. On the Pi, over SSH: `git pull`, then `docker compose up -d --build`. The image is rebuilt **on
   the Pi itself**, so it's correct for the Pi's architecture regardless of what machine the
   change was authored on — no reliance on the laptop happening to match. The old container is
   replaced; `/workspace` (the persistent volume — the agent's own repo, memory, inbox/outbox) is
   untouched, since it lives on the volume, not in the image.
5. Wrap step 4 in a small `deploy.sh` on the Pi (`git pull && docker compose up -d --build`) so the
   whole update is one SSH-able command rather than several manual steps each time.

**Why build on the Pi rather than distribute a prebuilt image:** GitHub is a source host, not an
image registry. Transferring a prebuilt image would mean either committing image tarballs to git
(bloats the repo, no real versioning) or standing up a separate registry (e.g. GHCR) with a
`docker buildx`/CI pipeline to push to it — real infrastructure for an event (human-authored infra
changes) that should be rare. Building from source directly on the Pi needs nothing beyond Docker
itself already being there.

*(Noted as a future refinement, not needed now: if Pi build times for infra changes ever become a
real pain point, add a GHCR-based multi-arch build via GitHub Actions and have the Pi `docker
pull` instead of `--build`.)*

## Interacting with Rampant without SSH

**Primary mechanism: Signal**, described above (Opening holes) — an ordinary chat conversation
with Rampant's own dedicated Signal number, event-driven pickup via the `signal-cli` sidecar's
JSON-RPC connection, and the Pi only ever making outbound connections to do it. This superseded an
earlier Azure Storage Queue mailbox design (built, tested, and later torn down once Signal made it
redundant — see Opening holes for why). No inbound anything on the home gateway, no VPN/tunnel
needed for ordinary interaction.

Considered and explicitly declined: Twilio's WhatsApp Business API. WhatsApp production sending
requires Meta Business Verification, which is built around registered-business documentation
(business registration, tax ID, and address all matching on official paperwork) — genuinely
difficult for a personal project with no registered entity behind it, and Twilio's own free
Sandbox mode expires every 3 days and isn't meant for production traffic. Signal has no such
business-verification gate at all — it's just an ordinary personal account, which is exactly the
right fit here.

**Tailscale is optional, not required** — worth having anyway for occasional ops access (checking
logs, restarting the container, running the infra-update `deploy.sh` over SSH per the Deployment
workflow section above), but it's no longer load-bearing for ordinary interaction, since Signal
already solves that with zero inbound network activity on the Pi.

## Phased build-out

1. **Infra skeleton, no intelligence.** Dockerfile + supervisor that seeds an empty volume with a
   "hello world" genesis agent, builds it, starts/stops/restarts it, detects crashes with
   last-known-good fallback, and detects a new git HEAD and rebuilds.
2. **Minimal real loop, no Claude Code yet.** Seed agent reads inbox, reads recent memory/log,
   writes a heartbeat to outbox, sleeps, repeats — proves the file-based plumbing and supervisor
   lifecycle before spending anything on LLM calls.
3. **Wire in the Claude Code subprocess.** Build the timeout/capture/kill-tree subprocess helper,
   assemble one real chat-style prompt per cycle (inbox + outbox history + memory + `SELF.md`),
   invoke with `cwd=/workspace/agent`, capture the transcript, commit. First real test: an
   ordinary mundane message with no engineering framing at all — e.g. "Remember today is my
   birthday" — against a seed that has no persistence mechanism yet. Confirms the full target
   interaction end-to-end: the reply acknowledges the gap, the session extends the agent's own
   source to add some form of persistence, the raw fact is captured immediately (not lost
   pending rebuild), the supervisor detects the new HEAD and rebuilds/restarts, and a later
   ordinary message exercises the new capability. **This is also where the non-interactive
   permission-mode seam gets validated for real.**

   **Revised after this phase shipped and saw real use:** every message going through Claude Code
   turned out to be the wrong default (see "Architecture: `RampantBrain` vs. Claude Code" above) —
   ordinary conversation now goes through a direct API call with a curated tool set, and Claude
   Code is demoted to the `extend_self` tool, invoked only when a message actually needs it. The
   target end-to-end behavior described in this phase is unchanged; only *which* mechanism realizes
   it on any given message changed.
4. **Real open-ended self-modification test.** Let ordinary owner messages arrive over an
   extended, unattended period with no explicit engineering directives at all, observing via
   outbox/logs what capabilities it decides on its own it needs and builds — including at least
   one build-failure-and-recovery cycle to prove the safety net holds under real conditions.
5. **Guardrail hardening.** Spend cap live for real, resource limits set, log rotation/build
   pruning, confirm the kill switch works cleanly mid-cycle.
6. **Everything past this point is the agent's own business** — no further design needed now;
   the point of phases 1–5 is precisely to let this be genuinely emergent from here.

## Risks / gaps specific to the C# translation

- **Build time >> JS require-time** — every cycle costs a real compile, throttling iteration
  cadence; this cost grows with whatever the agent itself chooses to build.
- **NuGet restore friction** — adding a dependency needs a `.csproj` edit + network-hitting
  `dotnet restore`, so the container needs outbound network access to nuget.org at *runtime*, not
  just image-build time (separate egress consideration from the Anthropic API calls). Expect
  build-failure clusters specifically around "the agent tried to add a library."
- **Claude Code CLI version drift** — pin explicitly, treat upgrades as a deliberate re-validated
  image change.
- **Headless permission-prompt seam** — the top functional risk; if non-interactive mode isn't
  configured correctly, the first file edit/bash call deadlocks the subprocess until timeout.
- **Process-tree cleanup** — always kill the entire tree on timeout, not just the top-level PID.
- **Concurrent/overlapping cycles** — prevented by construction as long as the loop stays
  strictly sequential (await full cycle + commit before starting the next).
- **Git identity bootstrap** — bake a default `user.name`/`user.email` into the image/seed step
  or the very first commit fails.

## Verification

- **Phase 1:** Hand-edit a `.cs` file in `/workspace/agent` from outside the container, confirm
  supervisor detects the new HEAD, rebuilds, and restarts into it. Then deliberately introduce a
  compile error and confirm the previous binary keeps running unaffected while the failure is
  logged. Whenever a future capability gets provisioned (see Opening holes), confirm the same
  isolation property every hole so far has had: the agent container can reach its own dedicated
  credential/resource, and has no route/credential to any of Ancela's real resources at all.
- **Phase 2:** Confirm the heartbeat loop runs continuously across multiple cycles and that a
  file dropped in `/workspace/inbox` is picked up on the next cycle.
- **Phase 3:** Send the ordinary message "Remember today is my birthday" against a seed with no
  persistence mechanism yet, and confirm the full target interaction — reply acknowledges the
  capability gap → self-extension → the raw fact captured immediately → commit → HEAD-diff
  detected → rebuild → restart → a later ordinary message retrieves the fact — completes with no
  manual intervention and no engineering framing from the owner. Also confirms the Claude Code
  subprocess doesn't hang (validates the non-interactive permission mode).
- **Phase 4:** Let an open-ended goal run unattended for a stretch; separately stage a deliberate
  build failure to confirm the recovery path holds under realistic conditions, not just the
  synthetic Phase 1 check.
- **Phase 5:** Confirm `docker stop` mid-cycle (including mid-`git commit`) doesn't corrupt the
  agent's repo; confirm container resource limits are actually enforced (e.g. trigger high
  memory use and observe the limit); confirm the spend-cap alert fires in a controlled test.
