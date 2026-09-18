# ZavaFinance Python

The Python 3.13 implementation runs a native Activity application in Microsoft
Foundry. Its deployed service name is **`zavafinance-one-python`**. It owns a
distinct Bot, app package, runtime identity and finance state.

Shared [architecture](../../docs/architecture-diagram.md),
[message flows](../../docs/swimlane-diagram.md),
[financial controls](../../docs/finance-controller-architecture.md),
[deployment](../../docs/deployment.md) and
[acceptance evidence](../../docs/operations.md) live in `docs`.
The [.NET implementation](../dotnet/README.md) is independently deployable.

## Dependencies and support

| Component | Selected dependency |
| --- | --- |
| Runtime | Python 3.13; Foundry `python_3_13`, Activity `2.0.0`, BotServiceRbac |
| Native Activity host | `azure-ai-agentserver-activity 1.0.0b3` — **published beta** |
| Foundry core | `azure-ai-agentserver-core 2.1.0` — stable package; state API **experimental** |
| Agent execution | `agent-framework-core 1.18.0`, `agent-framework-openai 1.14.3` |
| Microsoft 365 Agents | `microsoft-agents-* 1.5.0` |
| SQL | `mssql-python 1.15.0` and bundled ODBC companion |

[requirements.txt](requirements.txt) applies the resolved versions in
[constraints.txt](constraints.txt). [requirements-dev.txt](requirements-dev.txt)
adds development dependencies; [pyproject.toml](pyproject.toml) configures analysis.
Review exact direct/transitive packages when changing dependencies.

Foundry Hosted Agents is a **GA service**; the Activity API remains **Preview**.
Development uses Foundry-managed durable state, **Public Preview with no SLA**.
Stable Core package numbering does not make its experimental state API GA.
See [availability and support](../../docs/availability-and-support.md).

## Framework execution and code map

[FinanceAgent](zavafinance/agent.py) composes a real MAF `Agent`, `OpenAIChatClient`,
executable `FunctionTool`s and a per-turn `AgentSession`.

- `FinanceHistory` in [agent_policy.py](zavafinance/agent_policy.py) implements
  `HistoryProvider`, persists validated intent and excludes private results.
- `SingleModelTurn` middleware allows at most one non-streaming outer model request,
  with a model-only timeout; it does not shorten the Fabric analysis budget.
- `StopAfterTool` uses the framework's `MiddlewareTermination` signal to stop after
  the selected tool, without final synthesis.
- [FinanceTools](zavafinance/tools.py) keeps the permissioned `FinanceReply` outside
  model messages; strict typed inputs also validate persisted intent.

Reset, clarification selection and per-caller serialization are explicit application
operations. The tools are `get_kpi_info`, `get_statement` and `explore_finance`.
Statements use fixed parameterized SQL and application `Decimal` math; finite
SQL FLOAT conversion uses `Decimal(str(value))`, which cannot repair source precision.

| Module | Responsibility |
| --- | --- |
| [main.py](main.py) | Client composition and hosted resource lifecycle |
| [config.py](zavafinance/config.py), [contracts.py](zavafinance/contracts.py) | Validated configuration and typed contracts |
| [activity.py](zavafinance/activity.py) | Native Activity application and finance handlers |
| [channel/continuations.py](zavafinance/channel/continuations.py) | HTTP acknowledgement gate and process-owned continuation |
| [channel/sdk_compat.py](zavafinance/channel/sdk_compat.py) | Pinned SDK/OAuth adaptations, delivery checks and client cleanup |
| [identity.py](zavafinance/identity.py) | Human assertion validation, OBO and caller keys |
| [cards.py](zavafinance/cards.py), [wire.py](zavafinance/wire.py) | Clickable cards and strict submission boundaries |
| [agent.py](zavafinance/agent.py), [agent_policy.py](zavafinance/agent_policy.py) | Agent execution, session/history and middleware |
| [state.py](zavafinance/state.py) | Whitelisted caller-bound Foundry state |
| [model.py](zavafinance/model.py) | Model connection, safe message serialization and constrained reranking |
| [integrations.py](zavafinance/integrations.py) | Delegated KPIpedia and Fabric MCP clients |
| [finance](zavafinance/finance) | Periods, catalogue resolution, fixed SQL and calculations |
| [tests](tests) | Offline unit and real SDK/HTTP boundary fixtures |

The compatibility module uses private hooks of pinned Microsoft 365 SDK packages
for OAuth and cleanup. Revalidate those adaptations when changing dependencies.
The host disables sensitive GenAI content capture and suppresses token-client
logging that could expose encoded OAuth state.

## Local setup on Windows

Use Python **3.13** and PowerShell 7.2+. From `src\python`:

```powershell
py -3.13 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
.\.venv\Scripts\python.exe -m pip check
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

Tests use synthetic/mocked transport fixtures and do not require a populated `.env`.
The workspace's Python test/debug configuration uses this folder's virtual environment.

`main.py` is **hosted-only**: the Activity adapter requires protected Foundry ingress
and the runtime-provided agent identity. Do not fabricate runtime variables or
expose `/activity/messages` anonymously. SDK/ASGI fixtures validate the host locally
without bypassing those security requirements.

[.env.example](.env.example) describes authorized component-development settings.
Completed `.env` files stay Git-ignored. Do not put end-user assertions in them.

## Configuration

- The state store is `zavafinance-one-python-sessions`; `PYTHON_SESSION_KEY_SALT`
  must be distinct and stable across releases.
- Configure Foundry/model, OBO tenant/client/audience, KPIpedia, Fabric SQL/MCP and
  resolver Search/embedding settings through [Settings](zavafinance/config.py).
- The deployed [azure.yaml](../../azure.yaml) maps approved azd environment values.
  Runtime model/embedding/Search access uses managed identity; finance calls use
  delegated OBO.
- `OBO_MANAGED_IDENTITY_CLIENT_ID` selects the optional federated OBO path only
  after the corresponding Entra federation is configured. The supplied deployment
  uses the approved secret-based finance OAuth configuration.
- The default Fabric analysis budget is **300 seconds**; MCP reads use that budget.
  Connection, write/pool and cleanup timeouts remain bounded independently.
- Model-only timeout is **60 seconds**. Statement date guidance and parsing use the
  same UTC reference date per turn.

## Deployment and app installation

From the repository root, with `$searchResourceId` set to the approved Search ARM ID:

```powershell
.\scripts\deploy-python.ps1 `
    -EnvironmentName zavafinance `
    -SearchResourceId $searchResourceId
```

The wrapper checks dependencies/tests, preserves or initially creates the
Python-specific salt, deploys **only `zavafinance-one-python`**, and configures its
Bot, scoped model/Search roles and branded package.

For configuration-only reconciliation after a verified deployment:

```powershell
.\scripts\configure-channel.ps1 `
    -Variant Python `
    -EnvironmentName zavafinance `
    -SearchResourceId $searchResourceId
```

A missing `mcs` connection requires explicitly approved `-UserAuthResource` and
`-DelegatedScopes` (or `PYTHON_USER_AUTH_RESOURCE` / `PYTHON_DELEGATED_SCOPES`).
`-TokenExchangeResource` is an alias for `-UserAuthResource`. The selected Bot's
approved token-exchange URI is validated independently from the finance API scopes
and need not be an identifier URI on the shared OAuth application. Existing
connection values are preserved, including Python's distinct token-exchange URI.
`PYTHON_APP_PACKAGE_VERSION` can select an intentional installation update.
No new finance permissions or shared-app consent are implied.

Install `appPackage\python\build\zavafinance-one-python.zip`, not a generic
SDK-generated ZIP. In Teams: **Apps → Manage your apps → Upload an app → Upload a
custom app**, then **Add**, subject to tenant policy. The package preserves the
distinct routing Bot and finance OAuth identities.

A code-only release with unchanged manifest/auth settings does not require a package
reinstall. Use a fresh conversation and verify the runtime version; `reset` is not
a deployment switch.

Foundry uses direct-code remote build. Startup preflights the native SQL extension
and bundled provider before readiness. Native loading does not prove SQL network,
TLS or delegated authorization. See
[mssql-python prerequisites](https://github.com/microsoft/mssql-python#installation);
do not silently disable statements or weaken authentication if a prerequisite fails.

## Operating limits

No tokens or permissioned tool-result bodies are persisted in finance history.
OAuth continuation and accepted work are process-owned: there is **no durable
in-flight recovery**, and interrupted questions may need retrying.

Live delivery, financial reconciliation, fresh SSO and two-user isolation are
tracked separately in [operations](../../docs/operations.md). Staging and production
use planned independent Cosmos state, monitoring, Key Vault and private networking;
their adapter and deployments are not implemented.
