# ZavaFinance

A finance agent for **Microsoft Teams and Microsoft 365 Copilot**, split across two hosts: a
Foundry **hosted agent** that owns routing and tools, and a thin **channel** that owns identity,
acknowledgement, and delivery.

## Native function calling, verbatim answers

The hosted agent exposes the three finance tools as native function schemas. The model selects
at most one function and supplies its arguments; application code validates that call and
executes the selected tool with the signed-in user's delegated identity.

Tool registration lives directly in `OrchestratorAgent`: it lists the three tool classes and
discovers their public instance methods marked `[OrchestratorTool]`. The attribute supplies the
tool name; `[Description]` annotations and method signatures supply the descriptions and argument
schemas. There is no per-method registration list, assembly-wide scan, or separate catalog class.
The same annotation discovery locates the selected method for execution. `AIFunctionFactory`
binds it to a caller-specific tool instance, and `AIFunction.InvokeAsync` binds the native
arguments (including optional defaults and cancellation). There is no tool-name dispatch switch
or tool-specific argument DTO. Lazy factories only wire each tool's dependencies; unselected
clients are not created and bound functions are never shared between callers. The agent uses
the SDK's `AgentResponse` and `FunctionCallContent` directly, with a single `ValidateToolCall`
method for pre-execution checks rather than a custom selection-result wrapper.

There is **no second model call to compose the answer**. The tool's complete response, including
citations, figures, formatting, and its source footer, is returned directly. Native function
calling does not require model-authored answer synthesis.

Conversation history contains questions, function calls, and content-free tool-result markers
that close those calls for subsequent turns. It never contains permissioned tool answers or
access tokens. A missing business argument is handled by the tool's existing clarification
logic; an unknown function, malformed argument, or multiple function calls is rejected before
execution.

Model requests use local history with Responses API output storage disabled. On the first turn
after upgrading from structured routing, the old model history is reset; the per-user KPI and
Copilot Studio conversation handle are retained. Invalid model calls are not replayed.
`Orchestrator:MaxHistoryMessages` defaults to 20 and must be at least 3. History is trimmed
before routing and persistence at whole-turn boundaries, so calls and result markers stay paired.

It answers three kinds of question, each as the signed-in user:

| Tool | Backed by | Shape |
|---|---|---|
| `get_kpi_info` | Copilot Studio agent | Natural language, slow (~51 s) |
| `get_statement` | Local resolver and Fabric lakehouse SQL | Canonical, permission-checked arguments and deterministic figures |
| `explore_finance` | Fabric data agent over MCP | Open-ended, 25 s to 3 min |

> The finance dataset describes **Zava**, a fictional company. No real financial data is in this
> repository.

## Terminology resolver and clarification

`get_statement` keeps SQL fixed and parameterized. The model supplies the user's raw terms;
an **in-process resolver** matches Fabric-owned KPI and organization metadata before querying.
Known unambiguous codes/aliases resolve directly. Genuine ambiguity produces a clarification,
not a `TOP (1)` guess. Azure AI Search supplies keyword/vector/semantic candidates for
paraphrases; only permitted, revalidated catalogue IDs may be selected. A model never writes
the statement SQL or recomposes its financial answer.

Every semantic suggestion requires confirmation, even when only one candidate remains.
Only unambiguous exact metadata matches execute directly. Search index, embedding deployment
and dimensions are bound together by the active Fabric release; deployment settings provide
only the Search/model account endpoints and resolver behavior. Runtime retrieval uses the
hosted agent's dedicated Entra identity, not the project's infrastructure managed identity.
Fabric metadata and financial SQL are still authorized as the signed-in user.

The reporting hierarchy is **Company > Region > Department group > Department**, with global
department/group scopes. Branches expand to distinct existing fact-key pairs. KPI families
are navigation only; margins and other ratios still come from the existing component calculator.
The additive export has **114 entities and 336 scope rows**, preserving the original facts.

The Channel Service presents KPI/organization ambiguity as Adaptive Cards with numbered,
clickable rows and hierarchy context, built from structured clarification options. Clicking
anywhere on an option submits that choice immediately; there are no radio buttons or separate
Continue action. The agent also retains the numbered text result for compatibility, and users
can still reply with an option number. Pending requests and selections belong to the validated
caller and conversation, carry a catalogue version and expire; reset/stale/forged choices
must not execute a statement. Cached typed replies preserve the existing delivery retry rules.

- [Data model and publication runbook](docs/resolver-data.md)
- [IT-admin dependencies, environment tiers, RBAC and Entra catalogue](docs/it-admin-catalogue.md)
- [Architecture diagram](.github/modernize/assessment/engines/facts/architecture-diagram.md)
- [Swimlane and clarification sequence](.github/modernize/assessment/engines/facts/swimlane-diagram.md)
- [Editable Excalidraw architecture](docs/zavafinance-architecture.excalidraw)

Signed-in resolver acceptance is recorded separately in the
[current handover](HANDOVER.md#resolver-update---16-september-2026). Historical version-11
results below remain evidence for that earlier release, not a replacement for resolver tests.

## Why it is split

The same finance agent is reachable two ways:

```
Teams / M365 Copilot ─► Bot Service ─► ZavaFinance.Channel ─┐
                                                            ├─► ZavaFinance.Agent ─► tools
Any Responses API client ───────────────────────────────────┘      (Foundry hosted)
```

The channel is **not** the only front door. Anything that can call the Responses API — a web app,
another agent — reaches the same agent with the same per-user isolation, without Teams in the
path. That is the reason for the split, and it is what makes the agent testable from the Foundry
playground rather than only through a real Teams turn.

The split is not free. There is an extra hop, so a dead turn is now the channel, the gateway or
the container; the user assertion crosses the wire and must be kept out of logs at three points;
this solution owns the OBO lifecycle rather than delegating refresh to the SDK; and nothing is
removed operationally — the bot app, OAuth connection, Function App, Durable Task and blob storage
all remain, plus a container, a registry and an agent deployment.

## The header that makes it work

Foundry forwards only headers prefixed **`x-client-`** to a hosted-agent container, and
deliberately does **not** forward `Authorization`. The adapter documents this on the property
itself:

> `ResponseContext.ClientHeaders` — *Gets the forwarded client headers (those prefixed with
> `x-client-`) from the original HTTP request.*

A separate header is required because one bearer token cannot carry two audiences:

| Hop | `Authorization` | `x-client-user-token` |
|---|---|---|
| Teams → channel | Bot Service JWT — proves the caller is Bot Service | — |
| Channel → Foundry | Channel's managed identity — authorizes the call | the user's assertion, forwarded unchanged |
| Agent → Copilot Studio / Fabric | the OBO result — acts **as the user** | not sent |

Foundry neither authenticates nor interprets the header. **This solution owns its validation**,
and that ownership is the security-critical part of the design.

### The assertion is validated before its claims are used

The session key — the isolation boundary for all per-user state — is derived from the `tid` and
`oid` claims of the forwarded assertion. [`UserAssertionValidator`](src/ZavaFinance.Agent/UserAssertionValidator.cs)
verifies signature, issuer, audience and lifetime against the tenant's OpenID Connect metadata
**first**, and a token that fails ends the turn.

It is not sufficient to argue that a forged assertion would fail the later OBO exchange. That
check happens *after* the session key has already been used to load and later overwrite state, so
a forgery that never obtains a downstream token could still read and poison another user's
session. `UserAssertionValidatorTests` pins this.

## Layout

```
src/ZavaFinance.Agent/     Foundry hosted agent:
                             Agent/          routing agent and session state
                             Tools/          get_kpi_info, get_statement, explore_finance
                             Finance/        KPI catalog, period parsing, Fabric clients
                             Identity/       dependency-free shared identity project
                             Contracts/      dependency-free reply and clarification protocol
                             Abstractions/   IDownstreamTokenProvider
src/ZavaFinance.Channel/   Function App: Teams + M365 Copilot, ack, proactive delivery
tests/ZavaFinance.Tests/   isolation, finance maths, tool failures, routing golden set
appPackage/                Teams + Microsoft 365 Copilot manifest and icons
scripts/                   Fabric metadata and Search publication/verification
infra/                     Channel and resolver infrastructure and tier parameters
docs/                      Data publication and IT-admin catalogue
```

The channel references the shared **Identity and Contracts** projects, not the hosted-agent
executable. They contain caller/session identity and the typed reply protocol. They are deliberately
nested under the agent's upload directory: code deploy uploads one project directory, so an
external sibling dependency would be absent from the remote build. Agent and channel both
reference the same identity assembly; routing, tools, and finance dependencies stay agent-only.

The hosted agent binds tools to one caller through:

```csharp
public interface IDownstreamTokenProvider
{
    Task<string> GetTokenAsync(string[] scopes, CancellationToken cancellationToken);
}
```

`OboTokenProvider` performs the MSAL On-Behalf-Of exchange. The channel forwards the original
user assertion and never constructs these tools. Hosted execution and the golden set use the
same routing definition.

### State ownership

| Host | Persisted application state | Store |
|---|---|---|
| Channel | Platform conversation ID; pending, answer-ready, and delivered turn records including cached typed replies | Private Blob Storage |
| Agent | Bounded routing history, latest KPI, KPIpedia conversation ID and caller-bound pending clarification | Foundry State Store |

`FoundrySessionStore` reads and writes one known session type without runtime CLR type loading.
It preserves existing encoded keys and reads the old type/value envelope, ignoring obsolete
channel and last-tool fields. New writes use a plain JSON session object. Storage initialization
remains lazy and the configured TTL is retained.

Completed finance answers are cached before channel delivery. Retries reuse that answer rather
than repeat the finance call. A delivered record suppresses later redelivery; a process failure
between the external send and recording delivery can still duplicate a message. This is not an
exactly-once transport guarantee. Questions, answers, and user tokens do not enter Durable Task
inputs or outputs.

The MCP client uses the official `ModelContextProtocol.Core` transport for initialization,
JSON-RPC and HTTP/SSE. Only Fabric-specific tool selection, delegated authentication, result
extraction and retry policy remain in this project.

Agent and channel configuration are separate types. The channel retains the `Orchestrator`
configuration-section name for compatibility with existing deployment settings.

## Package status

Everything is GA except one package, and it is confined to one project:

| Package | Status | Where |
|---|---|---|
| `Microsoft.Agents.AI` 1.21.0 | GA | Agent |
| `Microsoft.Agents.CopilotStudio.Client` 1.8.77 | GA | Agent |
| `Microsoft.Agents.Storage.Blobs` 1.8.77 | GA | Channel |
| `ModelContextProtocol.Core` 2.2.0 | Stable | Agent MCP transport |
| `Microsoft.Data.SqlClient` 6.1.4 | GA | Agent |
| `Microsoft.Agents.Hosting.AspNetCore` 1.8.77 | GA | Channel |
| `Microsoft.Azure.Functions.Worker.Extensions.DurableTask` 1.16.4 | GA | Channel |
| `Microsoft.Identity.Client` 4.89.0 | GA | Agent |
| **`Azure.AI.AgentServer.Responses` 1.0.0-beta.8** | **preview** | Agent only |

Exactly one preview package, confined to one project. The routing conversation is serialized into
this solution's own store rather than a durable entity, which is what keeps
`Microsoft.Agents.AI.DurableTask` and `Microsoft.Agents.AI.Hosting.AzureFunctions` — neither of
which has a GA release — out of the dependency set entirely.

### On the one preview package

`Azure.AI.AgentServer.Responses` had breaking changes in beta.2, beta.6 and beta.8, and beta.8
removed public types outright. All of that churn is in **resilient background execution, steerable
conversations and stream providers** — none of which this solution uses, because responses are
returned whole and conversation state is owned here. The surface actually consumed
(`ResponseHandler.CreateAsync`, `ResponseContext.ClientHeaders`, `GetInputTextAsync`) has been
stable since beta.1.

Pinned deliberately. Do not adopt the resilience or steering APIs without re-reading that
changelog.

## Known limitation: progress cannot name the tool

Routing happens inside the hosted agent, so the channel learns which tool ran only when the answer
is already back. The acknowledgement is therefore tool-neutral — the user sees *"Working on that…"*
for the whole wait, which can exceed a minute — and it cannot say *"Analysing that in the finance
data agent…"* however much that would help.

This is pinned by `AcknowledgementCannotNameTheToolInThisTopology` so it cannot be quietly
reintroduced as a bug. Fixing it properly means having the agent stream its routing decision so the
channel can relay it, which trades the "responses are returned whole" rule for partial output.

The latency budget is unrelated to any of this: Bot Service times out at 10–15 s and the slow tools
take far longer, so acknowledge-then-answer-proactively is mandatory regardless.

## Every answer names its source

All three tools append a one-line attribution, from a single definition in `SourceFooter`:

| Tool | Footer |
|---|---|
| `get_statement` | `Source: Zava finance lakehouse. <KPI> = <formula>.` |
| `explore_finance` | `Source: Zava finance data agent (Microsoft Fabric).` |
| `get_kpi_info` | `Source: KPIpedia (Copilot Studio).` |

This was originally true of `get_statement` only, because that answer is composed here while the
other two are passed through verbatim from a subagent. The asymmetry was not deliberate, and it was
backwards: the two natural-language tools are precisely the ones whose text is generated elsewhere,
so they are the ones where naming the system matters most.

The footer is **appended after** the answer, never woven into it. The subagent answers are returned
verbatim on purpose — a paraphrased figure is a wrong figure, and a dropped citation is an unsourced
claim — so attribution has to be additive or it would defeat the passthrough. `SourceFooter.Append`
is a no-op when the answer already carries a footer, so a tool that composes its own source text
cannot end up with two.

## Verification

```powershell
dotnet build
dotnet test
```

For an explicitly offline run, exclude the live routing evaluation:

```powershell
dotnet test --filter "FullyQualifiedName!~RoutingEvalTests"
```

The offline suite covers native calls, caller isolation, state migration, bounded history,
ordinary-message delivery/retries, MCP transport, and deterministic finance calculations.

### Resolver acceptance - 16 September 2026

The additive Fabric release and independent Search index were published, verified and activated:
**114 catalogue entities, 336 scope rows**, eight generator tests and 13 delegated SQL checks.
The final combined resolver, routing, state, formatting and Channel/MCS transport Release
selection passed **332 tests**, zero failed/skipped. Both hosts published successfully.
This supersedes the earlier overlapping 202-test Agent and 86-test Channel selections.
Hosted Agent **v13** and the corrected Channel package are deployed.

Signed-in Microsoft 365 testing exercised the deployed resolver:

| Check | Verified result |
|---|---|
| Exact statement | EMEA November 2025 net revenue **85.6 M USD**, matching SQL **85,639,562.37** |
| KPI clarification | Gross Margin selected from the margin card: **42.52%**, prior **43.21%**, **0.69 pp down** |
| Organization clarification | IT card distinguished regional and global scopes; Global IT OPEX **13.1 M USD** |
| Semantic retrieval | Raw paraphrase exercised runtime embeddings and Search, both HTTP 200; confirmation required before SQL |
| Text compatibility | Both **third** and **2** resumed the correct authorized pending choices |
| Hierarchy scopes | November OPEX: Company **199.6 M USD**, EMEA Corporate **27.0 M USD**, Global IT **13.1 M USD** |
| Closing headcount | EMEA November 2025 **3,258 FTE**, not a sum across months |
| Reset protection | Replayed pre-reset card explicitly rejected with no valid pending choice |
| Confirmed delivery | Corrected v13 retest: one card per clarification, six post-persistence completions, zero exceptions in the test window; three cards and three finance answers survived browser reload |

These verify the deterministic statement/resolver path, not the separate Fabric Data Agent's
historical narrative-accuracy issue. See the handover for the Channel delivery correction,
deployed build IDs and final acceptance evidence.

**Clickable-item follow-up, 16 September at 18:05 UTC:** Channel-only deployment replaced
radio/Continue selection with direct row submissions; Agent v13 and infrastructure are unchanged.
All **90 targeted Channel tests** passed. A fresh signed-in Microsoft 365 conversation verified
Gross Margin and Global IT row clicks plus a typed `2`; three cards and three correct finance
answers persisted after reload, with six confirmed deliveries and no exceptions in the test window.
Previously sent/cached cards keep their original layout; start a new clarification to see the update.

### Historical simplification acceptance - 15 September 2026

The local simplification passed **222 targeted offline regression tests** (zero failures or
skips). Both hosts built and published successfully. The agent also passed isolated
single-directory publishing, local readiness, and missing-user-assertion checks; the channel
publish contains the shared identity library without the hosted-agent runtime or MCP packages.
On 15 September 2026 both simplified hosts were deployed: Foundry agent version **11**
(`2026-09-15.2-native-routing`) and the existing Functions channel. Fabric was resumed on **F2**.
Channel health, function indexing, Durable Task connectivity, and agent rejection of missing or
wrong-audience user assertions passed.

Signed-in Microsoft 365 Copilot testing on 15 September exercised the deployed version 11 in a
fresh conversation:

| Check | Result |
| --- | --- |
| KPIpedia explanation | Passed: full gross-margin definition, calculation and source footer. |
| Contextual SQL follow-ups | Passed: EMEA November 2025 gross margin **42.52%**, then Q4 **42.67%** without repeating the KPI or geography. |
| Closing headcount | Passed: EMEA Q4 2025 **3,247 FTE**, using the closing month rather than summing months. |
| Fabric MCP and final delivery | Passed: both analysis requests returned full answers; all six finance turns had one final response, with progress before, never after, the final. All six answers persisted after reloading the browser. |
| Fabric analysis accuracy | **Failed**: the ordinary question used gross revenue instead of net revenue (**38.86%** instead of **42.52%**). Supplying the correct formula recovered the margins but still produced an incorrect change (**-1.21 pp** instead of **-0.69 pp**). |

This is **not a fully passing finance end-to-end result**. Correct the published Fabric data
agent's KPI/query guidance and calculated changes upstream; the orchestrator must continue
returning its answer verbatim. See [the handover](HANDOVER.md#what-is-open).
Teams could not be exercised: both Teams web hosts redirected to "Classic Teams is no longer
available." Two-user RLS and live cancellation/failed-send recovery remain unverified.
KPIpedia's reference-style citation arrived in the raw M365 announcement, but did not render as
a clickable citation; its source footer and explanation did render.

To run the live golden set:

```powershell
$env:Foundry__ProjectEndpoint = "https://<resource>.services.ai.azure.com/api/projects/<project>"
$env:Foundry__ModelDeployment = "gpt-4.1-mini"
az login
dotnet test --filter "FullyQualifiedName~Routing"
```

The current native routing golden set passed **38/38** cases against the live model, plus **19**
annotation/golden-set contract checks. Testing caught and corrected a headcount status question
being expanded into an unasked trend analysis; the correction is in the tool annotations, not a
tool-selection switch. These tests exercise routing, not delegated finance tools or channel UI.
The earlier **54/54** routing result belongs to the historical pre-simplification test set.

## Publishing to Teams and Microsoft 365 Copilot

One package covers both surfaces. This is a **custom engine agent**.

```powershell
./scripts/build-app-package.ps1 -BotId <botAppId> -AppHostName <app>.azurewebsites.net
```

Then **Apps → Manage your apps → Upload an app**.

Three rules, each learned the hard way:

1. **`copilotAgents.customEngineAgents` requires the referenced bot to have `personal` scope**,
   and the app short name and short description must be defined.
2. **Declare `copilot` in `bots[].scopes` explicitly.** Schema 1.21 added it as the Copilot-surface
   scope. An agent whose installed package advertised only `personal` and `team` was visible in
   Copilot but returned *"Sorry, I wasn't able to respond to that"* — with no request ever reaching
   the app, because the failure was upstream.
3. **Bump `version` on every manifest edit**, or Teams silently ignores the update. Copilot's agent
   list also caches separately from Teams, so the two surfaces can disagree for minutes to hours.

## Prerequisites

- .NET 10 SDK
- A Foundry project with a chat model deployment
- A dedicated Azure AI Search service with semantic ranker and a compatible embedding deployment
- A staged, validated and activated Fabric resolver catalogue and matching versioned Search index
- A published Copilot Studio agent with user authentication
- A Fabric **F2 or higher** capacity, with a lakehouse and a published data agent
- An Entra app registration with delegated permissions and **tenant-wide admin consent**:
  `CopilotStudio.Copilots.Invoke`, Graph `User.Read`, Azure SQL `user_impersonation`,
  Power BI `Item.Execute.All`

Without admin consent, `.default` returns a token that silently lacks the scope and the downstream
call fails with an opaque 401.

### Credentials

The OBO confidential client prefers a **managed-identity federated credential**
(`api://AzureADTokenExchange`) over a client secret, which keeps the "managed identity, no keys"
rule intact and avoids a private endpoint for a single value. Set `Obo:ClientSecret` only where a
federated credential is unavailable.

## Operational notes

### A deployed agent does not reach existing conversations

Hosted-agent containers are **warm per session**, and a session is bound to its conversation. A
conversation that keeps being used keeps its container — and therefore keeps running the build
that was live when that container started. Deploying a new version does not migrate it.

This is worth knowing before it wastes an afternoon. A fix deployed correctly, verified active,
and confirmed by the startup marker still reproduced the original exception for four consecutive
test cycles, because the conversation under test was pinned to a container from an hour earlier.
New conversations were already running the fix.

Two things make it diagnosable:

- **`ThisAssembly.BuildMarker`** is logged at startup and participates in the package content, so
  it both forces a new version and answers "which build is actually running?" in one query.
- **`reset`** deletes the caller's session state, including the stored conversation id, so the
  next turn creates a fresh conversation and a fresh container. After deploying the agent, reset
  before testing.

Stack-trace line numbers are the other tell, and they are more reliable than they look: an async
frame reports the real await site, so a line that no longer matches the source means the running
assembly is older than the source — not that the fix was wrong.

### One ordinary-message delivery path

The channel sends an immediate ordinary acknowledgement, occasional ordinary progress messages,
and one ordinary final answer. It no longer starts a stream, intercepts stream frames, or falls
back between streaming and non-streaming presentation. Progress stops before the final send.
The tradeoff is deliberate: status messages remain in the conversation instead of being replaced
in place.

The earlier implementation needed stream-specific fallbacks because Microsoft 365 Copilot
accepted later streamed frames without rendering them, including the final answer. Ordinary
message activities were observed to work on both surfaces. That history is why the feature was
removed rather than merely deleting its delivery safeguards.

For each environment rollout, verify a fast statement, a slow KPI/analysis turn, clarification
delivery and cancellation on both Teams and Microsoft 365 Copilot. Local tests do not prove
channel rendering; the dated acceptance sections distinguish actual live checks from open gates.

### `azd deploy` is content-addressed

An unchanged package is skipped and the previous version keeps serving, reported as a successful
deploy in about ten seconds. Bump the build marker to force a version.

### Storage is private-only in this tenant

Policy forces `publicNetworkAccess: Disabled` on storage accounts, and setting it back to
`Enabled` is silently reverted. The hosted agent therefore uses the platform's own
`FoundryStateStore` rather than a customer storage account, which removes the storage account,
the VNet, the private endpoints and the private DNS zones from the agent tier entirely. The
channel still needs all of them, because Durable Functions does.

### One bot app id, one Azure Bot Service

`Failed to store new bot. MsaAppId is already in use` is the error when two Bot Services share an
app registration. The app can be reused for OBO, but not for a second bot.

### Reserved environment variables

`FOUNDRY_*` and `AGENT_*` are reserved for the platform. Declaring `Foundry__ProjectEndpoint` is
rejected at deploy time with a `ValidationError`; the endpoint is injected, so bind it from
`FOUNDRY_PROJECT_ENDPOINT` and carry anything else under a neutral name.

### Conversation ids are issued by the platform

The Responses endpoint validates the identifier and rejects anything it did not issue —
`Invalid conversation id '…', Malformed identifier`. Create a conversation through the
conversations endpoint and store its `conv_…` id per session; reusing it is what keeps the
agent's state sticky across turns.

## License

[MIT](./LICENSE)
