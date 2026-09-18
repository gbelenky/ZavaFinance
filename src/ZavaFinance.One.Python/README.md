# Zava Finance One Python

Standalone Python sibling of [the .NET Activity agent](../ZavaFinance.One/README.md),
on the experimental `activity-protocol` branch. It is not a proxy or a .NET sidecar.
The original deployment and the .NET One deployment remain separate.

## Boundaries

| Concern | Python implementation |
| --- | --- |
| Native channel | Published **beta** `azure-ai-agentserver-activity 1.0.0b3`; Activity 2.0.0 |
| Host | Stable `azure-ai-agentserver-core 2.1.0` |
| Finance-state API | **Experimental** API within the stable Core package |
| M365 application/authentication | Public `microsoft-agents-* 1.5.0` packages |
| Routing | **GA** Agent Framework core 1.18.0, OpenAI connector 1.14.3 and existing GPT-5.4-mini |
| Statement arithmetic | Application-owned Decimal calculations, never model arithmetic |
| Data access | Signed-in user's delegated OBO tokens, not the agent identity |
| Search and embeddings | Runtime managed identity; published metadata, not financial facts |
| Background execution | Process-owned work only; no durable in-flight recovery |
| Conversation state | Separate `zavafinance-one-python-sessions` store and `PYTHON_SESSION_KEY_SALT` |
| Installation | Separate Bot and **Zava Finance One Python** Teams/M365 package |

Unlike .NET One, this implementation does not need the unofficial source-built
.NET Activity package. Python still selects a beta Activity adapter; changing the
language does not establish GA support for that adapter.

The router exposes exactly three finance tools:

- `get_kpi_info`: KPIpedia in Copilot Studio.
- `get_statement`: parameterized Fabric SQL, validated catalogues and 18 fixed calculations.
- `explore_finance`: the published Fabric Data Agent over MCP.

The model selects at most one tool. Application code validates and executes it;
the answer is returned verbatim, with no second outer-model synthesis. Exact,
unambiguous authorized catalogue matches can execute; fuzzy/semantic matches
require a clickable clarification or a typed selection. Result text and delegated
assertions must not be placed in persisted router history.

The host explicitly disables sensitive GenAI telemetry content capture, including
when an inherited environment setting enables it. It also suppresses the pinned
token client's INFO logging of encoded OAuth state. Operational traces remain
available; prompts, tool arguments and financial answers are not intentionally recorded.

**Persisted conversation state is not durable execution.** A restart can interrupt
an accepted turn; retry the question. OAuth continuation state is process-local.
No new Functions app, App Service tier, Durable Task Scheduler, recovery opt-in,
or financial permissions are introduced by this Python version.

## Controller-oriented code map

| Module | Responsibility |
| --- | --- |
| `main.py` | Compose the real clients, host and resource lifecycle |
| `zavafinance/config.py`, `contracts.py` | Validated settings and small shared data contracts |
| `zavafinance/activity.py`, `identity.py` | Native messages/invokes, user authentication, OBO and caller isolation |
| `zavafinance/cards.py`, `wire.py` | Clickable attachment-only cards and strict submission limits |
| `zavafinance/orchestrator.py`, `model.py` | Single-tool routing, follow-ups and explicit execution |
| `zavafinance/state.py` | Whitelisted Foundry conversation state |
| `zavafinance/integrations.py` | Copilot Studio and Fabric Data Agent clients |
| `zavafinance/finance/` | Periods, KPI definitions, SQL, resolver and statement continuations |
| `tests/` | Offline unit and HTTP/SDK boundary tests |

## Local setup on Windows

Use Python **3.13**, PowerShell 7.2+, and an isolated environment. From this folder:

```powershell
py -3.13 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

Runtime dependencies are declared in [requirements.txt](requirements.txt);
[constraints.txt](constraints.txt) pins the resolved runtime versions and is
automatically applied by that requirements file. Development-only debugpy is excluded.
[pyproject.toml](pyproject.toml) configures type analysis only. There is deliberately
no second, empty package dependency declaration for the remote builder to prefer.

The VS Code workspace includes a Python-specific native-test debug launch and test
task. Both use this sibling's virtual environment, not the repository's older one.
Offline tests do not require a populated `.env`.

`main.py` is a **hosted-only** entry point: the Activity adapter relies on protected
Foundry ingress and the runtime-supplied agent instance identity. It is not an
anonymous local web server. Do not fabricate Foundry environment variables or expose
its `/activity/messages` route directly. The route is not a Responses chat API.
SDK/ASGI fixtures exercise the real host locally without bypassing production checks.

[.env.example](.env.example) documents configuration for authorized component
development. A completed `.env` must stay Git-ignored; never put an end-user assertion
in it. The optional `OBO_MANAGED_IDENTITY_CLIENT_ID` selects the federated OBO
alternative and requires corresponding federation on the finance OAuth application.
The supplied deployment reuses the existing secret-based OAuth configuration and
does not create that federation.

## Separate deployment

Reuse the existing Foundry project, model, Search and delegated finance configuration.
Install the local dependencies and run from the repository root:

```powershell
.\scripts\deploy-python-one.ps1 `
    -EnvironmentName zavafinance `
    -OriginalBotResourceId '/subscriptions/b651dacd-e6f5-465b-a17c-25f3a2cdd0c8/resourceGroups/rg-zavafinance-swc/providers/Microsoft.BotService/botServices/bot-zavafin-xjm5mipto7f22' `
    -SearchResourceId '/subscriptions/b651dacd-e6f5-465b-a17c-25f3a2cdd0c8/resourceGroups/rg-zavafinance-swc/providers/Microsoft.Search/searchServices/srch-zavafin-dev'
```

The script runs parity tests, creates the Python salt only if absent, deploys
**only `zavafinance-one-python`**, then configures its distinct Bot, `mcs` OAuth,
least-privilege runtime Search/model roles and demo tags.
It reuses the finance OAuth application's existing consent; it does not grant new
user permissions. Bot routing identity and `webApplicationInfo` OAuth identity
intentionally differ.

Channel configuration requires the existing finance OAuth registration to issue
v2 access tokens and expose the selected token-exchange resource URI. It validates
those prerequisites without modifying the shared Entra registration.
The current `zavafinance` azd environment persists these Python-only overrides:

```text
PYTHON_USER_AUTH_RESOURCE=api://botid-ed7dee33-74b7-44f9-82f5-01274a6b8a53
PYTHON_APP_PACKAGE_VERSION=1.0.1
```

These select the Bot connection/manifest resource and package version on subsequent
configuration runs. Without overrides, the script uses the original Bot's resource
and the manifest template's version. .NET One uses the separate `ONE_` prefix.
An override must name an identifier URI already exposed by the shared finance app.

The generated package is
[`appPackage/python/build/zavafinance-one-python.zip`](../../appPackage/python/build/zavafinance-one-python.zip).
It reuses the One icons but has a distinct name and application identity. Upload
that package, not the original or .NET One package.

Do not run an unscoped `azd deploy`, `azd up`, or Channel infrastructure deployment.
Do not rotate a deployed salt: that orphans its existing conversations.

The default is a direct-code `python_3_13` remote build. The SQL driver ships its
ODBC companion package, but Linux also needs the native libraries documented by
[mssql-python](https://github.com/microsoft/mssql-python#installation).
Startup explicitly loads both the native extension and the resolved bundled
provider before readiness. This still does not test SQL connectivity, TLS or user
authorization. If the managed base runtime
lacks those libraries, use a deliberately configured container rather than an
unauthenticated fallback or silently disabling statements.

## Acceptance and operational limits

### Fabric timeout correction on 18 September 2026 - deployed v4

The APAC Q2-to-Q3 analysis in deployed **v3** reached Fabric successfully:
cached-token authentication completed at **06:24:06 UTC**, model routing returned
HTTP 200, delegated OBO succeeded, and MCP initialization/discovery completed by
**06:24:13 UTC**. The explicit timeout reply was delivered at **06:25:15 UTC**.
This was not an SSO failure or the earlier model HTTP 400. Copilot's generic
"may still be processing" notice does not establish that analysis continues.

The shared HTTP client had a **60-second read timeout** inside the Fabric query's
**300-second overall deadline**. The [Fabric adapter](zavafinance/integrations.py)
now explicitly applies the configured analysis budget to MCP request reads,
without changing the shared client or Copilot Studio behavior. Connection timeout
remains 10 seconds, write/pool timeouts remain 60 seconds, and session cleanup
remains bounded to 5 seconds. The overall query deadline still bounds setup,
retries and streaming; progress messages do not extend it.

Validation: **42 integration/orchestration tests passed**, including four new
regressions. Before the fix, timeout-policy assertions and a delayed real HTTP
response failed. Afterward, a separate loopback HTTP test waited **65 seconds**
for the tool response, completed in **65.45 seconds**, and verified MCP session
cleanup. It used synthetic data, not Fabric or a signed-in channel.

**Deployed:** Python **v4** became active at approximately **06:42 UTC** after the
full **203-test** suite and dependency check passed. A fresh hosted v4 session
returned `/readiness` **HTTP 200 at 06:43:09 UTC**, then was deleted. Runtime
environment values and identity were compared before/after and remained identical.
Bot endpoint, OAuth client/scope/resource, runtime roles and package **1.0.1**
were reverified unchanged; original .NET v13 and .NET One v1 remain active.
No app reinstall is needed. Use a fresh Copilot chat for v4 channel verification.

No real Fabric analysis or channel message was submitted during this rollout.
At approximately **06:51 UTC on 18 September**, the user confirmed that the updated
agent was working. This is user-reported acceptance; no additional query timings,
answer-accuracy checks or per-tool/two-user evidence were independently collected.
The 65-second delay is a test case, not the new limit or proof that real analysis
will finish within 300 seconds. Longer work requires a separately verified
asynchronous task path and, for restart recovery, durable execution; neither is
implemented by this timeout correction.

### Verification on 17 September 2026

| Check | Result |
| --- | --- |
| Local regression suite | **199 passed, 0 failed, 0 skipped**, including OAuth/SSO/privacy and replayed function-call JSON regressions; dependency check passed before deployment |
| Separate Foundry deployment | `zavafinance-one-python`, version **3**, in `prj-fdr-swc`; code-only serialization fix deployed at approximately **23:42 UTC** |
| Cloud startup | Linux Python 3.13, Core 2.1.0 and Activity 1.0.0b3; `/readiness` returned **HTTP 200** |
| Native SQL prerequisites | SQL configuration was present; extension and bundled provider preflight completed before readiness. No live SQL query was run |
| Model and telemetry | Existing **GPT-5.4-mini**; sensitive GenAI content capture explicitly `false`; resilient tasks disabled |
| Bot | `zavafinance-one-python-bot-b0c665c8`, routing application `ed7dee33-74b7-44f9-82f5-01274a6b8a53`, targeting the Python Activity endpoint |
| OAuth and runtime access | Shared finance app now issues v2 tokens and exposes an additional Python routing URI. Python `mcs` and the new manifest use that URI; OAuth client, delegated scope, permissions, credentials and scoped runtime roles preserved |
| Install package | Python **1.0.1** built and checked: exactly `manifest.json`, `color.png`, `outline.png`; Developer Portal import passed. Installed **1.0.1** confirmed in M365 About at 21:14 UTC |
| Existing deployments | Original `zavafinance` version **13** and .NET `zavafinance-one` version **1** unchanged |
| Initial silent SSO | **Passed at 21:17 UTC** in a fresh M365 **v2** conversation: BEGIN with no token, `signin/tokenExchange` COMPLETE with a token and HTTP 200, original question resumed, delegated KPIpedia returned HTTP 200, and the answer was visible. No Sign In, credentials or consent clicked |
| External Edge / Azure profile | `What is net revenue?` submitted at **21:23 UTC**; authenticated KPIpedia answer visibly delivered. This turn reused the cached delegated token, not a second initial-SSO proof |
| Follow-up routing | The **v2** EMEA margin follow-up at **21:25 UTC** failed with model HTTP 400 because replayed function-call arguments were dictionaries, not JSON strings. **v3 fixes that boundary**. Two live model turns using the corrected source passed at **23:45 UTC**; no finance tools were executed by that diagnostic |
| Post-v3 channel verification | Hosted **v3** `/readiness` returned HTTP 200. External Edge's Azure profile was opened, but Windows was locked and composer focus could not be acquired. No new channel test message was submitted; visible first/follow-up answers on v3 remain unverified |
| Fabric Data Agent | Same analysis question retried in a fresh external Edge / Azure conversation at **21:28 UTC**. Model routing and delegated OBO succeeded; Fabric MCP initialization/discovery returned HTTP 200/202. Analysis timed out after roughly 60 seconds; the explicit timeout reply was persisted and visibly delivered. No financial analysis answer or accuracy acceptance |
| Fabric capacity | Read-only check found `capfabricdaweus3` **Active**. This work did not resume or otherwise change it |

The temporary startup-validation session was deleted after the readiness check.
It proved cloud startup, not delivery through Teams/M365. The deployment is
**not yet end-to-end accepted**.

Version 3 changes only [model history serialization](zavafinance/model.py):
argument dictionaries remain typed dictionaries in persisted state, then become
JSON strings when constructing the SDK function-call message. The regression suite
checks all three tools, empty/nullable/escaped arguments, unchanged state, paired
content-free results, and the real Responses HTTP payload. The new assertions
failed before the fix and passed afterward. Live first/follow-up requests against
the existing model both selected `get_kpi_info`; this is model-wire verification,
not delegated tool or M365 delivery acceptance. SSO, roles, salt, dependencies and
package **1.0.1** are unchanged; no package reinstall is needed for this update.

The version 2 server fix alone did not require reinstalling the app. The subsequent
SSO resource change required updating the installed Python package to **1.0.1**;
that update is now confirmed. For another installation, use
the [Python package](../../appPackage/python/build/zavafinance-one-python.zip)
in Teams desktop: **Apps > Manage your apps > Upload an app**, then confirm the
Python app's displayed version is **1.0.1**. Do not upload the original/.NET package
or publish the app tenant-wide. Importing into Developer Portal alone does not
update the personal installation.

Version 1's live OAuth card send returned a successful connector response with
no activity ID. The application treated that as failed delivery and returned
HTTP 500 before saving the original question's sign-in continuation. Version 2
allows that response only for the SDK OAuth control send, preserves the token
exchange resource and continuation, and still rejects failed HTTP operations
and missing delivery IDs for ordinary finance answers. Regression tests reproduce
the original 500 and verify the corrected exchange/replay path with synthetic
OAuth services; they are **not proof of live silent SSO**.

At **21:17 UTC**, a fresh conversation with installed **1.0.1** met those live
criteria: no token initially, a completed `signin/tokenExchange`, successful OBO
and KPIpedia calls, persisted state, a real connector reply ID and a visible
Net Revenue answer. Session
`98a537b8d7ccbc0f2638306d38aa2e6c06c32b296573e1f42726e41cf68cc12`
was explicitly version 2. The earlier `resourcematchfailed` callback at 21:08
preceded `installationUpdate` at 21:09; the 21:10 attempt had no observed callback.
Inspect the actual session version rather than assuming an older session ID is
permanently pinned. `reset` clears finance state, not the Bot's cached OAuth token;
it is not a fresh-auth test.
The host logs flow stages, exchange-resource presence and allowlisted
`signin/failure` codes, never the supplied bearer or arbitrary failure message.
The external Edge / Azure KPIpedia success at 21:24 separately verifies that
client path, but used a cached token.

An external contract check confirmed that [separate routing and OAuth app IDs
are supported](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad)
and the [Agents SDK documents `defaultScopes`](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/azure-bot-user-authorization-federated-credentials#expose-an-api-endpoint).
Neither fact proves this installation's silent SSO. The Teams registration guide
also requires **`api.requestedAccessTokenVersion=2`**. The shared finance app was
corrected from `null` to `2`; both Python and .NET validators already accept its
GUID audience and v2 issuer. The 20:12 retest still failed, so this correction alone
did not resolve the problem.

The later resource change follows the [Teams SDK's managed-identity SSO guidance](https://github.com/microsoft/teams-sdk/blob/main/teams.md/docs/main/teams/user-authentication/sso-setup.mdx#sso-with-user-assigned-managed-identity):
`api://botid-{routingBotId}` is exposed on the separate OAuth app. The shared app
now retains `api://botid-2b775b24-03b6-4206-92b6-b21791666dea` and additionally exposes
`api://botid-ed7dee33-74b7-44f9-82f5-01274a6b8a53`. Only the Python Bot connection
and Python package were switched to the new URI. The OAuth client remains
`2b775b24-03b6-4206-92b6-b21791666dea`, and its requested delegated scope remains
`api://botid-2b775b24-03b6-4206-92b6-b21791666dea/defaultScopes`.

This configuration now has **live initial-SSO evidence for this Python deployment**,
not a general Foundry compatibility guarantee or an isolated root-cause experiment.
Foundry publishes this Bot as `SingleTenant`, not `UserAssignedMSI`. The independent
contributions of token version, resource alignment and client/package refresh were
not isolated. Entra rejected
the additive URI while the app used v1 tokens; after the documented v2 correction
it accepted the URI without any [tenant policy exemption](https://learn.microsoft.com/en-us/entra/identity-platform/identifier-uri-restrictions).
Scopes, preauthorized clients, credentials and downstream permission metadata were
preserved. The shared application's new token format affects freshly minted tokens
for all three agents; original/.NET live compatibility still needs verification.
Agent versions and original/.NET Bot endpoints/connections were not changed.

Keep local tests, cloud startup, and real channel acceptance distinct. Required
interactive checks include sign-in/reset, each real finance tool, typed and clicked
clarification, successful delayed finance delivery and two-user isolation. The
21:28 analysis test verified delegated MCP access and delivery of an explicit
timeout, not a completed analysis. The 21:25 follow-up's model HTTP 400 was fixed
in version 3 and verified against the live model; a real channel follow-up still
needs verification after the desktop is unlocked. Opening a fresh conversation
alone was not the fix.

- Initial silent SSO remains unresolved for .NET One. Python's 21:17 initial exchange
  is proven; cached-token reuse alone must not be counted as an initial exchange.
- Check Fabric's current state before live testing. Do not resume a paused capacity
  without approval; paused capacity blocks Fabric-dependent live acceptance, not
  offline parity testing.
- Evaluations remain deferred; no paid evaluation suite is provisioned automatically.
- Stop or remove only the **Python** agent/Bot/package when retiring this experiment.
  Preserve the shared project, OAuth application, model, Search and Fabric resources.

Deployment and interactive results must be recorded from executed checks, not inferred
from the implementation or from .NET One's earlier results.
