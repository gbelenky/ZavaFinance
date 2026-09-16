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

## Resolver update - 16 September 2026

The approved update adds Fabric-owned terminology, Azure AI Search retrieval and a local
resolver inside the hosted agent, plus adaptive KPI/organization clarification in the channel.
It does not add a resolver API/Function or delegate SQL generation to a model.
The agent returns numbered clarification text plus structured options. The Channel Service
builds the Adaptive Card from those options; card presentation does not belong in the agent.

### Clickable-item follow-up - 18:05 UTC

- Channel-only ZIP deployment `52f91e63-089c-405a-b756-ccc2bba63b79` completed successfully at
  `2026-09-16T18:05:26.6013698Z`. Hosted Agent **v13** remains unchanged.
- Each numbered option is a full-width clickable row, including its hierarchy/description.
  Its `Container.selectAction` submits the existing schema/action/request/catalogue binding and
  its own option ID. No radio inputs or Continue button; typed numbers/ordinals remain supported.
  New cards use this layout; historical and cached cards keep their original payload.
- Five new behavioral cases first failed against the radio renderer. After the change,
  **90 targeted Channel tests** passed: row/payload binding, 1/8/25 options, inbound authentication,
  legacy submissions, retries, storage and real MCS SDK acknowledgement handling.
- `/health` returned 200; four Functions loaded and the Durable Task worker connected with MI.
  Existing scoped Channel role assignments were verified; no roles/settings/infrastructure changed.
- Fresh signed-in Microsoft 365 conversation `0be1747a-d05a-4eb4-be1b-dae771c5da83`:
  clicking Gross Margin returned **42.52%**, prior **43.21%**, **0.69 pp down**; clicking the
  Global IT row's description returned **13.1 M USD** OPEX. A further margin card accepted `2`
  and returned the same correct margin. No separate submit step was used.
- After reload: **three cards, 11 clickable rows, zero radios/Continue buttons and three final
  finance answers**. Six `Turn progress finished` traces confirmed post-Delivered persistence
  between `18:06:55Z` and `18:11:13Z`; no exceptions in the `18:06Z-18:12Z` window.
  Browser pointer automation required the existing DOM-click workaround; a first OPEX wait
  mismatched `13.1M` versus rendered `13.1 M`, not a failed submission.
- Single attachment-only transport, nonempty response-ID guard and at-least-once delivery
  remain unchanged. Changes are uncommitted and unpushed.

### Resolver rollout and earlier acceptance

The independent reporting model is Company > Region > Department group > Department, plus
global groups/departments. It preserves all existing keys and financial formulas. Branches are
sets of region/department fact pairs, not sums of previously calculated KPI values. Ambiguous
aliases remain ambiguous; KPI families cannot be executed as measures.

Data publication and activation verified:

- Version `2026-09-16-v1` staged through Fabric notebook
  `2abe237d-bf50-48ca-8f56-1c3b91d3e416`, run
  `a0719460-ef51-4bf9-9bda-65f9aa37b5e6`.
- After Search verification, activation notebook `4a3efa5b-1d59-4a4b-90a6-3aaf69e51991`,
  run `53892221-5b8a-482c-bb4c-c61aa79c774a`, completed at `2026-09-16T16:26:11Z`.
- Delegated SQL confirms **114 catalogue entities**, **336 scope rows**, one calendar convention
  row and exactly one active release: `2026-09-16-v1`, index
  `zava-resolver-2026-09-16-v1`, embedding deployment `text-embedding-3-large`, dimensions 1536.
- Eight offline generator tests and 13 live SQL invariant/reference checks passed.
- EMEA November 2025 still returns **85,639,562.37** net revenue, **42.52%** gross margin and
  **3,258 FTE** closing headcount using the new hierarchy scope.

The notebook verifies source Delta versions did not change during its writes. Only additive
resolver tables are written. SQL synchronization is asynchronous: the first table listing
after the successful Spark job did not yet show the new tables; the subsequent SQL checks did.

Search provisioning and publication are verified:

- Independent `srch-zavafin-dev` in Sweden Central, Basic 1 replica / 1 partition, Entra-only.
- Runtime Search reader and separate publisher schema/data roles verified at service scope.
- Runtime identity `0c09646f-a065-4aad-b29e-69e9bc6a0da6` now has Cognitive Services OpenAI User
  on the existing model account. Only the missing runtime assignment was created, using the
  Bicep what-if assignment ID `ac6d40a0-afdf-566c-b463-79564aa45148`; the publisher's existing
  grant was retained without duplication.
- Index `zava-resolver-2026-09-16-v1` contains exactly 114 expected document IDs, all at the
  staged version, with 1536-dimensional embeddings and semantic reranking.
- For "gross profit as a share of net sales", gross margin is candidate 2, not candidate 1.
  Publication checks top-three recall, not automatic top-one selection; the resolver must
  confirm fuzzy matches rather than infer correctness from rank. The initial top-one probe
  was too strict for this retrieval contract and failed before activation.
- Both hosts publish successfully with shared Identity/Contracts libraries; Channel dependency
  isolation is verified. Initial Agent Release selection: **202 passed, zero failed/skipped**;
  the separate initial Channel selection passed **86** tests before live delivery testing.
  The final corrected combined Release selection passed **332 tests**, zero failed/skipped.
- Nonzero hierarchy OPEX totals independently matched direct fact filters for November 2025:
  Company **199,596,299.89**, Global IT **13,098,583.70**, EMEA Corporate **27,046,917.95**.

Channel release is deployed:

- Code-only ZIP deployment `0bb04d1e-fc6e-484f-8344-ef72957cd88d` succeeded at
  `2026-09-16T16:38:08Z`; no settings or infrastructure were changed.
- Fresh Release tests for final Channel/contracts/formatting changes: **86 passed**.
  VS Code had stale discovery for a changed theory; fresh CLI discovery passed both cases.
- `/health` returns HTTP 200; all four Functions are indexed. Startup logs confirm the
  Durable Task worker connected using managed identity and started listening.

Hosted Agent **v12** was deployed with marker `2026-09-16.1-resolver-clarification`,
and is superseded by **v13** below.
`azd deploy zavafinance --environment zavafinance --no-prompt` succeeded; a fresh signed-in
Microsoft 365 conversation started the new version, with readiness HTTP 200 and the same
dedicated runtime identity. `azd show` does not recognize this data-plane source deployment
and can report "not provisioned"; the agent API, session startup and actual replies are the
authoritative evidence. Do not run global provisioning in response to that message.

Signed-in checks on v12 verified:

- Exact EMEA November 2025 net revenue: **85.6 M USD**, matching the unrounded SQL reference.
- KPI ambiguity: a Channel-built card offered EBITDA/Gross/Operating Margin; selecting
  Gross Margin returned **42.52%**, prior **43.21%**, change **0.69 pp down**.
- Organization ambiguity: IT offered four regional scopes and a global scope with full paths;
  the Global IT card choice returned **13.1 M USD** operating expenses.
- Company and EMEA Corporate OPEX returned **199.6 M USD** and **27.0 M USD** respectively,
  matching the independent fact-scope comparisons above.
- "Gross profit as a share of net sales" reached the resolver verbatim. Runtime logs show
  embedding HTTP 200 at `17:04:32Z` and Search HTTP 200 at `17:04:33Z`. The resolver requested
  confirmation instead of executing its highest-ranked candidate. Replying **third** selected
  Gross Margin and produced the same correct deterministic statement.
- Closing headcount for EMEA November 2025 returned **3,258 FTE**, prior **3,252**.
  Numeric reply **2** correctly continued its confirmation, complementing the ordinal check.
- After `reset`, replaying a visible pre-reset card returned "There is no valid pending choice
  for that selection. Please ask the statement again." It did not execute a finance query.
  Forged, expired, cross-session and changed-release selections are additionally covered
  by offline tests; this is not a claim that two real users were tested.

Live testing exposed a Channel delivery defect in the first ZIP: Microsoft 365 rendered
mixed text-plus-card activities but returned no message ID, causing three durable retries.
The nonempty message-ID requirement correctly prevented recording these as Delivered.
The correction sends a single attachment-only activity, with numbered choices and text
fallback inside the card. Agent numbered results and ordinary text finance replies remain
unchanged. Empty successful callback responses still fail; no message ID is invented.

Final corrective deployment and acceptance:

- Channel code-only ZIP `1982b315-64e6-40d4-8f5b-7970101f8df0` completed at
  `2026-09-16T17:31:31Z`. No settings, infrastructure or identity changes.
- Current hosted Agent **v13**, marker `2026-09-16.2-resolver-clarification`, adds narrowly
  scoped Search/reranker error recovery and sanitized malformed-vector warnings. Unexpected
  errors and caller cancellation propagate. Both hosts publish; the combined **332-test**
  selection passes. Actual source ZIP project closure and Channel dependency isolation pass.
- Fresh v13 startup at `17:34:01Z`, readiness HTTP 200, unchanged dedicated runtime identity
  and live Search Index Data Reader / Cognitive Services OpenAI User roles verified.
- Signed-in M365 conversation `3c1bb323-2096-4239-a863-b73bac1cdd4b`, hosted session
  `11c7fbdfe873c093853ed4da1d4a608f787f4af298a8d1156fff624d82f11a6`: exactly one
  margin card, one IT card and one semantic clarification card. Rendered list numbering
  verified, including the second and third option numbers.
- Gross Margin card selection returned **42.52%**, prior **43.21%**, **0.69 pp down**.
  Global IT card selection returned **13.1 M USD** OPEX, prior **12.9 M USD**.
  The semantic paraphrase again called embeddings and Search successfully (HTTP 200 at
  `17:37:15Z` and `17:37:16Z`); text reply **third** returned the same correct Gross Margin.
- All six turns emitted completion after awaited `MarkDeliveredAsync`; the three card
  completions were at `17:34:18.708Z`, `17:35:44.950Z` and `17:37:17.247Z`.
  Durable execution succeeded. Application Insights reported **zero exceptions** for
  `17:33:00Z` through `17:39:00Z`, with no delivery-failure traces.
- Browser reload preserved exactly three cards and three final finance answers, without
  duplicate clarification pairs. Normal confirmed delivery is verified; a crash between
  external send and persistence can still duplicate a message (at-least-once semantics).

Diagnostic boundaries: AppLens returned 401, so diagnosis used existing authorized Application
Insights and hosted-session logs. Direct workstation Blob inspection was blocked by the
intended private network rules; SCM had no managed-identity endpoint. No firewall/identity
setting was relaxed. Delivery evidence uses the post-`MarkDeliveredAsync` completion trace
and rendered conversation, with content-free tombstone shape verified by storage tests.

See [publication operations](docs/resolver-data.md) and the
[customer-shareable IT-admin catalogue](docs/it-admin-catalogue.md). Separate dev/staging/prod
resources and permissions are documented; staging/prod are not implicitly deployed.
The historical deployment and UI evidence below remains evidence for the prior release only.

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
previous deployed build at that checkpoint. The resolver release status is recorded above.

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
| Foundry `zavafinance` / `prj-fdr-swc` | Version 11 was active on 15 September; new-session logs confirmed `2026-09-15.2-native-routing`. Superseded by v13 above. |
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

**Source control is active.** The prior simplification was committed and pushed as `6659534`
on the user's explicit instruction. The resolver changes are intentionally **uncommitted and
unpushed**. Local credential/settings files remain outside the committed source; deployment
inputs must use the approved secure configuration mechanism. Never regenerate the session-key
salt casually: it changes every derived session key and orphans existing conversations.

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
