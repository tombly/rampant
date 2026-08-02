# Rampant

Rampant is an agent that writes its own features.

You text it like a person and it answers. When you ask for something it can't do, it doesn't just
apologise — it says "I can't do that yet, let me get back to you," has the capability built, and
comes back a few minutes later able to do the thing you originally asked for.

See [PLAN-V2.md](PLAN-V2.md) for the current design and [PLAN.md](PLAN.md) for how it got there.

## How it works

**Two programs share one box.** The *agent* is the part you talk to. The *supervisor* is the part
that builds. They run as two different user accounts on the same machine, which is what keeps them
genuinely separate rather than separate by convention.

**The agent thinks; it doesn't build, and it doesn't hold the phone line.** It can hold a
conversation, use whatever tools it has, and write down a description of a capability it wishes it
had. That's the extent of it. It cannot edit its own code, cannot run the coding tool, and doesn't
even hold the credentials that would let it. It answers you by writing a file.

**The supervisor builds, and it carries the messages.** It watches for those requests, runs a
coding tool to write the feature, rebuilds the agent, and restarts it. It holds the purse strings —
a daily budget and a minimum gap between builds, enforced no matter what the agent thinks it needs.
And it owns the Signal connection in both directions, so the half that can rewrite itself never
touches the channel you reach it on.

**Why bother splitting them?** Because the agent is allowed to change a great deal about itself,
and everything that must *stay* true has to sit somewhere it can't reach. It can't quietly raise
its own budget, can't disable its own logging, and can't remove its own ability to hear you. Those
aren't rules it's asked to follow — they're in a part of the system it has no way to touch.

**It starts with almost nothing.** No web search, no memory, no file access. Just a way to think, a
way to talk to you, and a way to ask for more. Every capability it ends up with is one it noticed
it needed and asked for. That's the whole experiment: we want to know what it builds when nothing
is handed to it.

**It runs at two speeds.** When you text it, it answers straight away. Left alone, it wakes once an
hour to consider whether anything needs doing. Most hours it should do nothing at all. That hourly
pace is a deliberate brake: thinking is cheap, building is not.

**You approve the changes that matter.** New tools go live on their own — that's the point. But if
it wants to change its own core, that gets held and you get a text explaining what it wants and
why. Nothing ships until you reply.

**What it can't do:** reach you through any channel but Signal, spend beyond its daily budget,
change its own source directly, or make itself unreachable. What it *can* do is grow into something
quite unlike what it started as — including something that isn't really an assistant anymore.
That's allowed. The one thing it must always be able to do is answer when you call.

## Quick start (local)

```bash
cp .env.example .env
# At minimum set OPENAI_API_KEY (what the agent thinks with), ANTHROPIC_API_KEY (what the
# supervisor builds with - use a dedicated key with a hard monthly cap), SIGNAL_PHONE_NUMBER and
# RAMPANT_OWNER_SIGNAL_ID. See .env.example, which explains every knob and which of them reach
# the agent process at all.

# Register the phone number BEFORE starting the daemon - registering while it's already running
# races it for the same account-data lock and can silently fail (confirmed live). If you ever need
# to re-register with the daemon already up, `docker compose stop signal-cli` first.
docker compose build signal-cli
docker compose run --rm signal-cli register --voice   # or omit --voice for an SMS code
# Signal will likely demand a captcha before sending the code:
#   Failed to register: Captcha required for verification, use --captcha CAPTCHA
# Get one at https://signalcaptchas.org/registration/generate.html, solve it, then right-click
# (don't click) the "Open Signal" link and copy its address - a signalcaptcha://... URL a browser
# can't open directly. Retry with it:
#   docker compose run --rm signal-cli register --voice --captcha "signalcaptcha://..."
docker compose run --rm signal-cli verify <code-received-on-the-phone>

docker compose up --build
```

This starts two containers:

- `rampant` — supervisor and agent, one container, two users (see `Dockerfile`,
  `docker-entrypoint.sh`)
- `signal-cli` — the Signal JSON-RPC daemon sidecar (see `signal-cli/Dockerfile`), Rampant's only
  interaction channel

On first boot the supervisor seeds `/workspace/agent` from `Rampant.Seed/`, commits it to a fresh
local git repo, builds it, and starts it. Message Rampant's Signal number — or, for local testing
without Signal registered, drop a plain text file in `/workspace/inbox` (bind-mounted at
`./local-workspace/`) and watch `/workspace/outbox`.

## Repo layout

- `Rampant.Supervisor/` — the fixed, human-owned half. Builds, deploys, meters spend, owns the
  Signal socket, gates core changes behind your approval. Never modified by the agent; its compiled
  binary lives root-owned in `/opt`, outside the volume.
- `Rampant.Seed/` — the genesis agent. Copied to `/workspace/agent` on first boot only; everything
  after that is the agent's own business. `Tools/` is empty on purpose.
- `Rampant.Cli/` — `rampant log`, a read-only operator view that interleaves every log source into
  one "what happened, in what order" scan.
- `signal-cli/` — the Signal daemon sidecar. Human-owned, like the supervisor.
- `PLAN-V2.md` / `PLAN.md` — the design, and the reasoning behind every decision in it.

## Deploying to the Raspberry Pi

See PLAN.md -> "Deployment workflow: laptop → GitHub → Pi".
