# Operations and acceptance

This is the shared operational handover for the .NET and Python Activity
implementations. Local build/test success, deployment health, visible chat delivery
and independent financial verification are distinct forms of evidence.

## Operating contract

- Deploy only the selected service: `zavafinance-one` (.NET) or
  `zavafinance-one-python` (Python).
- Keep each implementation's Bot, installed package, runtime identity, state store
  and HMAC salt distinct. Reusing an authorized finance OAuth client does not
  merge Bots or user sessions.
- A readiness response proves startup, not live SSO, SQL authorization or chat delivery.
- Work queues and OAuth continuations are process-owned. A restart can lose
  accepted work; ask the user to retry. Conversation persistence is not task replay.
- `reset` clears finance memory. It does not clear Bot sign-in, create a new
  Foundry runtime session or select a new agent version.
- Disable sensitive GenAI content capture. Do not log tokens, OAuth state,
  complete Activities or permissioned finance-result bodies.
- Do not rotate the state salt as an ordinary redeployment operation.

## Verified development resource mapping

Live ARM readback on **18 September 2026** confirmed two distinct Bots in
`rg-fdr`. Both target the Foundry project `prj-fdr-swc` in account `rsc-fdr-swc`,
with separate implementation endpoints:

| Implementation | Bot resource name | Bot application/client ID | Activity route in the shared project |
| --- | --- | --- | --- |
| .NET | `zavafinance-one-bot-b0c665c8` | `d0f5008b-84e1-414c-bd2d-f41e86612b47` | `agents/zavafinance-one/endpoint/protocols/activityProtocol?api-version=2025-05-15-preview` |
| Python | `zavafinance-one-python-bot-b0c665c8` | `ed7dee33-74b7-44f9-82f5-01274a6b8a53` | `agents/zavafinance-one-python/endpoint/protocols/activityProtocol?api-version=2025-05-15-preview` |

Shared Foundry resources do not imply a shared Bot or endpoint switching.
A code-only deployment with unchanged identities, manifest and authentication
configuration does **not** require reinstalling the user's app package.
Read these mappings back again for the release under approval.

### Channel installation and SSO preflight

The native .NET package was personally installed on **18 September 2026 at
11:43 UTC**. Teams confirmed successful installation, and Microsoft 365 Copilot
lists **Zava Finance .NET** under installed agents. Its verified ZIP is at
`appPackage\dotnet\build\zavafinance-one.zip`: **Zava Finance .NET**, version **1.0.0**.
Its manifest and Bot IDs are `d0f5008b-84e1-414c-bd2d-f41e86612b47`, with finance
OAuth application `2b775b24-03b6-4206-92b6-b21791666dea` and the .NET token-exchange
resource below. The Python package was not rebuilt.

This was a personal installation, not tenant-wide publication. Package generation
alone does not establish installation; the confirmation above was observed in Teams
and the installed Copilot agent list.

Teams SSO permits `bots[].botId` and `webApplicationInfo.id` to identify different
applications. Both verified `mcs` connections use finance OAuth client
`2b775b24-03b6-4206-92b6-b21791666dea` and finance API scope
`api://botid-2b775b24-03b6-4206-92b6-b21791666dea/defaultScopes`.
Their token-exchange resources are separately configured:

| Implementation | Verified `mcs.tokenExchangeUrl` |
| --- | --- |
| .NET | `api://botid-2b775b24-03b6-4206-92b6-b21791666dea` |
| Python | `api://botid-ed7dee33-74b7-44f9-82f5-01274a6b8a53` |

The token-exchange resource is not necessarily the finance API scope's resource
or an identifier URI on the shared OAuth application. Validate it against the
selected Bot connection independently from delegated finance scopes. Preserve
both existing values; do not repoint a connection merely to make the IDs match.

Native .NET live checks are recorded below. They used available delegated user
authorization and do not establish a fresh silent SSO exchange. Initial installation
is required when an app is absent; the no-reinstall rule applies only to an already
installed app with unchanged identity/manifest/authentication.

## Recorded development evidence

Dates below identify executed checks. They do not certify an untested rebuild
or every later runtime version. Release/version readback belongs to the deployment
record, not the shared architecture diagram.

### Verification record — 18 September 2026

| Check | Verified result |
| --- | --- |
| Active development deployments | .NET `zavafinance-one` **v4**, created at **13:20:58 UTC**; Python `zavafinance-one-python` **v6**, unchanged |
| .NET channel session version | Real Copilot session `fceed42ff5e445b21b566855893527c9c273aa2867dac6a9084f582b519507c` reports agent version **4** |
| Python after relocation to `src\python` | Existing `unittest discover -s tests` with the relocated `.venv`: **222 passed in 11.403 seconds** |
| Python dependencies | `pip check` clean in that environment |
| .NET Debug regression | **567/567 passed**, excluding opt-in `RoutingEvalTests`; counters verified in `.artifacts\dotnet-validation\mcp-timeout-debug.trx` |
| .NET Release regression | **567/567 passed**, with the same exclusion; counters verified in `.artifacts\dotnet-validation\mcp-timeout-release.trx` |
| .NET clarification/native framework regression | **114/114 targeted tests passed**, including typed percentage choices, aliases, collision handling, authorization revalidation and SDK history serialization |
| .NET Fabric timeout regression | **77/77 targeted tests passed**; actual DI timeout configuration, early HTTP cancellation, caller cancellation and whole-tool deadlines through token acquisition, MCP stages and JSON/SSE body reads |
| .NET host dependency/build validation | Locked restore and `One.Host` build passed with **zero warnings/errors** |
| .NET Release publish | Succeeded at `.artifacts\dotnet-validation\publish`; `ZavaFinance.One.Host.runtimeconfig.json` is the only runtime configuration, with the host and required finance/helper libraries present |
| .NET Linux validation | `linux-x64` locked restore succeeded; framework-dependent Release output rebuilt with model-history schema version 2 at `.artifacts\dotnet-validation\publish-linux` |
| .NET deployment-wrapper local integration | `.\scripts\deploy-dotnet.ps1 -PublishOnly -NoRestore` succeeded: **567 tests passed**, local `linux-x64` validation bundle staged at `.artifacts\dotnet-agent`, one host runtime configuration and both ignore files verified; no cloud/OAuth calls |
| .NET local finance assembly | SHA-256 of `ZavaFinance.Agent.dll` in both Linux validation outputs matches the tested Release assembly |
| Actual azd hosted-code packaging | `azd package zavafinance-one --environment zavafinance --no-prompt --output-path .artifacts\azd-packages\zavafinance-one.zip` succeeded in **37 seconds**, using the installed `azure.ai.agents` beta.13 packager and the real host project |
| Hosted-code ZIP validation | **97/97 file SHA-256 hashes** match the final `linux-x64` schema-v2 validation bundle, including application code, pinned SDK and dependencies |
| Targeted deployment | `azd deploy zavafinance-one --environment zavafinance --no-prompt` succeeded in **55 seconds**; deployed content hash exactly matches the verified hosted-code ZIP |
| Deployment isolation | .NET runtime identity and canonical environment hash unchanged; Python v6 identity/environment unchanged; both Bot endpoints and OAuth mappings unchanged |
| Native personal installation | Teams confirmed **Zava Finance .NET** installed successfully at **11:43 UTC**; Copilot lists the native `d0f5008b-84e1-414c-bd2d-f41e86612b47` app |
| Packaged configuration check | Tooling's scan against configured sensitive environment values passed; those values were handled in memory and not printed |
| Deployment tooling | **29 offline checks passed**: 18 channel/app-package checks and 11 staging/environment checks; all scripts parse, seven solution paths exist and four retained Bicep templates compile without restore |
| OAuth separation regression | **Eight additional targeted offline cases passed**; no Azure calls were made by those tests |
| Existing .NET model permission | **Cognitive Services OpenAI User** on `rsc-fdr-swc` |
| Existing .NET Search permission | **Search Index Data Reader** on `srch-zavafin-dev` |

No additional .NET permission changes or infrastructure provisioning were needed
for the code-only deployment. Local checks establish regression and package
equivalence; real-channel evidence is recorded separately below.
The wrapper's `.artifacts\dotnet-agent` folder is a local validation/comparison
bundle, not azd's deployment source. The actual code ZIP was independently produced
from `src\dotnet\ZavaFinance.One.Host` and has SHA-256
`471FAC361A3831A411A896B64FEFB6DC5D89C78A468323C607CAA099D8322BBE`.
It is distinct from the Teams app ZIP at
`appPackage\dotnet\build\zavafinance-one.zip`.
The deployed v4 content hash matches that hosted-code ZIP. The Teams package was
personally installed; it was not published tenant-wide. No Python deployment or
package installation was changed.

Caller/conversation partitions are HMAC-derived. Retained state records expire
through their TTL; keep each implementation's existing salt unchanged.
The application uses model-history schema version **2**. Incompatible history is
discarded without replay or re-persistence. Pending clarification stores hashed
authorized catalogue terms, not raw labels or financial results; typed KPI choices
can omit a complete trailing percentage unit, but ambiguous matches stay unresolved.
This local state schema is not the deployed Foundry agent version.
Verify the new release in a fresh real-channel session after deployment.

### Executed live checks

| Implementation | Observed evidence | Still requires acceptance |
| --- | --- | --- |
| .NET | Personally installed native app; v4 session verified; all **11 functional acceptance cases passed**: explicit/contextual/relative statements, current-year default, reset, missing-scope continuation, actual card click, typed `Operating Margin` without `%`, KPIpedia definition/follow-up and sourced Fabric analysis delivery | Fabric narrative correctness remains a separate upstream gate; fresh silent SSO and live two-user isolation remain untested |
| Python | On 18 September 2026, 08:41–08:58 UTC: visible statements, reset, explicit/contextual/relative periods, clicked and typed clarification, KPIpedia follow-up and Fabric analysis delivery | Fresh SSO on the release under approval, live two-user isolation and independent verification of analytical/margin figures |

Python's finance checks used cached authentication. A fresh silent SSO exchange
was separately observed on **17 September 2026 at 21:17 UTC**: no token at start,
completed `signin/tokenExchange`, resumed question, delegated KPIpedia response
and visible delivery without clicking sign-in. It must not be relabelled as a
fresh exchange for the 18 September checks.

All .NET v4 acceptance replies rendered live without reopening the conversation.
The APAC Q2-to-Q3 2025 analysis was accepted at **13:26:35 UTC**. Its MCP request
completed with HTTP 200 after **51.181 seconds**, the tool returned at **13:27:30**,
and the connector acknowledged delivery at **13:27:31**. The actual sourced
answer was observed in Copilot. This request completed below 100 seconds; it is
not evidence of a live request running longer than that.

The Fabric named HTTP client now uses the configured analysis timeout rather
than the framework's 100-second default. A linked whole-tool deadline covers
delegated token acquisition, all MCP stages and response-body reading. Actual-DI
regressions verify configuration; cancellation tests verify enforcement and
distinguish downstream cancellation from full-budget expiry. Caller cancellation
propagates, cleanup remains bounded, and no retry was added.

**Analysis delivery is not financial-accuracy acceptance.** The upstream Fabric
answer used an undefined "total performance" decline of 6.55m USD and included
cost increases under positive-driver headings. That narrative was not reconciled
to an approved KPI and must not be treated as a verified profitability conclusion.
The host preserves the tool answer verbatim; statement and margin verification
below does not certify exploratory narratives.

The Python Fabric analysis completed in **57.876 seconds** inside the tool and
**61.646 seconds** for the Activity request. This is one observed request, not a
latency guarantee. Its sourced narrative was delivered; its figures were not
independently reconciled.

### Independently checked statement examples

The Python statement query/calculator and displayed .NET v4 statements were
compared with independently aggregated KPI-003 EMEA facts on 18 September 2026:

| Period | Actual USD | Prior USD |
| --- | ---: | ---: |
| November 2025 | 85,639,562.37 | 79,263,250.84 |
| Q4 2025 | 247,637,781.66 | 269,483,415.03 |
| Q3 2025 | 269,483,415.03 | 279,983,815.11 |
| Q3 2026 | 291,519,242.25 | 298,015,766.36 |

The .NET clarification answers also matched ratios calculated from independently
aggregated EMEA component amounts, not averages of monthly percentages:

| KPI | Q3 2025 | Prior quarter |
| --- | ---: | ---: |
| Gross Margin | 43.22% | 42.99% |
| Operating Margin | -26.65% | -24.77% |

The UI intentionally abbreviates currency to millions. These checks do not
establish cent-level UI display or validate every KPI/scope. Re-run against the
approved data release before treating them as release acceptance.

## Release checklist

1. Run the selected language's tests and dependency checks; retain artifact hash
   and resolved package versions.
2. Inspect the deployment package for source completeness and absence of secrets,
   local environments, caches and test data.
3. Read back the selected Foundry service/version/runtime and its native Activity
   endpoint, then the corresponding Bot/OAuth mapping and scoped role grants.
4. Confirm the installed branded package's identity and version. A code-only
   deployment with unchanged manifest/auth settings does not require reinstalling.
5. Use a fresh channel session and confirm the actual runtime version; existing
   sessions pinned to another version cannot validate a new deployment. Test definition,
   statement, ambiguity, typed/clicked choice, contextual period, reset and a
   slow exploration reply.
6. Test fresh SSO independently, then repeat with a second permitted/restricted
   user and attempted foreign-state/card access.
7. Reconcile financial values and review exploratory evidence separately from delivery.
8. Record untested gates explicitly; do not turn a log acknowledgement into proof
   of visible or accurate output.

Staging and production additionally require the unimplemented version-aware
Cosmos adapter, managed identity/data-plane RBAC, ETags, TTL, backup/restore tests,
Application Insights/Log Analytics, Key Vault and validated private connectivity.
See [IT requirements](it-admin-catalogue.md) and [support boundaries](availability-and-support.md).

## Troubleshooting

| Symptom | Safe first checks |
| --- | --- |
| App unavailable | Selected package/Bot identity, Bot Activity endpoint, host startup and readiness |
| Sign-in prompt or loop | `mcs` connection, token-exchange resource, audience/token version and sanitized exchange outcome; do not weaken MFA or audience validation |
| Search/model 403 | Actual runtime identity, scoped roles, propagation and host network path |
| Statement authorization failure | Delegated OBO outcome, SQL endpoint, metadata visibility and fact scope for that person |
| Ambiguous or unexpected term | Active release, authorized catalogue candidates and Search projection; do not force rank one |
| Long analysis timeout | Tool's total budget, named HTTP client timeout and response-body cancellation, capacity/service health, bounded cleanup |
| Acknowledged request without answer | Process interruption, queued-turn errors and connector response; do not assume work will recover |
| Context absent | Caller/conversation identity, state store/salt, expiry and reset; inspect only allowed state fields |
| Incorrect figure | Period, KPI components, distinct scope keys, fixed query results and decimal/rounding rules |

Inspect only the logs and data authorized for the incident. Shared chat transcripts
can contain financial information even when application state excludes result bodies.
Do not change resource scale, pause/resume a shared Fabric capacity, broaden access,
or delete resources as an incidental troubleshooting step.
