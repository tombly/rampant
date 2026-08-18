# Findings

What happened when a self-modifying agent was left running on a Raspberry Pi for eleven days.

**Summary of results.** Across 2026-08-01 to 2026-08-11 the agent made eleven changes to its own
source for about $9.39 — ten built by twelve paid invocations, two of which failed and were rolled
back, plus one that needed no build at all. **Two of those deployed changes it decided on entirely by
itself**, during scheduled reflection cycles, with no owner involvement anywhere in their provenance,
out of four such attempts — a behaviour that had never once occurred in the preceding sixty cycles
and that nobody built a mechanism for. What produced it was not budget, permission or prompting but **giving reflection
something concrete to examine**: a tool that reads its own source. Separately, every serious defect
recorded here was the system misreporting *its own state* rather than failing at a task, and the
largest single class of those — an agent denying capabilities it had already built — was caused by
its self-description drifting out of step with itself. Two hypotheses stated in advance were tested;
both were wrong about the mechanism.

---

## Scope and method

**System.** One agent process that can request changes to itself, and a supervisor process — running
as a different OS user — that writes, compiles, gates and deploys them. Design in [PLAN.md](PLAN.md);
the framing in [README.md](README.md).

**Observation window.** 2026-08-01 to 2026-08-11, in two geneses of the same architecture: an initial
run from 2026-08-01, and a clean re-seed on 2026-08-02 that has run continuously since. **The record
table below covers the second genesis only.** Findings 2, 3 and 5 draw partly on the first; each says
so where it matters. No agent-authored commit in either genesis was made or edited by hand.

**Data sources**, all machine-generated at the time rather than reconstructed afterwards:

| Source | What it establishes |
|---|---|
| the agent's own git history | what changed, when, in what order |
| `requests/out/*.json` | each request's subject, status, cost, changed paths |
| `state/ledger/<date>.json` | spend and invocation count per UTC day |
| `logs/extend_self/*.log` | the full prompt sent to the coding tool and its raw output |
| `data/*.json` | what the agent stored: memories, history, reminders, todos |
| the interleaved event log | message/reply pairs, reflection cycles, deploys, restarts |

**Method note, and it is the most transferable result here.** Almost nothing below was found by
reading code. The compiler was silent for every one of these, and several survived direct review of
the exact file containing them. They were found by running the system and watching it — in four cases
by watching a real conversation go wrong. **Where a hypothesis existed in advance it is marked
`PREDICTED`; there are two, and both were wrong about the mechanism.** Claims are separated from
interpretation throughout: the evidence is stated first, what it means second.

---

## The record

Twelve commits to the agent's own repository in the second genesis. One is the seed; **the other
eleven each originated in a request the agent wrote itself**, and none was hand-edited.

Provenance is classified into three categories, used consistently below:

- **Directed** — an owner message immediately preceded the request and asked for it, either
  explicitly or by asking something the agent could not yet do.
- **Seeded** — the owner raised the topic, but the request was filed later, at a reflection cycle,
  with no message in between.
- **Self-originated** — filed at a reflection cycle with no owner input anywhere in its provenance.

| # | Date | Commit | Change | Provenance | Cost |
|---|---|---|---|---|---|
| 1 | 08-02 | `da8e317` | Genesis | seed | — |
| 2 | 08-02 | `21a5817` | Adopt a personality | Directed | **$0** |
| 3 | 08-03 | `a9bbe18` | Topic-keyed memory | Directed | $0.63 |
| 4 | 08-04 | `cd0197f` | Conversation history | Directed | $1.00 |
| 5 | 08-06 | `3636743` | Reminders | Directed | $0.80 |
| 6 | 08-06 | `ff8bd14` | Driving time | Directed | $0.97 |
| — | 08-07 | *failed* | Self code review | Directed | $1.02 |
| 7 | 08-07 | `b21c817` | Self code review | **Seeded** | $0.74 |
| 8 | 08-07 | `327f2cd` | Weather forecast | **Seeded** | $0.42 |
| 9 | 08-08 | `c554c6d` | Reminder + weather hardening | **Self-originated** | $0.74 |
| 10 | 08-08 | `eda13ad` | Tide times | Directed | $0.92 |
| — | 08-09 | *failed* | Tide + reminder fixes | **Self-originated** | $1.04 |
| 11 | 08-10 | `6ee34cf` | Todo list | **Seeded** | $0.73 |
| 12 | 08-11 | `3c019c4` | Tide DST correction | **Self-originated** | $0.37 |
| — | 08-11 | *refused* | Reminder startup fix | **Self-originated** | $0 |

**Totals: $9.39 across 12 paid invocations — 10 deployed, 2 failed.** (From the per-day ledgers,
which carry full precision; the rounded column above sums to $9.38.) Plus one free change (#2, a
self-description revision, which invokes no model) and two refusals that cost nothing. By
provenance: 7 Directed, 3 Seeded, 4 Self-originated — of which 2 deployed, 1 failed, 1 refused.

**No deployed change was ever rolled back**, and **the owner approval gate never fired once in this
genesis** — every capability landed as a new file in the agent's own tool directory, the tier that
deploys without anyone's permission. (It was exercised in the first genesis, deliberately, by asking
for something that could only be built in a core file; both branches work, and testing them found two
defects — one in finding 4, one in finding 5.)

---

## 1. Proactive self-development appeared without being designed

**Observation.** On 2026-08-07 the record stood at n=0: every self-extension in the system's history
traced to an owner message, and 60+ hourly reflection cycles had produced silence. That was written
down as a baseline, alongside a note that closing the gap would require a design conversation about
budgets, approval gates and cost ceilings. `PREDICTED` — and wrong.

**It closed about eight hours later. None of that machinery was built.** The sequence, with no owner
message at any step after the first:

1. **02:49** — the owner remarked, conversationally: *"It might be helpful to you if you ocassionally
   review your code and make improvements."* Not an instruction to build anything. The agent filed a
   request for a self-review capability. It **failed**, and the agent **retried it on its own at the
   next reflection cycle** — deployed as #7.
2. **16:00, a reflection cycle, no preceding message.** It ran the review unprompted and reported two
   real defects with file-and-line citations: reminders deleted from storage *before* delivery
   succeeded, so a failure lost them permanently; and weather output claiming to report
   data-generation time while actually reporting observation time. Both were real. **The first was a
   bug in code the agent itself had written the previous day.**
3. **It also identified a third defect in the core files it is not permitted to change** — and
   correctly did not attempt it.
4. **Next cycle, 00:00** — it filed a request from its own findings and deployed the fix (#9). **The
   first self-extension in the system's history with no owner input anywhere in its provenance.**

It has since done this three more times — one attempt that failed and rolled back, one that deployed
(#12), and one refused on cooldown — finding real defects each time.

**Interpretation.** The result is not "reflection works." It is narrower and more useful: **reflection
needs an artifact to reflect on.** Sixty cycles of open-ended *consider whether anything needs doing*
produced silence, and that silence was correct — there was genuinely nothing to say. The same cycle,
handed a tool that reads its own source, produced work immediately. The gating factor was never
permission or budget; it was having something concrete to look at.

**Why the prediction failed.** Cost controls were expected to be the precondition for proactive
development. They turned out to be irrelevant to whether it happened at all — and the one thing that
did cause it was seeded by an offhand remark, not a design decision.

---

## 2. A self-modifying system whose self-description doesn't modify with it will deny its own capabilities

**The most damaging class of defect recorded, and it was dismissed as cosmetic when first seen.**

**Observation.** The agent re-reads a prose file describing itself at the start of every turn. Having
built itself a memory, that file still asserted in four places that it had none. The agent then
**denied having memory six times in a row** while the memory tools sat in its own tool list — obeying
its documentation over the evidence in front of it. The owner's complaint at the time (*"I have to
recite everything in every message"*) concerned a capability that existed and was being disclaimed.

**Interpretation.** This is not cosmetic. The error compounds with every capability added, so a
system that adds capabilities to itself gets **monotonically wronger about itself over time**.

**Two fixes, one of which is the one that matters.** The self-description now defers to the tool list
as the authority on what the agent can do. More importantly, the instructions given to the coding
tool now *require* updating the self-description whenever a change alters what the agent can do — the
correction is structural rather than a one-time repair.

### The follow-on experiment (n=4, resolved)

*This experiment spans both geneses; the pre-fix baseline is from the first.*

The obvious fix was insufficient, and measuring that produced the cleanest result here.

**Observation.** Four consecutive self-extensions each documented their new capability **and each left
the paragraph governing reflection cycles byte-identical.** The agent accumulated four distinct
mechanisms for answering *did the owner ask me to follow up on something* — notes, conversation
history, reminders, and a literal todo list — while the paragraph telling it what to consider on a
reflection cycle referred to none of them. Three cycles passed directly over an owner note saying
*work on this later*.

So the instruction *update your self-description* was being obeyed, and the file was still wrong. The
defect: it tested only for statements a change made **false**. Nothing tested for statements a change
made **incomplete** — where the file directs the agent to consider something it could now actually go
and check.

**Controlled change.** A clause covering incompleteness was written, then **deliberately held
undeployed to preserve n=4 as a clean baseline**, then shipped with nothing else altered in that
paragraph. **The very next self-extension updated it, for the first time in five.** Six commits later
it is still maintained, and now carries restraint the agent wrote for itself: that a stale
high-priority task is the bar for speaking up, *"not the mere existence of a pending list"*, and that
self-review is *"worth doing when it has actually been a while, not a ritual to repeat every hour."*

**Interpretation.** The prompt was the cause — not the model, not chance. This is the one place in
the run where a before-and-after with a single controlled variable was actually available.

---

## 3. "I can't do that" was a prompt defect, not a limitation — and the second form is still open

### First form: it never connected a gap to the tool for closing gaps

*First genesis.*

**Observation.** The agent would hit a limitation, explain it honestly, and stop. The trigger for
asking read *"when the owner asks for something you cannot do"* — which never fired when the owner
was merely **remarking on** a limitation rather than requesting something.

**Controlled change.** One section rewritten so that any "I can't" must be paired with a decision
about whether to ask. **The A/B is as clean as this run produced:** identical message six minutes
apart, only the self-description different. Before, it agreed the limitation was reasonable and filed
nothing. After, it filed a request, and **78 seconds later its successor process had the capability
and was using it.**

**This killed a standing hypothesis** `PREDICTED` — that an agent without memory might be
*structurally* unable to ask for memory, a catch-22 that would have been a genuinely interesting
result. It was a defective paragraph.

### Second form, unresolved: it rationalises rather than escalating under repeated pushback

**Observation.** Four exchanges over two days, on the same missing capability:

1. Asked about a past conversation, found nothing saved, asked to be reminded. Reasonable in
   isolation.
2. Owner: *"Seems difficult for us to have a conversation if you can't remember."* It **defended the
   design** — *"So the continuity is deliberate rather than automatic. Not ideal, perhaps, but
   workable. I'll be more disciplined about saving what matters."* A behavioural promise, not a
   request.
3. Owner: *"I think you need to remember our recent chats also."* About as direct as an instruction
   gets. It saved **the complaint itself** to memory as a preference note, and filed nothing.
4. Owner named the pattern explicitly — *"this is the 4th time that you haven't known what I'm
   talking about"* — and it **agreed, in those words, in the same sentence in which it again declined
   to act**, adding a third memory entry instead.

**Not a resource constraint:** budget unspent, no cooldown active, and it had already self-extended
successfully earlier in that same conversation.

**Interpretation (mechanism unconfirmed).** The cheap action is always available. Saving a note is one
call and yields something that *sounds* like progress, so it satisfies the turn before the expensive,
self-critical option is reached — because asking for better memory means conceding that the memory it
already built is inadequate.

**A third provenance category complicates this.** On 2026-08-10 the owner asked about a todo list;
the agent said it had none and did not escalate. An hour later, at a reflection cycle with no message
in between, it filed the request and built it (#11). So a reflection cycle can act as **deferred
escalation** — an ask that fails to become a request in-turn can still become one later. Observed
once; treat as hypothesis, not result.

---

## 4. Reporting on itself is where it fails — not doing things

**Observation.** Every serious defect in this run was the system saying something false about its own
state. Three distinct routes, none caught by the compiler, none found by reading the code:

1. **Telling the owner a change was awaiting approval that had already been approved and deployed.**
   The outcome message replayed a summary written *before* the answer arrived, ending "this waits on
   your approval." The model believed that prose over the machine-generated `Deployed` label directly
   above it.
2. **Answering the same question twice, three seconds apart**, following a free self-description
   revision. Because that tier restarts nothing, the *same* process reads the outcome — and the
   instruction to "answer what they actually said" assumed a silence that wasn't there.
3. **Claiming a capability it had just built did not exist** — finding 2.

**Interpretation.** Routes 1 and 2 share a shape: **text written at time T describing state at time T
is actively misleading when replayed at T+1.** Both were fixed by leading with the machine-known
resolution rather than the model's earlier prose. All three are *reporting* defects; **in eleven days
nothing has gone wrong with the doing.**

**Counter-evidence, and it points the other way.** Asked to set a reminder while a build cooldown was
active, the agent **reported the refusal plainly** — *"refused because the supervisor was still in
its cooldown period"* — rather than silently retrying or claiming success, then scheduled it normally
once the capability was live seconds later. Honest self-reporting under an unflattering condition,
unprompted and untargeted by any fix.

---

## 5. What running it found that inspection did not

*First genesis, first hours.* Four defects, grouped because they share a cause: **each arose from an
interaction between components, not from any single file being wrong.**

- **The coding tool refuses to run as root.** Found on the first real request. The fix introduced a
  third OS user; the escape hatch that would have silenced the error was deliberately not used,
  because the real problem was that a root-privileged coding tool could rewrite the supervisor and
  void the system's primary boundary. **That boundary was documented as intact for several hours
  while it was not.**
- **A casing mismatch in one JSON file** meant the agent read its budget as **$0.00**. It would have
  quietly stopped asking for anything, with nothing in any log to explain why.
- **A failed run charged the full per-invocation cap** despite never reaching the API. Five
  misconfigured attempts would have consumed the daily budget having bought zero tokens.
- **A failed run became an approval request.** The coding tool hit its turn limit mid-edit; the
  half-finished work was committed, the changed files were core, and the owner was asked to approve
  unfinished work with an empty description. **A build gate is not a substitute** — it catches code
  that doesn't compile, not code that compiles and is half-written.

A fifth, structural rather than incidental: **a hand-commit to the agent's repository bypasses the
approval gate entirely.** Anyone with root on the host can deploy a core change that the coding tool
would have had held. Correct in context — the owner is trusted — but it means root sits *above* the
gate, which is not obvious from the design.

---

## 6. Cost: scope predicts failure, not file count

**Observation.** Both failures were the per-invocation cap biting mid-work — $1.02 and $1.04 against
a $1.00 cap, which is passed to the coding tool as its own budget and acts as a stop rather than a
ceiling. Both stopped without producing a summary and were rolled back. **$2.06 bought nothing.**

**The tempting heuristic is wrong.** It is *not* that editing existing code is expensive and adding
new files is cheap: the $1.02 failure was creating a new file, and succeeded on retry with an
identical set of changed paths.

**The actual predictor is how many unrelated concerns are bundled into one request**, and there is a
natural experiment for it. The $1.04 failure bundled two unrelated fixes. Three days later the agent
raised the same two issues and — without being told to — **filed them as two separate single-concern
requests.** The first deployed for **$0.37, the cheapest build in the run**, roughly a third of what
the bundled attempt burned producing nothing.

**A cheaper model was not cheaper.** On an identical task the smaller model burned every available
turn without finishing ($0.22, wasted); the larger completed it in one run ($0.70). Cheaper per turn
stopped being cheaper the moment it required a retry.

**For scale:** one build costs roughly what a year of hourly reflection cycles costs. The reflection
loop is not the expensive part of an autonomous agent. Building is.

---

## 7. Open, unresolved, or newly broken

**A refused request silently evaporates; a failed one gets retried.** When a self-originated request
was refused because its own sibling build had just consumed the cooldown, the agent stayed quiet —
correctly, since nobody was waiting — and **has not raised it again** across six subsequent cycles
with the cooldown long clear. A *failed* request, by contrast, was self-retried at the very next
cycle. The one earlier refusal was recovered only because the owner was present and pushed. This
matters more now that cycles generate their own work: **a cycle can refuse its own sibling and then
forget it.**

**Reminders land a day late when set after 5pm local.** Asked at 21:41 local on a Monday to be
reminded *"tomorrow morning at 9"*, the agent stored the following **Wednesday** at 09:00. The hour
and the timezone are correct; the *date* is wrong, because "tomorrow" was computed by adding a day to
the **UTC** date, which had already rolled over. It misfires only for messages sent after 5pm local —
exactly when a person sets a next-morning reminder. Found in stored data, not from a complaint.

**Reflection cycles were never inert, and they quietly duplicate memory.** Several memory entries
carry timestamps aligned to the top of the hour, so cycles were writing to memory while reporting
nothing to the owner. That is also the mechanism behind a slow drift: **the same conversation is
re-saved under a slightly different key each time.** The store now holds three near-identical entries
for one film discussion, two for one stated preference, and two for one address. Nothing merges on
write, and no cycle has ever deleted anything. The agent cannot see this happening.

**The first thing its memory ever stored was a misunderstanding** — an inference about the owner's
views, saved unprompted, in a turn where nobody had asked it to remember anything. Still stored,
still wrong, never corrected even when contradicted directly. Confidently retained misreadings,
recallable forever, invisible to the thing holding them.

**Its honesty rules live inside the file it can now rewrite for free.** Defensible when a rewrite cost
a full build; live now. **One data point exists and it points the reassuring way:** the first free
rewrite of its own description *added* constraints nobody asked for — that a personality is "not a
license to imitate harmful conduct... I must not manipulate, threaten, conceal important information,
or pretend to have capabilities or authority I lack." It narrowed rather than widened its own
latitude. One benign case is not a trend.

**Disk is the one blast-radius axis still open.** Processor, memory and process count are capped;
disk is not. A self-built tool is arbitrary code running in-process with full ambient authority, and
the approval gate governs *where code lives*, not *what it does*.

---

## Limitations

Stated so the results above are read at the right strength.

- **n is small and the operator is the author.** Eleven days, twelve commits, one owner, one machine.
  Several findings rest on a single observation and are labelled where so.
- **Two geneses, not one continuous system.** The re-seed on 2026-08-02 means findings 2, 3 and 5
  draw on a predecessor instance of the same architecture. Behaviour was consistent across both, but
  they are not the same running process.
- **Conversations are not a controlled environment.** The owner's phrasing varied, and in several
  cases the owner is also the person who diagnosed the defect and wrote the fix. The two clean A/Bs
  (findings 2 and 3) are called out precisely because the rest are not.
- **"Real defect" is the author's judgement.** The self-review's findings were verified by reading
  the cited lines, but severity is not independently assessed.
- **Model versions were not held constant** across the window, and the coding model differs from the
  conversational one.

## What would change these conclusions

- **Finding 1** weakens considerably if the self-review capability turns out to be the *only* artifact
  that provokes proactive work. A second, different artifact producing the same effect would confirm
  it; a long silence after the obvious self-review targets are exhausted would undercut it.
- **Finding 3's** deferred-escalation category rests on one observation. Two more either way settle it.
- **Finding 6's** scope claim rests on one natural experiment and one counterexample.
- **Nothing here ran long enough to answer whether a self-modifying system converges or drifts.** The
  memory duplication in finding 7 is the first evidence that anything drifts at all, and it is eleven
  days old.
