# Rampant

A sandboxed, self-modifying autonomous agent, written in C#/.NET, that rewrites its own source
code in response to ordinary conversational requests. See [PLAN.md](PLAN.md) for the full design,
architecture, and rationale.

## Quick start (local)

```bash
cp .env.example .env
# edit .env and set ANTHROPIC_API_KEY (create one at console.anthropic.com, set a spend cap first)
# and SIGNAL_PHONE_NUMBER (a number dedicated to Rampant - see .env.example for why)

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
- `rampant` - the supervisor + agent workspace (see `Dockerfile`)
- `signal-cli` - the Signal JSON-RPC daemon sidecar (see `signal-cli/Dockerfile`), Rampant's only
  interaction channel, now already registered from the steps above

On first boot, the supervisor seeds `/workspace/agent` with a minimal genesis agent (from `Rampant.Seed/`),
commits it to a fresh local git repo, builds it, and starts running it. Message Rampant's Signal
number, or (for local testing without Signal registered) drop a message in `/workspace/inbox`
(bind-mounted locally) and watch `/workspace/outbox` for a reply.

## Repo layout

- `Rampant.Supervisor/` - the fixed, human-owned piece: detects self-edits, rebuilds, restarts,
  falls back to the last-known-good binary on a bad edit. Never modified by the agent.
- `Rampant.Seed/` - the genesis template for the agent's own repo. Copied to `/workspace/agent` on first
  boot only; everything after that is the agent's own business.
- `signal-cli/` - the Signal daemon sidecar's Dockerfile + entrypoint. Human-owned, like the
  supervisor - not part of the agent's own mutable repo.
- `PLAN.md` - the full design document.

## Deploying to the Raspberry Pi

See PLAN.md -> "Deployment workflow: laptop → GitHub → Pi" for the full one-time setup and
ongoing update loop.
