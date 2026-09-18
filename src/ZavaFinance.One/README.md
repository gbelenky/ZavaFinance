# Zava Finance One Activity application

Experimental native Microsoft 365 Activity boundary. This is a **library**, not an
entrypoint. The separate One host owns `ActivityServer.Run`, authentication,
configuration, and the explicitly approved source-built, unreleased Activity host
SDK. This library uses stable Microsoft 365 Agents SDK **1.8.77**.

The executable is [ZavaFinance.One.Host](../ZavaFinance.One.Host/). It uses
`DigitalWorker=false`, GPT-5.4-mini with low reasoning effort, and the separate
`zavafinance-one-sessions` store and `ONE_SESSION_KEY_SALT`.

The original channel and Responses agent remain separate. The only project
references are the shared [Activity helpers](../ZavaFinance.Activity/) and the
existing [finance engine](../ZavaFinance.Agent/). No Channel, Functions, or Durable
Task dependency is pulled into One.

## Host integration contract

### Branch contract

| Concern | Original Zava Finance | Zava Finance One |
| --- | --- | --- |
| Channel boundary | Separate Functions Channel and Bot | Separate Bot calling the Foundry Activity endpoint |
| Hosted entrypoint | Responses handler | Thin `ActivityServer.Run<FinanceActivityApplication>` host |
| Routing and reranking | GPT-4.1-mini, temperature zero | GPT-5.4-mini, low reasoning, no temperature |
| Background work | Channel Durable Task Scheduler | Native M365 in-memory queue |
| Interrupted work | Channel retry and persisted delivery records | No recovery, replay, or persisted delivery record; user retries |
| Finance state | `zavafinance-sessions` | Separate `zavafinance-one-sessions` and HMAC salt |
| Human permissions | Validated delegated assertion and application OBO | Same validation and OBO; no agent-identity finance fallback |

Persisted finance conversation state is **not durable task execution**. Native
OAuth continuation state is separate again: process-local `MemoryStorage`, not
shared across instances or preserved across restarts. Neither store resumes an
interrupted tool call.

The [official Python Activity echo sample](https://github.com/microsoft-foundry/foundry-samples/tree/main/samples/python/hosted-agents/bring-your-own/activity/echo)
uses the same thin-host pattern: a protocol host plus M365 activity handlers.
One already uses its .NET equivalent. The echo sample does not demonstrate human
SSO, delegated finance access, clarification binding, or crash recovery.
The original Responses package remains a transitive dependency through the
reused Agent library, but One does not start that entrypoint or expose a Responses route.

See the [One architecture](../../.github/modernize/assessment/engines/facts/architecture-diagram.md#zava-finance-one-application-architecture)
and [native turn swimlane](../../.github/modernize/assessment/engines/facts/swimlane-diagram.md#zava-finance-one-native-activity-turn).
The original deployment is retained, not repointed.

For the separate full Python implementation, see
[Zava Finance One Python](../ZavaFinance.One.Python/README.md). Its runtime,
Bot, package and session store are distinct; this guide continues to describe .NET One.

### Registration

Call `AddFinanceActivity()` on the service collection after the shared
`AddFinanceServices(...)` registration. Use a distinct One session store name and
session-key salt. The typed `ActivityServer.Run<FinanceActivityApplication>`
registration supplies the concrete native agent, `IActivityTaskQueue`, and
`HostedActivityService` through M365's `AddAsyncAdapterSupport()`. Do not add
duplicate queue or hosted-service registrations. The concrete agent registration
is required because queued clarification continuations specify that agent type.

`FinanceActivityApplication` constructor dependencies:

| Dependency | Registration owner |
| --- | --- |
| `AgentApplicationOptions` | Native Activity/M365 host |
| `IActivityAssertionValidator` | `AddFinanceActivity()`; delegates to `UserAssertionValidator` |
| `IFinanceActivityRunner` | `AddFinanceActivity()`; delegates to `OrchestratorAgent.RunReplyAsync` with the existing routing `AIAgent` |
| `IConfidentialClientApplication` | Shared `AddFinanceServices()` |
| `SessionKeyProvider` | Shared `AddFinanceServices()` |
| `ChannelOptions` | Host; named user-authorization handler defaults to `mcs` |
| `IActivityTaskQueue` | Already supplied by the typed Activity host through M365 `AddAsyncAdapterSupport()` |
| `ILogger<FinanceActivityApplication>` | Host logging |

Configure `mcs` user authorization to obtain a delegated assertion for the existing
authorized finance OBO application audience. The One Bot has a different application
identity; the OAuth application itself is reused, not recreated. `Obo:TenantId`, `Obo:Audience`, and the confidential
client must agree with that OAuth connection. There is no app-only fallback.
Use in-memory M365 sign-in state for this non-recovering experiment; never put
user assertions in configuration, finance session state, queue payloads, or logs.
Disable the optional typing timer if the transport requires exactly one outgoing
answer message.

## Execution and acknowledgements

- Messages run inside the native host's M365 background handler. They await the
  finance operation; this library does not start detached tasks.
- Supported `adaptiveCard/action` and `task/submit` invokes first authenticate,
  cryptographically validate the user assertion, cross-check the activity's
  tenant/user, and parse the shared clarification payload.
- Invokes do **not** wait for finance. A normalized message is submitted to
  M365's `IActivityTaskQueue`, then the invoke receives its protocol
  acknowledgement. A stopped queue produces a failure acknowledgement instead.
- Rejected clarification invokes return a protocol error without enqueueing
  finance work or attempting a buffered chat reply. Message-turn errors still
  produce a single safe text reply with a required delivery acknowledgement.
- Queued messages explicitly use `DeliveryModes.Normal`, even if the invoke used
  `stream` or `expectReplies`, so the result is delivered through the connector
  rather than the completed invoke's HTTP response. Adaptive Card success
  acknowledgements use `application/vnd.microsoft.activity.message`.
- The queued item contains a cloned activity and the channel's authenticated
  service identity, not a user assertion or the original turn context. Its fresh
  message turn reacquires and validates the user's token before session access.
- The native `HostedActivityService` directly calls the queued adapter's
  `ProcessActivityAsync` with the host stopping token and a fresh DI scope by
  default. It does not re-enter the HTTP handler or enqueue the message again.
  Work may start before the inbound turn finishes; it must not depend on the
  inbound turn state having been saved.
- Exactly one answer message is sent: either plain text or one shared clickable
  Adaptive Card attachment, never mixed text and attachment. A missing or blank
  delivery acknowledgement ID is an error. Invoke protocol acknowledgements are
  not answer-delivery IDs.
- The queue is **in-process and non-durable**. Process loss can lose accepted work.
  There is no replay store, detached custom queue, separate DTS, or crash recovery.
- Ordinary messages receive native HTTP acceptance, not a user-visible
  "Working on that" message. One has no Channel progress loop and disables the
  optional typing timer.

Reset commands, generic clarifications, typed choices, card selections, caller
ownership checks, finance routing, tool execution, and session behavior are all
owned by the existing orchestrator. No second synthesis/model call is introduced.
In One, `reset` clears finance state only. It does not sign the user out, clear
the Bot token cache, replace a Foundry runtime session, or load a newly deployed
version. Use a new channel conversation and check runtime/version evidence after
deployment; a successful reset is not an initial-SSO test.

## Reproducible build and isolated deployment

Prerequisites: branch `activity-protocol`, installed .NET SDK **10.0.303**,
PowerShell 7.2+, Git, Azure CLI/Bicep, and the existing authorized `zavafinance` azd
environment. The deployment was verified with azd **1.33.0** and its
`azure.ai.agents 1.0.0-beta.13` extension. No container registry is needed for this
bundled .NET code deployment.

The host selects exactly
`Azure.AI.AgentServer.Activity 1.0.0-beta.1.source.dc9cca2d1f1c.core28.m3651877`.
[The build script](../../scripts/build-experimental-activity-sdk.ps1) verifies the
public source at Azure SDK commit `dc9cca2d1f1c9f42182a0f1d6cc2540acf5dc956`,
retains licenses/provenance/checksums and creates a portable local NuGet feed. It
deliberately uses Core **beta.28** and M365 **1.8.77**, rather than the source
project's Core beta.30/M365 1.6.150 declarations. This is an **unsigned,
unofficial experimental compatibility build**, not a public NuGet release.

From the repository root:

```powershell
.\scripts\build-experimental-activity-sdk.ps1 `
  -WorkDirectory C:\scratch\zava-one-sdk `
  -OutputDirectory .\.artifacts\one-sdk

dotnet restore .\src\ZavaFinance.One.Host\ZavaFinance.One.Host.csproj `
  --locked-mode
dotnet publish .\src\ZavaFinance.One.Host\ZavaFinance.One.Host.csproj `
  -c Release --no-restore `
  -o .\.artifacts\one-host\publish

azd deploy zavafinance-one --environment zavafinance --no-prompt
```

The existing environment already contains the separate `ONE_SESSION_KEY_SALT`.
Preserve it across redeployments; replacing it orphans existing One conversation
keys. For a new environment, configure a new cryptographically random salt
before the first deployment, alongside the existing OBO/Fabric/resolver settings.
Never print or commit those settings.

The host has its own [dependency lock](../ZavaFinance.One.Host/packages.lock.json)
and ignored, isolated package cache under `.artifacts\one-sdk`. To reproduce the
SDK dependency graph in a fresh scratch directory, also pass the retained feed's
`packages.lock.json` as `-DependencyLockFile` to the build script.

The checked-in lock is **portable `net10.0`**, not RID-specific. The host sets
`SelfContained=false` and `UseAppHost=false`; Foundry starts the published DLL
with its `dotnet_10` runtime. Adding `-r linux-x64` to the locked restore requests
a different dependency graph and fails with `NU1004`. A deliberate RID-specific
build requires its own reviewed lock update, not disabling lock checks.

The bundled publish includes sibling project references. A host-only publish
target excludes the original Agent executable's bootstrap files, but retains its
DLL as the shared finance engine. This avoids RID/non-RID bootstrap filename
collisions without globally disabling duplicate-output checks.

Do **not** run an unscoped `azd deploy`, `azd up`, or original infrastructure
deployment to update One.

## Bot configuration and app package

After the targeted deployment:

```powershell
.\scripts\configure-one-channel.ps1 `
  -EnvironmentName zavafinance `
  -OriginalBotResourceId '/subscriptions/b651dacd-e6f5-465b-a17c-25f3a2cdd0c8/resourceGroups/rg-zavafinance-swc/providers/Microsoft.BotService/botServices/bot-zavafin-xjm5mipto7f22' `
  -SearchResourceId '/subscriptions/b651dacd-e6f5-465b-a17c-25f3a2cdd0c8/resourceGroups/rg-zavafinance-swc/providers/Microsoft.Search/searchServices/srch-zavafin-dev'
```

[The guarded configuration script](../../scripts/configure-one-channel.ps1):

- Refuses another branch or mismatched original/new resource identities.
- Adds only the new Bot's `mcs` OAuth connection, reusing the existing finance app.
  Secure Bicep parameters use temporary process environment variables, not files.
- Grants the new runtime **Search Index Data Reader** on the resolver Search
  service and **Cognitive Services OpenAI User** on the model account.
- Merges `purpose=demo` / `owner=gbelenky` tags onto the new Bot.
- Verifies persisted OAuth settings, required role grants, and original Bot
  endpoint/identity, then builds the three-file, branded package.

Upload [zavafinance-one.zip](../../appPackage/one/build/zavafinance-one.zip) using
the Teams custom-app upload flow. The ZIP contains only the manifest and two new
icons. It does not replace the original package or publish to the tenant catalog.
The package's Bot/custom-engine IDs are the One identity; `webApplicationInfo`
still identifies the authorized finance OAuth app. Do not replace it with a
generic service-generated package that loses this distinction.
In particular, `azd` can generate `appPackage.zip` beside One.Host. That generic
package is not the branded finance ZIP linked above. Rebuild through the
configuration script after deployment; package metadata alone does not prove SSO.

The endpoint exposes only `activity` with **BotServiceRbac**: the caller needs
the required Foundry access as well as their delegated finance permissions.
The current Foundry account already permits public network access, so the
documented `enable_m365_public_endpoint` private-network exception is not
required and no account network settings were changed.

## Deployed experiment and acceptance status

Verified on **17 September 2026**:

| Item | Value / evidence |
| --- | --- |
| Foundry agent | `zavafinance-one`, version `1`, active, in `prj-fdr-swc` |
| Azure Bot | `zavafinance-one-bot-b0c665c8`, resource group `rg-fdr` |
| Bot/runtime identity | `d0f5008b-84e1-414c-bd2d-f41e86612b47` |
| Delegated OAuth app | `2b775b24-03b6-4206-92b6-b21791666dea` |
| Runtime | Bundled `dotnet_10`, native `/activity/messages` |
| Live startup | Host and both native background services started; `/readiness` returned HTTP 200 |
| Configuration | OAuth, scoped Search/model grants and demo tags verified; repeat execution passed |
| Local regression | 73 native tests, including 24 hosted-adapter cases; 142 combined native/shared/original tests passed (overlapping counts) |
| Local host publish | Portable framework-dependent publish passed; no Functions/Durable dependencies or original Agent bootstrap files |
| Channel installation | One is installed and opens in M365 Copilot web |
| Interactive authentication | User completed sign-in with credentials or MFA; Entra recorded success |
| Cached-token check | A fresh One conversation answered `reset` without another prompt; this does not establish initial SSO |
| KPI tool evidence | Native logs show a KPIpedia answer and connector HTTP 201; answer content not independently browser-verified |
| Open acceptance | Initial silent SSO, live card continuations, Fabric statements/exploration and two-user isolation |

**Local source is ahead of deployed version 1.** The later rejected-invoke fix
and hosted-adapter tests have not been redeployed. Local test counts do not prove
that the deployed binary contains those fixes.

### Initial SSO remains unresolved

`AutoSignIn=true` starts authentication automatically; it does not guarantee
silent SSO. The observed credential/MFA challenge remains an acceptance issue,
even though the token obtained afterward works.

The pinned SDK enables SSO by default. A token-service HTTP 200 can contain a
sign-in resource rather than a user token. Inspect the emitted OAuth card's
token-exchange resource and subsequent `signin/tokenExchange` result to distinguish
client-side fallback from a failed exchange. Do not log tokens, magic codes, or
complete OAuth activities to diagnose it.

Microsoft documents separate Bot and OAuth app IDs as
[a valid supported configuration](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad).
Their difference alone is not a root cause. Adding `OBOConnectionName` does not
fix initial SSO: optional SDK OBO follows sign-in, and this application already
performs downstream OBO. The exact Foundry ServiceIdentity/M365 Copilot flow still
needs live validation; no consent, MFA, identity, or audience checks were weakened.

Fabric remains paused. Obtain approval before resuming it for finance tests, and
pause it afterward. Acceptance must exercise **One**, not the installed original
Zava Finance app: sign-in, reset, typed and clickable clarifications, a slow tool
reply, and two-user isolation. Startup/readiness and unit tests do not establish
that end-to-end contract.

## Targeted tests

From the repository root:

```powershell
dotnet test .\tests\ZavaFinance.Tests\ZavaFinance.Tests.csproj --filter FullyQualifiedName~ZavaFinance.Tests.One
```

These tests use the existing xUnit runner. Hosted regression cases start a real
M365 `HostedActivityService` with the typed application and `CloudAdapter`, not a
manual queue consumer. They verify prompt invoke acknowledgement while finance
is blocked, continued execution after request-scope disposal and cancellation,
fresh scoped authentication, and exactly one acknowledged connector reply.
Both supported invokes are exercised with normal, `expectReplies`, and streaming
delivery, including their actual JSON/SSE success and rejection responses.

The hosted cases use the real M365 OAuth and connector clients with an in-memory
fake HTTP transport, synthetic assertions, and synthetic finance/validation
collaborators. Other boundary tests exercise the existing orchestrator for reset,
typed/card selection, and caller ownership. No test calls live finance services or
exchanges real tokens; this is not live Foundry/Bot/`mcs` acceptance evidence.

Combined native/shared/original compatibility selection (**142 passed**):

```powershell
dotnet test .\tests\ZavaFinance.Tests\ZavaFinance.Tests.csproj --no-restore `
  --filter 'FullyQualifiedName~ZavaFinance.Tests.One|FullyQualifiedName~FinanceServiceCollectionTests|FullyQualifiedName~ChannelClarificationTests|FullyQualifiedName~ChannelInboundClarificationTests|FullyQualifiedName~NativeResponsesWireTests'
```

VS Code's current test discovery still retains two obsolete one-argument theory
cases; selecting those reports a parameter-count mismatch. The current source has
six two-argument cases and all pass through the rebuilt CLI runner above. Refresh
test discovery before relying on the editor's cached case list; do not remove
delivery-mode coverage to accommodate stale test IDs.
