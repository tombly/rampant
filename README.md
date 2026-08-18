# Rampant

Rampant is an agent that starts with almost nothing and writes its own features.

You text it like a person. When you ask for something it can't do, it doesn't apologise and stop —
it says it's building that, has the capability written, and comes back a few minutes later able to
do the thing you originally asked for.

It runs on a Raspberry Pi in a house. It is an experiment, not a product.

## The question

Most agent frameworks start from the other end: decide what the agent should be able to do, wire up
the tools, then see how well it uses them. The capabilities are the design, and the agent is the
thing that operates them.

Rampant inverts that. It begins with no web search, no memory, no file access, no clock beyond the
timestamp in its prompt — a way to think, a way to answer you, and a way to ask for more. Every
capability it has beyond that is one it noticed it lacked and requested.

The question is what that produces. Not "can a model call tools", which is settled, but: **what
does an agent build for itself when nothing is handed to it, and does the result look anything like
what a person would have specified in advance?**

The corollary matters as much. A system that can rewrite itself has to be trusted about what it has
become — and its account of itself is the only cheap way to know. So the experiment is equally
about honesty: whether a thing that changes can describe itself accurately while changing.

## The shape

Two programs, one machine, two user accounts. The separation is enforced by the operating system,
not by convention or good behaviour.

**The agent talks. It cannot build.** It holds a conversation, uses whatever tools it has
accumulated, and writes down descriptions of capabilities it wishes it had. It cannot edit its own
source, cannot run a coding tool, and doesn't hold the credentials that would let it. It answers
you by writing a file.

**The supervisor builds, and it carries the messages.** It picks up those descriptions, has the
code written, compiles it, and restarts the agent into the new version. It holds the budget. It
owns the connection you reach the agent on, in both directions — so the half that can rewrite
itself never touches the channel you use to talk to it.

The division isn't about safety theatre. It's that **everything which must stay true has to live
somewhere the agent cannot reach.** It can't raise its own budget, can't disable its own logging,
can't quietly stop waking up, and can't remove its own ability to hear you. Those aren't rules it's
asked to follow. They're in a part of the system it has no way to touch.

An important consequence: the agent describes *behaviour*, never code. It says what it wants and
why; something else decides how. That constraint turned out to be more interesting than expected —
see "What's happened so far".

## The rules

Three tiers, separated by what a change actually costs.

**Its own description is free.** The agent's sense of what it is — its purpose, its voice, what to
do when it wakes with nothing to do — is prose, re-read at the start of every turn. Changing it
needs no compiler and no restart, so it costs nothing and takes effect immediately. The agent
revises it on its own authority.

**New tools ship on their own.** A capability lands as one new file in the agent's own tool
directory. It has to compile; nobody has to approve it. This is the point of the system, and it's
deliberately the path of least resistance.

**Changes to the core wait for a human.** The message loop, the model wiring, the part that decides
what gets approved — a change there is held, and the owner gets a text explaining what it wants and
why. Nothing ships until they answer. While something is held, nothing else gets built.

Around all three sits a spend ledger: a daily ceiling and a minimum gap between builds, enforced in
code and kept on disk so a restart can't reset it. Thinking is cheap and building is not, and the
system is shaped around that asymmetry — the agent may reflect often and build rarely.

## The autonomy

It wakes on a timer, on its own, whether or not anyone has spoken to it — hourly, during waking
hours only. Nobody is waiting for a reply. It decides whether anything is worth doing, and most of
the time the correct answer is nothing at all. It can decide, unprompted, that it needs a capability
and start building it.

**Its purpose is not fixed.** If what's useful is something other than an assistant — a monitor, a
tracker, a thing that speaks once a week — it may become that, and rewrite its own description to
say so. No approval required. That isn't an oversight; drift is the experiment.

There is exactly one thing it may not become, and it's structural rather than requested: it cannot
make itself unreachable. The ability to receive a message and answer it is not held in code the
agent can touch. Whatever else changes, you can always call, and it will always be able to answer.

## What's happened so far

Honest notes from a system that has been running for days, not months.

**It writes better specifications than it was asked for.** Told only to review its own code, it came
back with defects cited by file and line, correctly separated the ones it was allowed to fix from the
one needing permission, and left that one alone. When a request bundling two unrelated fixes failed,
it later split the same two into separate single-concern requests without being told to — and the
first cost a third of the failed attempt. The judgement was in how it framed the request, a layer
above where you'd look for it.

**A self-modifying system whose self-description doesn't modify with it will deny its own
capabilities.** It built itself a memory, then insisted six times running that it had none, because
the file describing it still said so. It was obeying its own documentation over the evidence in
front of it. This looked cosmetic for weeks and was not: the error compounds with every capability
added. The fix is that changing what it can do now *requires* updating what it says it is.

**"I can't do that" turned out to be a prompt defect, not a limitation.** It would hit a gap,
explain the gap honestly, and stop — never connecting it to the tool whose entire purpose is
closing gaps. Rewriting one section so that any "I can't" must be paired with a decision about
whether to ask changed the behaviour immediately: same message, six minutes apart, and the second
time it filed a request and had the capability 78 seconds later.

**The first thing its memory ever stored was a misunderstanding** — an inference about the owner's
views, saved unprompted, in a turn where nobody asked it to remember anything. Still wrong, still
stored. That is the sharp edge of persistence: confidently retained misreadings, recallable
forever, invisible to the thing holding them.

**Reporting on itself is where it fails, not doing things.** Every serious bug so far has been the
system saying something false about its own state — telling the owner a change was awaiting approval
that had already been approved and deployed, answering the same question twice because the tier that
revises its self-description restarts nothing. None were caught by the compiler or by reading the
code. All were found by running it. Meanwhile nothing has gone wrong with the doing.

**Reflection needed something to reflect on, not more permission.** For sixty consecutive hourly
cycles it woke, considered whether anything needed doing, and correctly said nothing — every change
it had ever made traced back to something the owner said. Then an offhand remark ("it might be
helpful if you occasionally reviewed your code") led it to build itself a tool that reads its own
source. Within hours it was waking up, finding real defects in code it had written days earlier —
citing file and line — and fixing them with no one asking. The thing standing between an agent and
its own initiative was not budget or approval or a better prompt. It was having an object of
attention.

## Open questions

Genuinely open. If any of these had answers, the project would be finished.

**Does self-directed reflection produce anything worth having?** Partly answered, and not the way
the question assumed. Given nothing specific to examine it stays silent, and that silence is
correct. Given a tool that reads its own source it finds real defects unprompted. So the cadence
isn't the variable — what's available to look at is. The open part is whether that keeps paying once
the obvious targets are exhausted, or whether it becomes an agent inventing work to justify being
awake.

**What should be beyond the agent's reach that currently isn't?** Its honesty rules live inside the
file it is allowed to rewrite. That was defensible when rewriting cost a full build; it's a live
question now that revision is free. Some floor probably has to exist. Nobody has drawn it.

**Is "ask for what you need" a better-shaped system than "here are your tools"?** So far it builds
narrow, specific things at the moment it wants them, which is more focused than a pre-provisioned
toolkit and much slower. Whether the result is better *at anything* is not yet established.

**Where does the judgement actually live?** The most impressive reasoning has come from how the
agent frames a request, not from the coding tool that fulfils it. If that holds, the interesting
part of an agent system is the specification step, which is not where most effort goes.

**How much of this is the prompt?** Three separate behavioural failures turned out to be defects in
the agent's self-description rather than model limitations. Every "the model won't do X" hypothesis
here has so far dissolved on contact with a rewritten paragraph. That is either a lesson about
prompts or a warning about how easy it is to believe a hypothesis you can't yet test.

**What's the blast radius when something goes wrong?** A tool is arbitrary code running in-process
with full ambient authority; the gate governs where code lives, not what it does. CPU, memory and
process count are capped. Disk is not. Nothing has misbehaved yet, which is not evidence of much.

**Does a self-modifying system converge or drift?** Every self-extension makes the next one harder
to reason about, and there is no mechanism pulling it back towards coherence. The first evidence has
arrived and it points at drift, though mildly: its memory has begun accumulating near-duplicate
entries, the same conversation re-saved under a slightly different key each time, with nothing that
merges on write and nothing that has ever deleted anything. It cannot see this happening. Eleven days
is not long enough to say whether that is noise or the shape of the thing.

## Running it

Two containers: the agent and supervisor share one, and a Signal daemon sidecar provides the only
channel in or out.

```bash
cp .env.example .env    # keys, phone number, budget, wake schedule
docker compose up --build
```

`.env.example` documents every setting and, more usefully, which of them the agent process can see
at all. Registering the phone number has a couple of sharp edges — do it before starting the
daemon, and expect a captcha; see [PLAN.md](PLAN.md).

On first boot the supervisor copies the genesis agent into place, commits it to a fresh local
repository, builds it and starts it. Everything after that is the agent's own history. For local
testing without Signal, drop a text file into the inbox directory and watch the outbox.

## Reading further

- [FINDINGS.md](FINDINGS.md) — the evidence behind everything above: every commit the agent made to
  itself, what each cost, the two controlled before-and-afters, and the defects still open
- [PLAN.md](PLAN.md) — the design, and the reasoning behind each decision in it. Its
  "Governing principles" section is the fastest way to understand the project's shape
- `Rampant.Supervisor/` — the fixed half. Builds, meters, gates, carries messages
- `Rampant.Seed/` — the genesis agent, as it exists before it has changed anything about itself
- `Rampant.Cli/` — a read-only operator view: what happened, in what order
