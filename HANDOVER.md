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

**The model selects tools; it never rewrites their answers.** Tool output is returned to the user verbatim. This
is not a style preference. On a real measured turn, a sourced answer of about 2,800 characters —
with a summary table and a citation — came back from the model at about 1,100 characters, as flat
prose with the citation gone, *while the model was explicitly instructed to return it verbatim*.
Prompt wording cannot guarantee fidelity, so fidelity is enforced in code. A paraphrased figure is
a wrong figure.

**Native function calling is compatible with verbatim output.** The earlier implementation used
a custom structured route object and rendered tool descriptions into the prompt. The local code
now supplies native function schemas instead. The host executes the selected call and returns
the tool response without a model synthesis pass. The rewrite that removed citations was caused
by that extra generation pass, not by function calling. Conversation history closes each
function call with a content-free result marker, never the permissioned answer. This code change
does not itself redeploy the hosted agent; earlier live verification below describes the
previous deployed build.

Annotated methods now drive both native declarations and SDK invocation. The host uses
`AIFunctionFactory.Create` and `AIFunction.InvokeAsync` to bind arguments rather than a tool-name
switch or a route DTO. Only the selected tool is constructed with the current caller's identity.
Result marshaling preserves the original string rather than JSON-serializing it. Selection uses
the SDK's `AgentResponse` and `FunctionCallContent` directly; `ValidateToolCall` in the agent
retains the strict pre-execution checks without a separate selection-result class.

Before the broader simplification, the native implementation passed 94 targeted offline tests, including SDK argument/default
binding, cancellation, caller isolation, Responses wire encoding, verbatim output, per-session
history round trips, rejected calls, and legacy-session migration.
The live golden-set evaluation now uses that same native selection path, but was skipped locally
because the Foundry endpoint and model environment variables were not configured.

### Native-function simplification (15 September 2026)

- Removed the unused answer-capture class, write-only last-tool state, no-op route callback and
  obsolete Fabric thread field. Tools return their strings directly.
- The agent has a typed Foundry session store. Existing keys and old type/value envelopes remain
  readable without loading assembly-qualified CLR types. Channel state is owned by the channel.
- The channel references only a dependency-free identity project, nested inside the agent's
  single-directory upload. It no longer references the hosted-agent executable.
- History now obeys its configured message cap, dropping complete turns rather than splitting
  native calls from their result markers.
- Unconfigured SQL produces an explicit configuration message, not a fabricated empty statement.
- Fabric MCP protocol handling moves to the official minimal client SDK.
- Progress and final answers use ordinary messages only. Streaming presentation is intentionally
  removed, not retained without its necessary fallbacks.
- One channel turn record owns pending, answer-ready and delivered state. Cached answers protect
  retries from repeating completed finance calls. A crash after an external send but before the
  delivery write can still duplicate a message.

Local verification: **222 targeted offline tests passed, zero failed or skipped**. This includes
missing message IDs, cached-answer retries, in-flight progress completing before the final
message, legacy state migration, delegated caller isolation, MCP cleanup failures, and finance
reference values. Both hosts built and published. An isolated agent upload passed readiness
and missing-assertion probes; the channel publish contains Identity but no hosted-agent runtime
or MCP dependency. Temporary publish and duplicate test-project artifacts were removed.

VS Code still displayed cached duplicate assembly-attribute diagnostics after the project
boundary change. An agent rebuild passed with zero warnings/errors, and design-time MSBuild
evaluation excludes all nested Identity sources and generated files. Reload the VS Code window
to refresh its project model; do not edit generated assembly attributes.

Deployment on 15 September 2026:

| Component | Verified outcome |
| --- | --- |
| Fabric `capfabricdaweus3` / `rg-fabric-da` | Resumed at 11:17 +02:00; Active on unchanged F2. Billing is running. |
| Foundry `zavafinance` / `prj-fdr-swc` | Version 11 active; new-session logs confirm `2026-09-15.2-native-routing`. |
| Functions `app-zavafin-xjm5mipto7f22` | ZIP deployment `bde00a72-6465-475a-8a4b-3fd3a46d8d1e` succeeded; four functions indexed, health 200. |
| Channel dependencies | Startup logs confirm Durable Task connected using managed identity. |
| Authentication checks | Channel rejects an unsigned request with 401; agent refuses missing and wrong-audience user assertions. |
| Live routing | 38/38 native golden-set cases passed against `gpt-4.1-mini`, plus 19 annotation/golden-set contract checks. |

The first routing run exposed one misroute: "How are we doing on headcount in APAC this year?"
was expanded into an unasked trend comparison. The statement and analysis annotations now
distinguish a single-KPI status lookup from requested analysis and preserve the question's scope.
All 57 checks passed before the correction was deployed as version 11.

**Signed-in Microsoft 365 verification resumed and found an upstream accuracy blocker.**
The user completed sign-in, resolving the earlier password-page blocker. Testing used `reset`
and a fresh conversation from 11:40 to 11:50 +02:00 on 15 September:

| Prompt/check | Observed result |
| --- | --- |
| `What is gross margin?` | KPIpedia returned the full definition, calculation, non-additive aggregation caveat and source footer. |
| `Now show me the figure for EMEA in November 2025.` | Inherited Gross Margin and returned **42.52%**, prior month **43.21%**, change **0.69 pp down**. |
| `And for Q4 2025?` | Inherited KPI and geography; returned **42.67%**, matching the component-derived reference rather than the incorrect average **42.68%**. |
| `Show closing headcount for EMEA in Q4 2025.` | Returned **3,247 FTE**, matching the closing-month reference. |
| Explain the October-to-November EMEA gross-margin decline and department drivers | MCP completed and the full answer rendered, but the answer used gross revenue as denominator: **39.56% -> 38.86%**, rather than **43.21% -> 42.52%**. **Accuracy failed.** |
| Repeat the analysis with the explicit Net Revenue/COGS formula | Correct source totals and **43.21% -> 42.52%** appeared, but the answer called that **-1.21 pp**, not **-0.69 pp**. **Accuracy still failed.** |

The normal analysis used November gross profit **36,411,157.22** over gross revenue
**93,701,276.73**. The correct denominator is net revenue **85,639,562.37**. The diagnostic
request explicitly supplied `Net Revenue = Gross Revenue - Revenue Deductions` and
`Gross Margin % = (Net Revenue - COGS) / Net Revenue * 100`; this was a test, not a persistent
fix. Do not label the analysis correct just because MCP, routing and delivery succeeded.

Backend evidence:
- Hosted session `4f39d51c40ede9b55df7754dcfa0213a816ae431779e1255b966580709acb75`
  started version **11**, build `2026-09-15.2-native-routing`.
- Logs show native selection of `get_kpi_info`, `get_statement` and `explore_finance`,
  successful delegated downstream calls, SQL rows and persisted agent history.
- Six finance turns completed through the ordinary-message path. Each has a
  `Turn progress finished` trace and completed Durable orchestration. That trace is emitted
  after a nonempty send response ID and successful Delivered persistence.
- Browser checks found one final source-bearing answer per finance prompt. Acknowledgements
  and the 15/90-second progress messages appeared before the relevant final, not after it.
  Reloading the conversation retained all six final answers, including the exact SQL figures.
- The first analysis returned 2,188 downstream characters and 2,243 after the source footer;
  the diagnostic final was 4,813 characters. These were real answers, not transport fallbacks.
- The M365 conversation is `dafe5a86-674b-4d53-a15d-6740ddf118cd`; its hosted session was
  left active.

KPIpedia's raw accessible announcement included its SharePoint reference-style citation.
M365 rendered the explanation and source footer, but not a visible clickable citation.
Both `teams.microsoft.com/v2` and `teams.cloud.microsoft` redirected to the retired-client
error before the Teams app opened. Teams is therefore **not verified**. Two-user RLS,
live cancellation and failed-send recovery were not exercised; the offline regression tests
remain the evidence for the latter two.

Azure CLI's bot-audience consent limitation remains irrelevant to the successful browser path.
No authentication, consent, storage network rules, Fabric agent configuration or SKU was changed
to make these tests pass. All changes remain uncommitted and unpushed. The historical results
below remain separate evidence.

**Follow-up send check (15 September 2026).** After the reported "Failed to send" messages,
the bot registration, generated app manifest and messaging endpoint matched, the Teams channel
was enabled, and the Functions endpoint remained healthy. No message requests appeared in the
09:55-10:08 UTC telemetry window; health requests continued to arrive. A fresh signed-in M365
"What is net revenue?" control then returned a full KPIpedia answer: ingress at 10:09:25 UTC
returned HTTP 200 in 1.09 seconds, and final delivery completed at 10:09:54 UTC
(operation `a11dc8ab4c6057c53bab036a9a5c5461`). The user subsequently confirmed "all works."
The reported send issue is therefore closed on user confirmation, without a code, configuration
or permission change and without an established root cause. This does not supersede the
separate Fabric calculation failures above or constitute an automated Teams acceptance test.

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

## Historical deployed build (before the native-function and simplification changes)

Both halves are deployed. All three capabilities have been verified end to end in Teams against
ground truth computed independently from the source data — the KPI explanation returned verbatim,
the figures exact to the cent, and the causal analysis exact to the percentage point. The routing
evaluation passes fully against the live model after the split, which is the evidence that routing
behaviour survived the port rather than merely compiling.

That deployed build's progress and delivery experience uses informative streaming updates
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

**Correct Fabric analysis accuracy before accepting the finance experience end to end.**
The 15 September signed-in M365 tests passed KPIpedia, exact SQL figures, follow-up context and
ordinary delivery, but both analysis questions failed numerical acceptance. Align the published
Fabric data agent with KPIpedia and the deterministic SQL definitions: gross margin uses
**net revenue**, and percentage-point movements must be calculated from the underlying
components, not guessed in the narrative. Put the KPI-specific calculation rules and example
queries in the upstream
[data source configuration](https://learn.microsoft.com/fabric/data-science/data-agent-configurations#data-source-instructions),
then republish and retest the original question without supplying its formula. Do not mask this
by rewriting permissioned answers or adding another model synthesis pass in the orchestrator.

Also complete Teams testing in a supported authenticated client, two-user RLS isolation,
M365 citation-link rendering, and controlled live cancellation/failed-send recovery. Do not
inject send failures into the shared deployment without an isolated test plan. For recovery,
confirm that retry reuses the cached answer rather than repeating downstream finance calls.

Use the channel's `Turn progress finished` trace alongside the actual rendered conversation:

```kusto
AppTraces
| where Message has "Turn progress finished"
| where isnotempty(OperationId)
| project TimeGenerated, Message
| order by TimeGenerated desc
```

`Streaming` and `DeliveredInStream` belong to the old implementation and are no longer current
acceptance criteria. The new implementation has one ordinary-message path. Local transport and
lifecycle tests do not substitute for signed-in channel rendering or permission checks.

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

**Prompt injection defence still needs a dedicated review.** Source documents and inbound user
text remain untrusted. Tool answers no longer enter this model's context; native-call history
uses content-free markers instead. That boundary prevents this host from following instructions
embedded in returned answers, but it does not replace protections within the downstream agents
or validation of inbound requests.

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
