# Availability and support register

Verified against the linked Microsoft documentation on **17 September 2026**.
This records product, feature, API, package and model status separately; it is not a
production acceptance report or a Microsoft support commitment.

## Hosting and SDKs

| Component | Status and implication |
|---|---|
| Microsoft Agent Framework core | **GA**. The application selects `Microsoft.Agents.AI 1.21.0`. |
| Microsoft Foundry Hosted Agents | **GA service**. A prerelease adapter does not make the entire service Preview. |
| `Azure.AI.AgentServer.Responses 1.0.0-beta.8` | **Prerelease application library**. Supplies the application's HTTP/Responses/SSE adapter inside the hosted container. |
| `Azure.AI.AgentServer.Core 1.0.0-beta.28` | **Transitive prerelease application library**, actively used for Foundry state storage. It is not the Microsoft-managed hosting service. |
| `Azure.AI.AgentServer.Activity 1.0.0-beta.1` | **Unreleased** in the public SDK source inspected on this date; its public NuGet page returned 404. One uses the unofficial, unsigned compatibility build `1.0.0-beta.1.source.dc9cca2d1f1c.core28.m3651877`, approved for **`activity-protocol` only**. It is not added to the original host. |
| Python `agent-framework-foundry-hosting` | **Prerelease integration**, explicitly identified as such in the current hosting documentation. Not used by the standalone Python One implementation. |
| Python One `azure-ai-agentserver-activity 1.0.0b3` | **Published beta adapter**, unlike .NET One's unofficial source build. Installed package metadata confirms the beta status. |
| Python One `azure-ai-agentserver-core 2.1.0` and `agent-framework-core 1.18.0` | **Stable packages**, pinned explicitly. Core 2.2.0b1 is not selected; the Activity adapter remains beta. See the [Python guide](../src/ZavaFinance.One.Python/README.md). |
| Python One Core state API | **Experimental API inside Core 2.1.0**, despite the package's stable version. Typed conversation storage is not durable task recovery. |
| `azure.ai.agents >=1.0.0-beta.4` in the deployment manifest | A **minimum extension version range**, not an exact pin or evidence that the installed extension is prerelease. Record the actual installed version for a release. |
| `dotnet_10`, Responses `2.0.0` / Activity `2.0.0` | Runtime/protocol identifiers, not beta labels. Original exposes Responses; One exposes Activity only. |
| Azure Bot Service | **GA service**. The original Channel template's `2023-09-15-preview` management API remains a separate preview API selection. |
| Foundry Activity publishing | The documented Bot endpoint selects `api-version=2025-05-15-preview`; current agent management/publishing examples use `api-version=v1`. Publishing a Responses agent uses a managed bridge. One instead runs the native Activity host; neither publication nor a healthy startup proves channel OAuth and final card delivery. |
| Foundry long-running response recovery | **Preview feature**. Background execution alone is not crash recovery; see the boundary below. |

The user has chosen to retain Foundry hosting and the original two beta AgentServer libraries;
One adds the separately approved, source-built Activity adapter. The standalone Python
sibling selects the published Python Activity beta instead; it does not remove or upgrade
the original or .NET One packages.
Production support for those exact package versions still requires clarification from the
product group; service GA must not be presented as an answer to that support question.

Project files record **direct** dependencies. A restored dependency graph also records resolved
transitive versions. Neither an older local restore nor this table is a deployed SBOM.

Sources:
- [Agent Framework Foundry hosting: service GA and prerelease integrations](https://learn.microsoft.com/agent-framework/hosting/foundry-hosted-agent)
- [Hosted-agent runtime and adapter responsibilities](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agent-contract)
- [Publish agents to Teams and Microsoft 365](https://learn.microsoft.com/azure/foundry/agents/how-to/publish-copilot-virtual-network)

### Native Activity migration gate

There are two distinct paths. Publishing a Responses agent enables a managed
Responses-to-Activity bridge. Separately, the public .NET SDK source contains a native
Activity host that maps `/activity/messages` to the Microsoft 365 Agents HTTP adapter,
allowing an ordinary `AgentApplication` to run inside the hosted agent.

The native host includes a compiled sample sending Adaptive Card attachments and reading
`Action.Submit` data from `Activity.Value`. That sample establishes the intended API shape,
not an executed Teams/M365 acceptance test. The package changelog still identifies it as
unreleased; the published runtime contract does not yet document that native container route.

| One requirement | Verified boundary / deployment gate |
|---|---|
| Clickable clarification cards and submissions | Implemented with shared cards and the real native queue; local tests pass. Live channel delivery remains unverified. No verified equivalent mapping was found for the Responses bridge. |
| Delegated sign-in and OBO | Interactive sign-in succeeded and cached-token reset works. **Initial silent SSO remains unresolved** after a credential/MFA challenge. Bot transport claims are not the human OBO assertion; cached-token success is not SSO evidence. |
| Long turns without a separate Durable Task Scheduler | One may use the native SDK's in-process background handling. **No crash recovery is claimed or enabled**: a restart can lose an in-flight turn and the user must retry. |
| Recovery-safe authorization | Out of scope for this experiment. Delegated assertions must still never enter persisted finance/session state; see the recovery warning below. |

**Experimental implementation is approved, on `activity-protocol` only.** This supersedes
the earlier blocked decision and the proposal to use Preview response recovery for One.
The source commit linked below is the dependency baseline. Initial silent SSO, live
card continuations, Fabric finance and two-user isolation still require executed
validation before One can be called end-to-end accepted.
Do not remove cards or replace user permissions with agent permissions to pass those tests.
The original Channel, hosted agent, Bot and scheduler remain unchanged.

One version **1** is deployed separately. Live session logs show its native host,
both M365 background services and `/readiness` HTTP **200**. The new Bot's OAuth
connection, scoped model/Search role grants and branded installable package are
configured and verified. One is installed in M365 Copilot web; interactive sign-in
and reset with a cached token succeeded. KPIpedia returned an answer and the
connector acknowledged delivery in native logs; browser content was not independently
verified. This is **deployed, not yet end-to-end accepted**. Local source now includes
a later rejected-invoke fix and 73 native / 142 combined passing cases, not yet redeployed.
The exact dependency pins, reproducible build, configuration commands and
remaining acceptance gates are in the [One implementation guide](../src/ZavaFinance.One/README.md).

The separate **Python One version 2** was deployed on **17 September 2026** with
the published Activity beta and stable Core 2.1.0. Its **198 local tests passed
with no skips**; a hosted Linux startup session passed native SQL prerequisite
loading and `/readiness` returned **HTTP 200**. Its separate Bot, OAuth connection,
scoped runtime roles and install package were verified. The temporary startup
session was deleted. The user installed and reached Python One in Teams/M365,
but received a Sign In fallback. Version 1 logs proved an OAuth card send returned
an empty activity ID and the application's strict delivery check caused HTTP 500,
before the original question's auth continuation was saved. Version 2 accepts a
successful connector response without a message ID only during the OAuth control
send; ordinary finance replies still require an ID and HTTP errors still propagate.
Real new version 2 M365 conversations at **20:01 and 20:12 UTC** still displayed
Sign In, with OAuth delivery HTTP 202 and no token-exchange callback observed.
The shared finance app was corrected to issue **v2 access tokens**; that change
alone did not resolve SSO. Subsequently, an additional Python-routing resource URI
was exposed on the shared app, preserving its original URI, permissions and
credentials. The Python Bot connection and new package **1.0.1** use that alias.
Importing into Developer Portal alone did not update the personal installation,
but installed **1.0.1** was subsequently confirmed at **21:14 UTC**.
**Initial silent SSO passed at 21:17 UTC**: no token initially, successful
`signin/tokenExchange`, resumed original question, delegated KPIpedia HTTP 200,
persisted state and a visible answer without manual Sign In. External Edge's
Azure profile also received a KPIpedia answer at 21:24 using the cached token.
The following analysis question failed at model routing with HTTP 400. Retried
in a fresh conversation at 21:28, it reached Fabric MCP with delegated OBO but
timed out; the explicit timeout reply was delivered to Edge.
Python **version 3** subsequently deployed at approximately **23:42 UTC**, with
**199 passing tests** and hosted readiness HTTP 200. It fixes the model HTTP 400
by JSON-encoding replayed function-call arguments at the SDK boundary, without
changing the persisted state schema or forwarding financial tool outputs.
Two live model turns using the corrected source passed at 23:45; no finance
tools were executed by that diagnostic. Windows was locked during the external
Edge/Azure retest, so no new channel message was submitted. Package 1.0.1, OAuth,
runtime identity and scoped roles were verified unchanged; both .NET versions
remain unchanged. The temporary v3 validation session was deleted.
On **18 September at 06:24 UTC**, a user-submitted v3 APAC analysis again passed
cached-token authentication, model routing and delegated Fabric MCP discovery,
then delivered an explicit timeout roughly 60 seconds later. The Python HTTP
read limit was 60 seconds despite a 300-second overall analysis deadline.
The adapter now aligns read timeout with that budget while preserving
bounded connection/cleanup, cancellation and retry behavior. **42 targeted tests**
and a synthetic loopback HTTP response delayed **65 seconds** passed.
**Version 4 deployed at approximately 06:42 UTC**, following **203 passing tests**
and a clean dependency check. Fresh hosted v4 readiness returned **HTTP 200 at
06:43:09 UTC**; the temporary validation session was deleted. Runtime environment,
identity, Bot endpoint, OAuth, roles, package 1.0.1 and both .NET versions were
verified unchanged. No live analysis or channel message was submitted in this
rollout. It adds neither asynchronous Fabric tasks nor durable restart recovery.
The user confirmed the updated agent was working at approximately **06:51 UTC**.
That user-reported acceptance does not independently establish Fabric duration or
answer accuracy. Detailed analysis, live SQL, current-version channel follow-ups,
card continuations and two-user acceptance remain open.
Initial SSO is verified for this deployment, not guaranteed
for every Foundry configuration; the independent effects of the token-format,
resource and package/client changes were not isolated. Freshly minted shared-app
tokens also need original/.NET live compatibility checks.
Those agent versions and Bot endpoints were not changed. See the
[Python verification record](../src/ZavaFinance.One.Python/README.md#acceptance-and-operational-limits).

Sources:
- [Native Activity package changelog at the inspected commit](https://github.com/Azure/azure-sdk-for-net/blob/dc9cca2d1f1c9f42182a0f1d6cc2540acf5dc956/sdk/agentserver/Azure.AI.AgentServer.Activity/CHANGELOG.md)
- [Native Activity handler implementation](https://github.com/Azure/azure-sdk-for-net/blob/dc9cca2d1f1c9f42182a0f1d6cc2540acf5dc956/sdk/agentserver/Azure.AI.AgentServer.Activity/src/Internal/ActivityEndpointHandler.cs)
- [Native card/submission sample](https://github.com/Azure/azure-sdk-for-net/blob/dc9cca2d1f1c9f42182a0f1d6cc2540acf5dc956/sdk/agentserver/Azure.AI.AgentServer.Activity/tests/Snippets/Sample11Snippets.cs)
- [Hosted-agent protocols and long-running execution](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents)
- [M365 user OAuth and runtime OBO](https://learn.microsoft.com/microsoft-365/agents-sdk/agent-oauth-configuration#obo-exchange-at-runtime)

### Background execution is not crash recovery

Foundry Responses supports background execution and polling, so a long model/tool response
does not inherently require an additional Azure Durable Task Scheduler.

For process-loss recovery, the server must explicitly enable resilient background processing,
and the request must be a **stored background response**. The runtime persists input, maintains
leases and reenters the handler after interruption. Application checkpoints, safe reruns,
duplicate-side-effect prevention and user-visible delivery still need an explicit design.
Foreground calls do not acquire those recovery guarantees.

This applies to the **outer hosted-agent request**, not the router's internal model call.
Do not enable model-output storage or submit permissioned tool answers to the routing model
as a shortcut for durable execution.

The existing Channel still uses Durable Task for its deployed acknowledgement/background/
proactive-delivery flow. Do not remove its scheduler while that Channel remains operational.
One instead uses an **in-memory M365 queue**, not Foundry-managed resilient Responses.
It has no persisted delivery record, retry/replay coordinator or crash recovery.
Its separate Foundry finance state store preserves conversation data, not executing work.
Its native OAuth continuation store is process-local and is not durable or shared.

The earlier Preview-recovery proposal is **not enabled in the current One experiment**.
Any later recovery implementation must address transport/authentication: the selected adapter persists forwarded
client headers verbatim in recovery input. Simply adding `background=true` and `store=true`
to the existing `x-client-user-token` flow would persist the delegated assertion. A supported
fresh-token flow is required before enabling recovery while retaining the current
no-tokens-in-workflow-state design.

Source: [Resilience for long-running hosted agents (Preview)](https://learn.microsoft.com/azure/foundry/agents/concepts/long-running-agent-resilience).
SDK detail: [response recovery payload and client headers](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/agentserver/Azure.AI.AgentServer.Responses/src/Internal/Resilience/ResponseRecoveryPayload.cs).

## Model lifecycle

| Deployment/model | Verified version | Published status |
|---|---|---|
| Original routing/reranking: GPT-4.1-mini | `2025-04-14` | **Legacy**; published retirement date **14 April 2027**. Legacy is not Preview. |
| Selected for Zava Finance One: GPT-5.4-mini | `2026-03-17` | **GA**; published retirement date **21 September 2027**. Existing deployment verified; no duplicate is required. |
| Terminology embeddings: text-embedding-3-large | `1` | **GA**. The active resolver release continues to request **1536 dimensions**. |

A deployment name alone does not establish its underlying model version or lifecycle.
Read the live deployment when approving a release, and recheck Microsoft's lifecycle schedule.
The GPT-5.4-mini selection does not change the internal models used by Fabric or KPIpedia.
No workload-specific comparative latency, price or quality advantage is claimed here.

The application supports GPT-5.4-mini through the explicit neutral setting
`ModelReasoningEnabled=true`, together with `ModelDeployment=gpt-5.4-mini`. Routing and
constrained reranking then use low reasoning effort without temperature, while keeping
internal model calls at `store=false`. One v1 was deployed with those model settings.
The original service retains the default `ModelReasoningEnabled=false` and temperature zero;
the shared code does not change the original cloud deployment.

Sources:
- [Azure OpenAI models](https://learn.microsoft.com/azure/foundry/openai/concepts/models)
- [Model retirement and lifecycle](https://learn.microsoft.com/azure/foundry/openai/concepts/model-retirements)
- [Reasoning-model API parameter compatibility](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/reasoning)

## Fabric feature boundaries

| Capability | Current finding |
|---|---|
| Fabric capacity, OneLake, lakehouse, Spark notebooks and ordinary SQL analytics endpoint queries | **GA baseline capabilities**. They do not require an AI reasoning preview. |
| Fabric Data Agent core | **GA since March 2026**. Do not label the whole service Preview. |
| Data Agent Standard runtime | **GA**, default for newly created agents. A previously published agent is not proof of a Standard-runtime deployment. |
| Data Agent Preview runtime | **Preview**. A published agent retains its selected runtime until republished with a different selection. |
| Advanced NL2SQL / Advanced DAX generation | **Preview** features; separate from core Data Agent availability. |
| Copilot Studio Fabric IQ Data MCP tool integration | **GA**, recorded in the August 2026 release log. This does not automatically upgrade other integration paths. |
| Foundry Data Agent / Fabric IQ integrations | Current integration-specific documentation still marks these **Preview**. |
| Custom-client Data Agent MCP | Current documentation removed Preview labels in September 2026. A separate explicit GA announcement for every custom-client/legacy route was not established. |
| Application's older `/aiskills/.../mcp` route | Exact current support/status is **unconfirmed**. Do not infer its GA status or automatic migration from another integration's announcement. |

The current documented custom-client route is
`https://api.fabric.microsoft.com/v1/mcp/workspaces/{WorkspaceId}/dataagents/{DataAgentId}/agent`.
Changing the application to that route requires a separate compatibility and delegated-access
test; this audit does not silently change it.

The legacy `Fabric:DataAgentApiVersion=2024-05-01-preview` option is not consumed by the current
MCP calls. It is not an active preview API dependency merely because it remains in configuration.

Sources:
- [Fabric Data Agent concepts](https://learn.microsoft.com/fabric/data-science/concept-data-agent)
- [Runtime selection and publication](https://learn.microsoft.com/fabric/data-science/data-agent-runtime)
- [Custom-client MCP endpoint](https://learn.microsoft.com/fabric/data-science/data-agent-mcp-server)
- [SQL source options](https://learn.microsoft.com/fabric/data-science/data-agent-sql-sources)
- [Semantic model and DAX options](https://learn.microsoft.com/fabric/data-science/semantic-model-best-practices)
- [Foundry integration](https://learn.microsoft.com/fabric/data-science/data-agent-foundry)
- [Foundry Fabric IQ integration](https://learn.microsoft.com/fabric/data-science/data-agent-foundry-fabric-iq)
- [Copilot Studio Fabric IQ Data MCP tool](https://learn.microsoft.com/fabric/data-science/data-agent-microsoft-copilot-studio-tool)
- [Fabric release log](https://learn.microsoft.com/fabric/fundamentals/whats-new)

## Release approval

Record the actual agent version, runtime identity, model version/SKU, package graph, selected
APIs, published Fabric runtime and integration route. Obtain explicit approval for remaining
preview/prerelease selections and verify support terms independently. Historical deployment
and test evidence in the [handover](../HANDOVER.md) remains historical; a corrected GA label
does not mean a newer architecture has been deployed or passed acceptance.
