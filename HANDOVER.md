# ZavaFinance — Handover

A record of how this project got to where it is: what was decided, what was discovered the hard
way, what is proven, and what is still open. Written from the working session that produced it.

---

## What it is

A finance agent for Microsoft Teams and Microsoft 365 Copilot. It answers three kinds of question,
each as the signed-in user:

| Question | Answered by | Shape |
|---|---|---|
| What does this KPI mean? | A Copilot Studio agent | Natural language, slow — around 51 seconds |
| What was the figure? | Fabric lakehouse SQL | Structured, sub-second |
| Why did it move? | A Fabric data agent | Open-ended, 25 seconds to 3 minutes |

Zava is a fictional company. There is no real financial data anywhere in this project.

## Why it is shaped this way

Routing and tools live in a Foundry hosted agent, and the Teams side is a thin channel that owns
identity, acknowledgement and delivery. The reason for that split is that the agent becomes
reachable by anything that can call the Responses API, not only through Bot Service, and it becomes
independently testable from the Foundry playground.

Everything else about the split is cost, and that cost is real — an extra hop, a user assertion
crossing the wire, ownership of the token lifecycle, and nothing removed operationally. The trade
was made deliberately and with those costs on the table. Anyone revisiting it should weigh them
again rather than assume they were overlooked.

---

## The decision that makes the whole thing work

Foundry forwards only headers prefixed `x-client-` into a hosted-agent container, and deliberately
does not forward the authorization header. So the channel sends two tokens: its own managed
identity to authorize the call, and the user's Teams sign-in assertion forwarded unchanged in a
client header. The agent performs the on-behalf-of exchange from that assertion, once per
downstream resource.

Foundry neither authenticates nor interprets that header. **This solution owns its validation**,
and that ownership is the security-critical part of the design.

The consequence that took the longest to internalise: validation must happen **before** any claim
is used. It is tempting to argue that a forged assertion would simply fail the downstream token
exchange, so why validate early. That reasoning is wrong, because the user's identity is what
derives the session key, and the session key is used to load and later overwrite state *before* any
downstream call happens. A forgery that never obtains a downstream token could still read and
poison another user's session. There is a test pinning this specifically, and it should not be
relaxed.

## The other security invariant

A conversation identifier is not a user. In a Teams group chat, one conversation covers many
people. All per-user state is therefore keyed by a hash of tenant, user object id and conversation
together, with the fields length-prefixed so a crafted conversation id cannot shift bytes across a
field boundary and collide with someone else's key. Identity comes from the validated assertion's
claims, never from the message payload; the payload is cross-checked only as defence in depth, and
a disagreement fails the turn rather than being reconciled.

This has never been tested with two real users. See the open items — it is the most important gap.

---

## What was learned the hard way

These are the things that cost real time. None of them are obvious, and several look like a
different problem than they are.

**A deployed agent does not reach existing conversations.** Hosted-agent containers are warm per
session, and a session is bound to its conversation. A conversation in active use keeps its
container and therefore keeps running the build that was live when that container started. A fix
was deployed correctly, confirmed active, confirmed by its own startup marker — and still
reproduced the original exception for four consecutive test cycles, because the conversation under
test was pinned to an older container. New conversations were already running the fix. The remedy
is to send `reset` before testing after any agent deploy. Async stack-trace line numbers are the
reliable tell: a frame that no longer matches the source means the running assembly is older than
the source, not that the fix was wrong.

**Deploys are content-addressed.** An unchanged package is skipped, the previous version keeps
serving, and it is reported as a successful deploy in about ten seconds. There is a build marker in
the source whose only job is to be bumped so a deploy is forced and so the question "which build is
actually running?" has an answer.

**Microsoft 365 Copilot silently drops progress indicators and streamed answers.** Typing activities
were sent and accepted — measured at twenty-one sent, zero failed — and rendered nothing at all. The
documented mechanism is an informative streaming update instead. But the stream is not reliable on
that surface either: measured by status code, Copilot creates a resource for the *first* streamed
frame (`201`) and accepts-and-discards every later one (`202`, no resource id) — including the final
answer. Ordinary message activities are created on both surfaces. So the channel verifies that the
final message actually produced a resource and re-sends it as an ordinary message when it did not,
and switches later progress nudges to ordinary messages once a discarded frame is seen. The trap to
avoid: "does the stream have an id" looks like the right check and is not, because the id comes from
the first frame, which Copilot does create.

**A causal-question failure was a runtime problem, not a data problem.** Multi-step questions
returned first "no rows" and then a confidently wrong answer. Tables had recently been added to the
data agent and looked like the cause; they were nearly reverted. They were not the cause — the
generally available runtime could not plan a multi-step query over them. Switching the data agent to
its preview runtime produced a correct decomposition while single-fact checks stayed exact. That
setting is not exposed on the REST surface, only in the portal.

**A duplicate-turn defect was hiding in plain sight.** A single Teams message was arriving as two
separate posts about eleven seconds apart, and each started its own turn — so every question ran
routing twice and executed the chosen tool twice. For a conversational subagent that means a
duplicate question injected into its own state, not merely wasted latency. Making the turn
identifier deterministic in session and turn means a redelivery now collides with the turn already
in flight.

**Capacity throttling looked like a connectivity failure.** After sustained testing the Fabric
capacity began refusing requests, and the user-facing message said the data agent could not be
reached. That was wrong twice over: the request was delivered and refused, and the fix is to wait
or scale rather than to investigate connectivity. It now has its own message and is deliberately
not retried, because throttling outlasts a turn. Note that the overage is smoothed forward, so
waiting alone does not clear it — pause and resume the capacity before a demo.

**An unpublished-looking agent was a sunset API.** Calls to the old assistants-style endpoint still
succeeded at every step — creating threads, accepting messages, starting runs — and only the run
itself failed, with an unhelpful bad-request error. It looked exactly like a broken or unpublished
agent, and the portal kept working throughout because it had already migrated. Moving to the MCP
endpoint fixed it with no permission or infrastructure change at all.

**A manifest scope, not the app, broke Microsoft 365 Copilot.** The agent was visible in Copilot
and returned a generic apology, with no request ever reaching the application — the failure was
entirely upstream. The installed package advertised only the Teams surfaces. Declaring the Copilot
surface explicitly resolved it. Related: Copilot caches its agent list separately from Teams, so
the two can disagree for minutes to hours after an update, and a rename is the worst case because
the old entry disappears before the new one appears.

**On preview dependencies.** There is exactly one, and the instinct was to hand-roll around it. That
instinct was wrong on inspection: every breaking change in its history is confined to features this
design does not use — resilient background execution, steerable conversations, stream providers —
because answers are returned whole and conversation state is owned here. The surface actually
consumed has been stable since the first beta. It is pinned deliberately. Do not adopt the
resilience or steering APIs without re-reading that changelog first.

**On package versions.** The project was briefly on a beta line unnecessarily. The generally
available line is the lower version number; the higher one is beta. This is easy to get backwards.

---

## Design positions worth defending

These were argued through and should not be quietly reversed.

**The model routes; it never writes the answer.** Tool output is returned to the user verbatim. This
is not a style preference. On a real measured turn, a sourced answer of about 2,800 characters —
with a summary table and a citation — came back from the model at about 1,100 characters, as flat
prose with the citation gone, *while the model was explicitly instructed to return it verbatim*.
Prompt wording cannot guarantee fidelity, so fidelity is enforced in code. A paraphrased figure is
a wrong figure.

**Tool-derived replies stay out of stored history.** When they were stored, the model began reciting
the previous answer instead of calling the tool again — observed as a turn that called nothing at
all.

**Tool descriptions have exactly one home.** They were once written on the tool methods, where
nothing read them, while the model saw a terser hand-written restatement. The negative constraints
that separate the two overlapping tools never reached the model at all, which meant editing a
description appeared to change routing and did not. They are now generated from the methods.

**Shared arguments are modelled once.** Two properties describing the same concept, one per tool,
caused the model to extract correctly and then file the value under the wrong property. There is
nothing in a flat schema that says which argument belongs to which tool.

**Ratios are recomputed from summed components, never summed or averaged.** On this dataset a naive
aggregation is wrong by over a hundred percentage points. The shape of the data type is the
guardrail, not a convention. Headcount is summed across departments but never across months — a
period figure takes the closing month. Percentage movements are reported in percentage points.

**Routing runs at zero temperature.** Borderline phrasings were flipping between a sub-second tool
and a fifty-second one across identical runs, which meant the same question had wildly different
cost depending on luck.

**The latency budget is unchanged by the split.** The channel still times out in ten to fifteen
seconds, so acknowledge-then-answer-proactively is still mandatory. Splitting the host fixed none
of what forced that design.

---

## Current state

Both halves are deployed. All three capabilities have been verified end to end in Teams against
ground truth computed independently from the source data — the KPI explanation returned verbatim,
the figures exact to the cent, and the causal analysis exact to the percentage point. The routing
evaluation passes fully against the live model after the split, which is the evidence that routing
behaviour survived the port rather than merely compiling.

The most recent change is the progress and delivery experience. It uses informative streaming updates
where the channel supports them, falls back to typing indicators and nudge messages where it does
not, and verifies that the answer's own activity actually produced a resource before considering the
turn delivered.

**This has now been exercised by a signed-in human on Microsoft 365 Copilot**, and both the
definition path and the slow causal path returned their answers in full. Telemetry confirmed the
fallback carried them: `DeliveredInStream=False`, preceded by the warning naming the reason. Teams
has not been re-tested since; it takes the streaming path, which is the branch that was already
working.

One limitation is known and pinned by a test so it cannot be forgotten: the progress message cannot
name the tool it is about to use. Routing happens inside the agent, so the channel learns which tool
ran only when the answer is already back. Restoring that capability means streaming the routing
decision out as it happens, which enters the churn-prone part of the streaming API.

---

## What is open

**Verify the progress experience on Teams.** Microsoft 365 Copilot is done — verified on 14
September with a signed-in turn, both the KPI definition and the causal analysis arriving in full
via the ordinary-message fallback. Teams takes the streaming path and has not been re-tested since
the change; it is the branch that was already working, so this is a confirmation rather than an
investigation.

Send `reset` first if reusing an existing conversation, then ask a question and read the result off
the `Turn progress finished` line in Application Insights:

```kusto
AppTraces
| where Message has "Turn progress finished"
| project TimeGenerated, Message
| order by TimeGenerated desc
```

`Streaming=True DeliveredInStream=True` is the streaming path working — expected on Teams.
`DeliveredInStream=False` means the channel did not render the streamed answer and it was re-sent as
an ordinary message; that is the Copilot fallback doing its job, and it is accompanied by a warning
naming the reason. `Streaming=False` is the typing-and-nudge path. The failure this guards against is
an acknowledgement followed by silence, so the thing to confirm on each surface is simply that the
answer arrives exactly once.

Note that the tenant has been cleaned: the stale **Finance Orchestrator** agent and the
**MSC Dispatcher** project it was backed by have been removed — resource group
`rg-mscdispatcher-swc`, the `MSCDispatcherAgent-Bot` app registration and its service principal.
Both agents disappeared from the Copilot agent list as a result. Zava Finance is the only finance
agent left, and its own app registration (`ZavaFinance-Bot`) was never involved.

**The project is not under source control.** Nothing has been committed. A secret and the session
key salt are sitting in a plain folder. This should be dealt with early. Note that the salt must
never be regenerated — it would change every derived session key and orphan every existing
conversation.

**The isolation test has never been run.** Two users, one group chat, a question whose answer
differs by permission. This is the single most valuable test in the project and it does not exist
yet. It must be run with view-only accounts, because row-level security does not apply to
edit-level roles — a run with the wrong account type returns unfiltered data for both users and
passes while proving nothing.

**Prompt injection defences are not in place.** Answers are grounded in documents that users can
edit, and those answers enter the model's context on later turns while the agent acts with the
user's permissions. Anyone who can edit a source document can place instructions in it. Filtering
is needed at two points — inbound user text, and any tool response before it enters history — and a
shield hit needs its own user-facing message.

**One knowledge source is mis-scoped.** The configured source points at a single file, but answers
cite unrelated third-party content from the same location. Deep file-level paths do not scope
reliably through search. The fix is to attach the file directly, or move it somewhere that contains
nothing else.

**Smaller items**, in rough priority order: replace message-count history trimming with something
token-aware; build the index needed to purge a user's data on request, since hashed keys are not
enumerable by user; decide who may see the orchestration dashboard, which is production data access;
confirm traces land in the Foundry agent view; and extend the routing evaluation until it can
actually fail — new cases should come from mis-routings observed in real use, not from more invented
phrasings.

**On capacity**: the Fabric capacity in use is the smallest available and is undersized for the
data agent's multi-step questions. Either scale it or pause and resume before any demo.

---

## How to pick this up

Read the project README first — it carries the architecture and the operational notes in detail.
This document is the *why* behind them and the state of play.

The fastest way to lose a day is to debug a fix that is already deployed but not running, so make
`reset` reflexive after any agent deploy, and trust the build marker and the stack-trace line
numbers over the deployment tool's success message.

The fastest way to introduce a serious defect is to touch the identity path — assertion validation,
session key derivation, or the boundary between the two — without reading why each check is where
it is. Those parts have tests that exist to stop exactly that, and a failure there is a user seeing
another user's data rather than an error anyone would notice.
