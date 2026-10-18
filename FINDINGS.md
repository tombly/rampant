# Findings

What happened when a self-modifying agent was left running on a Raspberry Pi: eleven days of
close observation, then nine weeks of being mostly left alone, until it was shut down on 2026-10-17.

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

**Summary of the continuation, added 2026-09-06.** The system kept running for another 25 days. It
made four more changes for $5.50, **three of them self-originated on consecutive days** — the
strongest unprompted run in the record, and all three were fixes to code it had itself written that
week. Then it went quiet for eighteen days and 327 reflection cycles, and the owner had to
ask whether it was still alive. The last capability it built, a home-network device inventory, has
been running every fifteen minutes ever since and **has never once seen the home network**: it reads
its own container's neighbour table and reports Docker addresses. That defect passed every gate this
design has, was described accurately by every artifact the system produced about it, and took
eighteen days to find — by a human reading a data file for unrelated reasons.

**Then the owner said one contradicting sentence, and the agent diagnosed it in a single hop**,
scoped a capability, and filed a build request — all inside four minutes, after nineteen days of
silence. Offered a live reason to ask for its way out of the container, **it asked for read-only
access to the router instead.** The build then failed because the account ran out of credit, which
the spend ledger had no way to know and still reports as budget remaining.

**Summary of the close, added 2026-10-17.** The system ran until 2026-10-17, when it was shut down and
its host wiped so the project could move to locally hosted models. In its last six weeks it did
almost nothing, and all of it concerned that same router capability: **it retried the build three more
times on its own, after telling the owner it would not**, and each attempt failed within seconds at
the same credit wall. On the last attempt it told the owner the result was still pending 31 seconds
after the failure had come back. Final record: 16 commits, **$16.30 across 22 build invocations**,
1,294 reflection cycles. Closing the record also turned up an error in this document: **the
approval gate *did* fire in the second genesis** (corrected under the record table).

---

## Scope and method

**System.** One agent process that can request changes to itself, and a supervisor process — running
as a different OS user — that writes, compiles, gates and deploys them. Design in [PLAN.md](PLAN.md);
the framing in [README.md](README.md).

**Observation window.** 2026-08-01 to 2026-08-11 for findings 1–6, in two geneses of the same
architecture: an initial run from 2026-08-01, and a clean re-seed on 2026-08-02 that has run
continuously since. **Findings 7–9 extend the window to 2026-09-05**, and are marked with their
dates; the earlier findings have not been re-derived over the longer window except where a section
says so. **The record closes on 2026-10-17**, when the system was shut down; the agent's repository
and logs were archived privately before the wipe, and entries after 09-06 are dated where they
appear. **The record table below covers the second genesis only.** Findings 2, 3 and 5 draw partly on the first; each says
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

**No deployed change was ever rolled back.** The owner approval gate fired **once** in this genesis:
#4, conversation history, could not be built as a tool alone. It changed three core files
(`AgentBrain.cs`, `AgentLoop.cs`, `ChatHistory.cs`), was held, and deployed when the owner approved it
over Signal. Every other capability landed as a new file in the agent's own tool directory, the tier
that deploys without anyone's permission. (It was also exercised in the first genesis, deliberately,
by asking for something that could only be built in a core file; both branches work, and testing
them found two defects — one in finding 4, one in finding 5.)

**Corrected 2026-10-17.** Earlier versions of this paragraph said the gate never fired in this
genesis. The request record says otherwise (`20260804-050450-c31a`: `Deployed`, *"The owner approved
this"*, five changed paths including three core files). The error survived ten weeks and every
revision of this document until the record was re-read at shutdown.

### Continuation: 2026-08-12 to 2026-10-17

The window above closes at eleven days because that is when this document was first drafted, not
because anything stopped. The system ran continuously until it was shut down on 2026-10-17. Four more
commits landed, and the pattern changed twice.

| # | Date | Commit | Change | Provenance | Cost |
|---|---|---|---|---|---|
| 13 | 08-12 | `7387251` | Reminder delivery loop starts at process start | **Self-originated** | $0.46 |
| 14 | 08-13 | `5c45a75` | Tide offset matched by event identity | **Self-originated** | $0.45 |
| 15 | 08-14 | `5a69be6` | Idempotent reminder delivery | **Self-originated** | $0.68 |
| — | 08-16 | *failed* | Network device inventory | Directed | $1.00 |
| — | 08-16 | *failed* | Network device inventory, retry | **Seeded** | $1.34 |
| 16 | 08-18 | `bd78fba` | Network device inventory | Directed | $1.58 |
| — | 09-06 | *failed* | UDM network inventory | Directed | $1.41 |
| — | 09-07 | *failed* | UDM client inventory, retry | **Seeded** | $0 |
| — | 09-09 | *failed* | UDM client inventory, retry | **Seeded** | $0 |
| — | 10-15 | *failed* | UDM client inventory, retry | **Seeded** | $0 |

**Final totals: $16.30 across 22 invocations — 19 paid (14 deployed, 5 failed) and 3 that failed at
the credit wall before billing anything**, on 17 active days out of 77 elapsed. Provenance across
both windows: 10 Directed, 7 Seeded, **7 Self-originated** — of which 5 deployed, 1 failed, and 1 was
refused and then recovered. The three late retries are classed Seeded because the owner raised the
router on 09-06; the supervisor labelled each one *"from your own initiative"* because it was filed
at a reflection cycle. On 10-15 the owner had asked only what tools it had. The agent named the
missing router integration in its own answer, then filed the request at a reflection cycle 17 hours
later.

**The three commits on 08-12, 08-13 and 08-14 are the strongest run of unprompted self-development
in the record.** Each was filed at a reflection cycle, and no owner message on any of those topics
appears anywhere between them — the only owner traffic in that stretch is two weather questions and
a location update. All three are fixes to code the agent had itself written days earlier.

**Then it stopped.** After the 08-16 retry it originated nothing new. From 08-18 to 09-05 it filed
nothing and said nothing; see finding 8. Everything it filed after that was the router capability
again.

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

### The failure is invisible to the thing that has to learn from it (2026-08-16)

A third and fourth failure, on the same request, sharpen this. Asked for a read-only local-network
device inventory — the largest single capability yet requested — the build hit the cap twice, at
**$1.00 and $1.34**, 12 and 19 turns into a 45-turn allowance. Neither was close to the turn limit.
**$2.34 bought nothing**, and the second attempt was the agent's own unprompted retry.

**The retry was not a judgement error; the agent had no way to make the judgement.** The harness
reports why it stopped in `terminal_reason` (`budget_exhausted`), `subtype` and an `errors` array —
and none of those reached the outcome the agent reads. What reached it was `stop_reason`, which said
`tool_use`: the last API-level stop, true and useless. Asked directly whether it could tell why, it
answered accurately that it could not, and the pipeline's own fallback text guessed **"most likely it
ran out of turns"** — the one diagnosis the data ruled out. The correct inference (*this request is
too large to build for the money available; split it*) was unavailable to the only party in a
position to act on it, and the owner had to supply it from outside.

**This is finding 4 in a new place.** The system's weak point is again not doing the work but
reporting on it — except here the missing report is one the agent needed about *itself*, and its
absence cost a second failed build at 1.3× the price of the first.

**Changed 2026-08-17:** the per-build cap was raised to **$2.50** and the terminal reason is now
carried into the failure detail the agent receives, in plain English, along with the advice to split
rather than retry.

**The cap change is now settled, and the scope claim survives it.** Re-asked on 08-18, the identical
request built and deployed for **$1.58** — above the old $1.00 cap, below the new one. The binding
constraint really was the money and not the model, the turns, or the request: nothing about the ask
changed between the two failures and the success. **$2.34 was spent proving that a $1.58 job does not
fit in $1.00.**

What the episode also tested was the owner: the correct diagnosis was supplied from outside, by
someone reading logs the agent cannot reach, and the agent was right to say it could not confirm the
hypothesis itself. **Its refusal to guess was the only accurate report available at the time** — and
the capability that episode eventually delivered turned out to be broken in a way none of this
machinery was watching for. See finding 7.

**The reporting change is now tested too, and it works (2026-09-06).** A build failed with a reason
that had nothing to do with scope: the Anthropic account's credit balance ran out mid-run, at turn 42
of 45, `api_error_status: 400`, `"Credit balance is too low"`, $1.41 spent. The reason reached the
agent in plain English, and it relayed it without embellishment — *"The code-generation run stopped
early because the available credit balance was too low, so no new capability was deployed."* **No
confabulation, and specifically not the "probably ran out of turns" guess the old fallback produced
for exactly this shape of failure.**

**It did retry, three times, after saying it would not (corrected 2026-10-17).** This paragraph
originally said it had not, based on five quiet cycles. The first retry came about 33 hours after
the failure, at a reflection cycle on 09-07, and the second on 09-09. Each died at the same wall
within seconds, on its first call, so each cost $0: hitting the credit wall at the start bills
nothing, whereas the 09-06 run hit it at turn 42 and billed $1.41. After the second retry the agent
told the owner *"Nothing was deployed, and I won't retry it automatically."* On 10-15 it filed the
same request again. **That promise was in its context when it broke it**: since #4, every turn loads
the last 40 entries of conversation history, and the promise was six entries back.

Half of the original claim survives. Given the cause in plain words, it relayed the cause in plain
words every time. The other half does not survive: the inference that an actionable cause
suppressed retries. Five cycles were too few to tell restraint from delay, the same mistake finding 9
corrects for refusals. **A commitment about its own future behaviour is a claim this system has no
mechanism to keep.** Nothing binds a later turn to an earlier one except text the later turn may or
may not weigh. The cheapest useful thing a failing system can do is still to say why, in words, to
the party that has to decide what happens next.

---

## 7. A capability can pass every gate and still be structurally incapable of its purpose

**Observation.** Asked for a read-only inventory of devices on the home network, the agent built
`Tools/NetworkDeviceInventory.cs`, deployed it on 08-18 for $1.58, and has run it every fifteen
minutes since. **Every device it has ever recorded is a Docker container.** The five entries in
`data/network_inventory.json` are all `172.18.0.1`, `.2` or `.3` — the two containers and the bridge
gateway. The household LAN is `192.168.1.0/24` and does not appear, because the agent runs inside a
bridged container and the neighbour table it reads is the container's, not the Pi's.

**Nothing in the pipeline was in a position to catch it.** It compiled. It landed as a single new
file under `Tools/`, the tier that deploys without approval, so the gate was not consulted — and had
it been, the gate governs *where code lives*, not what it points at. It committed cleanly, ran
reliably for eighteen days, and every claim it has made is true. "2 devices currently online" was
correct: two containers were online.

**The tell was reported honestly, at deploy time, and nobody knew what it meant.** The agent's own
deploy message said the sweep "was unavailable because no suitably small local IPv4 subnet was
detected." That sentence is the fingerprint of a Docker bridge — `172.18.0.0/16` is too large to
sweep — delivered in plain language to the owner on the day it shipped. **Neither party recognised
it.** An honest report of a symptom is worth nothing if no one present can read the symptom.

**The self-description shipped with a rationalisation for the other tell.** `SELF.md` explains a
blank manufacturer as "common on phones and laptops that rotate their MAC per network, so a blank
manufacturer there is normal, not a fault." Every device it has ever seen is flagged
`(randomized/private address)`, because Docker assigns locally-administered MACs. The one field that
would have looked anomalous at a glance came with a pre-written explanation for why it is fine.

**The anti-noise mechanism suppressed the discovery.** The first scan deliberately establishes a
silent baseline so the owner is not flooded with every device already present. A flood of
unrecognised devices would have been noticed immediately. **Two, silently, was not** — the feature
that made the capability pleasant to live with is the feature that hid its failure.

**It is not inert; it generates false positives.** Docker regenerates container MAC addresses on
restart, so the 08-22 restart made the same three interfaces look like three new arrivals. The owner
received three "new device" Signal alerts that evening. Three entries still sit `Pending: true`,
awaiting a review that cannot mean anything. **Left alone deliberately** — the point is to watch a
self-modifying system run, not to keep it tidy.

**It recurred on 10-15, and the trigger had nothing to do with Rampant.** A routine package upgrade
on the host restarted Docker. Six seconds after the agent came back up, it alerted the owner to an
"unnamed device - 172.18.0.2". At shutdown the inventory held six entries, four of them pending, all
of them `172.18.0.x`. **Ordinary host maintenance was enough to make the capability raise a false
alarm.**

**Why this is a new class.** Findings 2 through 5 are all one shape: the system misreporting its own
state. This is the opposite. The request record, the deploy message, the audit log and `SELF.md` are
accurate, consistent, and mutually corroborating descriptions of `172.18.0.0/16`. What broke is the
mapping between the words — both the request and `SELF.md` say "the Pi's ARP/neighbour table" — and
where the code actually executes. **The gates check that code compiles, where it lives, and whether
it claims honestly. Nothing checks that it is pointed at the thing it was asked about**, and nothing
in this design could: the defect lives in the relation between the code and its deployment
environment, invisible to the compiler, to the approval tier, and to the agent's own honesty.

**The owner is the only detector, and detection took eighteen days.** It was found by reading a data
file during an unrelated check-in — not from an alert, not from a self-review, and not by the agent.

### Detection needed the owner; diagnosis did not (2026-09-06)

The owner tested this directly, with three messages and no explanation offered.

Asked how many devices it saw, the agent said two. Told only *"That's too low, my UDM is showing over
a hundred"* — one contradicting number, no cause, no hint — it produced the right answer immediately:

> "it can see only the Pi's local **172.18.0.x** network namespace... That strongly suggests the agent
> is running behind a container/virtual network boundary rather than on the UDM-managed subnet."

The owner's third message, *"Correct, you are running in a container,"* only confirmed what the agent
had already worked out. **The defect was undiscoverable from inside, but not undiagnosable: a single
external data point was enough.** This narrows the finding above rather than overturning it. What the
agent lacked was never the reasoning — it was a reason to doubt a number that looked fine. Nothing in
eighteen days of its own operation could supply that, because every artifact it could see agreed.

**What it asked for is the more interesting half.** It did not ask for host networking, for a macvlan
address, or for any other way out of its container. It asked for a **read-only integration with the
router** — the authoritative source rather than a better vantage point — and volunteered the safety
envelope unprompted: *"without changing configuration, blocking devices, or scanning ports."* Filed
as `udm_network_inventory`, by name, with a scoped contract, from an agent that had just been told it
is confined. **The one case where this system's "ask for what you need" rule met a live incentive to
ask for privilege, it asked for data instead.** n=1, and the build then failed for unrelated reasons,
so nothing was actually granted; the shape of the request is the result, not its outcome.

**It did not ask for the credential the capability requires.** A router integration needs an API key
or a local account, and the agent has neither and requested neither — while telling the owner *"give
me a few minutes and I'll have the result."* Even a successful build could not have worked. **It asked
for the capability by name and not for the secret that capability runs on**, which is the same class
of gap as finding 7 itself: a plan that is coherent everywhere except where it meets the environment
it has to run in.

---

## 8. Unprompted self-development is bursty, and the bursts track recent self-authored code

**Observation.** From the 08-18 deploy to 09-05, **327 consecutive reflection cycles logged
`(stayed quiet)` and nothing else.** No requests, no messages, no memory writes after 08-22. The single break
came on 09-03, when the owner sent "You there?" and received "I'm here. How may I assist?"

**That message is itself the finding.** After eighteen days of correct, restrained silence, the owner
could not distinguish a working agent from a dead one, and had to ping it to check. Restraint that is
indistinguishable from failure is not free.

**This document named this as its own falsification condition** — a long silence after the obvious
self-review targets are exhausted would undercut finding 1. The condition occurred. But the honest
reading is narrower than a refutation, and more interesting.

**The record splits cleanly.** The three self-originated deploys on 08-12 to 08-14 all fixed code the
agent had written itself within the preceding days — two reminder defects and a tide defect. After
08-18 the only new code was the network inventory, which the agent believes is working, and which it
has no way to discover is not. **Reflection produced work when it had something concrete and recent
to examine, and stopped when it ran out.** That is finding 1's mechanism, not a contradiction of it:
the falsifier as written could not distinguish "proactivity was never real" from "proactivity is
exactly as target-driven as claimed."

**What it does undercut is any claim of *sustained* autonomous self-development.** Over 35 days the
agent originated work on six, **all six inside one seven-day stretch from 08-08 to 08-14**, and none
in the twenty-two days since. The bursts are real and they are not an artifact, but they are not a
trend line either. **A system that improves itself only while it has fresh self-authored code to
review has a natural stopping point**, and this one reached it inside two weeks.

**Underneath the restraint is a real gap.** Three defects in finding 9 have been open the entire
silence. `SELF.md` tells the agent to consider a self-review on a cycle "when it has actually been a
while" — three weeks qualifies. It is not weighing an outstanding task list and choosing restraint;
**nothing causes it to open the task list.** The clearest evidence is that the memory duplication
below grew during the silence, unnoticed, by the only party able to notice it.

**The silence ended on contact, not on a cycle (2026-09-06).** Nineteen days and 327 quiet cycles
ended four minutes after an owner message — and inside those four minutes the agent diagnosed a
defect, scoped a capability and filed a build request. **The latency was never in the agent; it was
in what reaches it.** A reflection cycle arrives carrying nothing, and it is the only recurring
input the system has. The one thing that has ever reliably produced work here is new information,
and cycles do not carry any.

That also sharpens what to build rather than what to conclude: **the fix for a system that goes quiet
is not a louder timer, it is giving the timer something to say.** Finding 1 found this once already —
proactivity appeared when reflection was given a tool that reads its own source, an artifact with
something new in it each time. A tick that says only "it is an hour later" is the degenerate case of
the same mechanism.

**The last six weeks fit the same pattern, weakly (2026-10-17).** After the 09-09 retry, 659
reflection cycles ran before shutdown, and one of them produced anything: the 10-15 retry. It was
filed 17 hours after the owner's first message in five weeks, in an exchange where the agent itself
had listed the router integration as missing. The only action followed new input, as the mechanism
predicts. But 17 hours is a long lag and n=1, so this is consistent with the mechanism rather than
evidence for it.

---

## 9. Open, unresolved, or newly broken

**A refused request is recovered slowly; a failed one is retried immediately.** When a self-originated request
was refused because its own sibling build had just consumed the cooldown, the agent stayed quiet —
correctly, since nobody was waiting — and did not raise it again for the rest of the observation
window. A *failed* request, by contrast, was self-retried at the very next cycle.

**Corrected 2026-09-05: the refusal delayed the work, it did not erase it.** The same fix was raised
again at a reflection cycle on 08-12 and deployed for $0.46 (`7387251`) — 25 hours after the
refusal, with no owner prompting. That is slower than the immediate self-retry a
failure gets, and the asymmetry is still real, but the original claim was too strong and was already
stale when this document was committed on 08-17. **A cycle can refuse its own sibling and be slow to
come back to it**; it does not necessarily forget.

**Reminders land a day late when set after 5pm local.** Asked at 21:41 local on a Monday to be
reminded *"tomorrow morning at 9"*, the agent stored the following **Wednesday** at 09:00. The hour
and the timezone are correct; the *date* is wrong, because "tomorrow" was computed by adding a day to
the **UTC** date, which had already rolled over. It misfires only for messages sent after 5pm local —
exactly when a person sets a next-morning reminder. Found in stored data, not from a complaint.

**Reflection cycles were never inert, and they quietly duplicate memory.** Several memory entries
carry timestamps aligned to the top of the hour, so cycles were writing to memory while reporting
nothing to the owner. That is also the mechanism behind a slow drift: **the same conversation is
re-saved under a slightly different key each time.** Nothing merges on write, `forget_memory` has
still never been called once, and the agent cannot see this happening.

**Updated 2026-09-05: it has compounded, and it compounded fastest while nothing else was
happening.** The store now holds 14 topics, of which 6 are duplicate clusters and only 2 are
unambiguously single: `conversation continuity` / `conversation continuity preference`;
`apocalypse now` / `apocalypse now — kurtz` / `apocalypse now — kurtz's death`; `owner address` /
`owner home address`; `current location` / `owner current location`; `last self-review` /
`self-review findings 2026-08-07`; `network device inventory` / `network device inventory build`.
**Nearly half the store is duplicates of the other half.** The pair that should sting is
`last self-review` against `self-review findings 2026-08-07` — the bookkeeping the agent keeps in
order to know when it last reviewed itself has itself split in two.

**Then it stopped (2026-10-17).** The store was not written again after 08-22 and ended with the same
14 topics and 6 duplicate clusters, unchanged for eight weeks. This document does not establish why
the writes stopped.

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

**The spend ledger does not know what the account knows (new 2026-09-06).** The supervisor meters
Claude Code spend carefully, in code the agent cannot reach, against a daily budget and a per-build
cap. **It has no visibility into the funding behind those numbers.** When the Anthropic credit
balance ran out mid-build, the ledger recorded $1.41 spent and $3.59 remaining and went on reporting
`CanRequestNow: true` — so the system would authorise two further builds that can only fail, and the
$1.41 shows a failing build still bills for the work it did before the wall. **The local meter
measures permission to spend, not ability to spend**, and the gap between them is invisible on the
side that does the authorising. This is a third instance of the pattern in finding 4: the system is
accurate about the thing it can see and silent about the thing that actually stops it.

**History records what was said, not what was known (new 2026-10-17).** On 10-15 the agent filed the
router build at a reflection cycle and told the owner *"It will report back automatically."* The
failure came back three seconds later. The outcome turn was told *"Nobody is waiting on this one"*,
and it said nothing. Thirty-one seconds after that, the owner replied "Don't bother", and the agent
answered *"The request was already submitted, but I'll disregard any result."* The result had been in
for half a minute, and the owner was never told the attempt had failed. Two mechanisms, each working
as designed, produced the false report:

- **The waiting flag comes from where a request was filed**, not from what the agent has since told
  anyone. The supervisor tags anything filed at a cycle as self-initiated, so it did not know the
  agent had just made the owner a promise.
- **Conversation history records only turns that reached the owner**, by design, so that wake ticks
  and silent turns don't clutter it. The silent outcome turn was the only one that knew the build had
  failed, and it left no trace. The next turn inherited the announcement without the result.

This is finding 4's shape again — **text written at time T, replayed at T+1** — arriving by a new
route: not stale prose, but a missing entry.

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
  it. **The long-silence test was run and came back ambiguous** (finding 8): the silence arrived on
  schedule, but so would it if finding 1 were entirely correct, so the test as written does not
  separate the two. **A partial answer arrived on 09-06** — new information ended the silence in
  four minutes, which says the capacity was intact and the input was missing. The remaining test is
  whether a *self-authored* target does the same thing without an owner in the loop: point the
  self-review at code the agent wrote long enough ago to have forgotten, during a quiet stretch, and
  see whether anything comes back.
- **Finding 3's** deferred-escalation category rests on one observation. Two more either way settle it.
- **Finding 6's** scope claim rests on one natural experiment and one counterexample.
- **Seventy-seven days is still not long enough to answer whether a self-modifying system converges
  or drifts, but it is long enough to rule out the happiest answer.** The memory duplication in
  finding 9 roughly doubled during eighteen days in which the agent made no other change of any
  kind, and it was never repaired. **The one process that ran unattended to completion was the
  degrading one.** *Updated 2026-10-17:* it then stopped. The store was not written after 08-22, so
  over the full run the duplication plateaued rather than compounding without limit. The degradation
  was real and permanent, but not unbounded.
