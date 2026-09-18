# ZavaFinance .NET

The .NET 10 implementation runs a native Activity application in Microsoft Foundry.
Its deployed service name is **`zavafinance-one`**. It owns a distinct Bot, app package,
runtime identity and finance state.

Shared [architecture](../../docs/architecture-diagram.md),
[message flows](../../docs/swimlane-diagram.md),
[financial controls](../../docs/finance-controller-architecture.md),
[deployment](../../docs/deployment.md) and
[acceptance evidence](../../docs/operations.md) live in `docs`.
The [Python implementation](../python/README.md) is independently deployable.

## Dependencies and support

| Component | Selected dependency |
| --- | --- |
| Runtime | .NET 10; Foundry `dotnet_10`, Activity `2.0.0`, BotServiceRbac |
| Agent execution | `Microsoft.Agents.AI 1.21.0` |
| OpenAI chat-client adapter | `Microsoft.Extensions.AI.OpenAI 10.5.1` |
| Microsoft 365 Agents | `Microsoft.Agents.* 1.8.77` for Activity/Copilot Studio integration |
| Foundry state integration | `Azure.AI.AgentServer.Core 1.0.0-beta.28` — **prerelease** |
| Native Activity host | `Azure.AI.AgentServer.Activity 1.0.0-beta.1.source.dc9cca2d1f1c.core28.m3651877` — **unofficial, unsigned public-source compatibility build** |

The Activity package is pinned to Azure SDK source commit
`dc9cca2d1f1c9f42182a0f1d6cc2540acf5dc956`, with Core beta.28 and M365 1.8.77
dependencies. It is not a supported GA transport or a public NuGet release.
The [SDK build script](../../scripts/build-experimental-activity-sdk.ps1) verifies
source/provenance and retains license/checksum information.

Foundry Hosted Agents is a **GA service**; the selected Activity API remains
**Preview**. Development finance memory uses Foundry-managed durable state,
**Public Preview with no SLA**. Consult the
[support register](../../docs/availability-and-support.md) before promotion.

## Code map

| Path | Responsibility |
| --- | --- |
| [ZavaFinance.One.Host](ZavaFinance.One.Host) | Executable composition, native `ActivityServer` and runtime configuration |
| [ZavaFinance.One](ZavaFinance.One) | Finance Activity handlers, user authentication, queued continuations and reply delivery |
| [ZavaFinance.Activity](ZavaFinance.Activity) | `ZavaFinance.ActivitySupport` namespace: `ActivityOptions`, `CallerIdentityResolver` and `ClarificationCard` |
| [ZavaFinance.Agent](ZavaFinance.Agent) | Agent Framework execution, finance tools, filtered history, resolver and state |
| [ZavaFinance.Agent/Identity](ZavaFinance.Agent/Identity) | Human assertion validation, downstream OBO and caller-bound keys |
| [ZavaFinance.Agent/Contracts](ZavaFinance.Agent/Contracts) | Typed finance reply and clarification contracts |
| [tests/ZavaFinance.Tests](tests/ZavaFinance.Tests) | Finance, identity, framework and native Activity boundary tests |

[FinanceAgentFactory](ZavaFinance.Agent/Agent/FinanceAgentFactory.cs), injected into
`OrchestratorAgent`, constructs a fresh MAF `ChatClientAgent` per turn with executable
`AIFunction`s. The framework owns invocation and `AgentSession`.
[FinanceHistory](ZavaFinance.Agent/Agent/FinanceHistory.cs) composes
`InMemoryChatHistoryProvider`, retaining user text, validated tool intent, paired
content-free markers and no-tool replies—not permissioned tool-result bodies.
[FinanceTools](ZavaFinance.Agent/Agent/FinanceTools.cs) supplies caller-bound executable
wrappers, while [FinanceToolCatalog](ZavaFinance.Agent/Agent/FinanceToolCatalog.cs)
binds functions and validates the complete model selection before execution.

`SingleModelTurn` permits one outer non-streaming model request with a 60-second
model-only timeout and validates the complete selection before execution.
`StopAfterToolAsync` function middleware permits one invocation and sets the
framework termination flag. The selected tool's typed finance reply is returned
verbatim outside model history, with no final synthesis. Definition and analysis
subagents can make their own model calls inside the selected tool; the one-request
limit applies to outer orchestration.

Reset, authorized clarification and per-caller turn serialization remain explicit
application operations. Tokens and permissioned tool-result bodies are not persisted.
The application entrypoints are `OrchestratorAgent.RunAsync(tokens, key, question, ct)`
and `RunReplyAsync(tokens, key, question, submission, ct)`; the native Activity adapter
uses `IFinanceActivityRunner`.

The three tools are `get_kpi_info`, `get_statement` and `explore_finance`.
Statements use authorized metadata, fixed parameterized Fabric SQL and .NET
`decimal` arithmetic. The runtime managed identity accesses models/embeddings
and Search; finance services use the signed-in user's delegated OBO permissions.

## Local setup and validation

Use Windows, PowerShell 7.2+ and the .NET 10 SDK. Run commands from the repository
root. Restore the approved pinned SDK feed to `.artifacts\one-sdk\feed` before
building. To reproduce that feed, supply an administrator-selected dedicated
working directory outside the repository. The builder defaults to installed SDK
**10.0.303**; use its `-SdkVersion` parameter for an explicitly selected compatible
installed .NET 10 SDK:

```powershell
.\scripts\build-experimental-activity-sdk.ps1 `
    -WorkDirectory $sdkWorkDirectory `
    -OutputDirectory .\.artifacts\one-sdk

dotnet test .\src\dotnet\tests\ZavaFinance.Tests\ZavaFinance.Tests.csproj `
    --configuration Release --filter "FullyQualifiedName!~RoutingEvalTests"

.\scripts\deploy-dotnet.ps1 -PublishOnly
```

For a fresh SDK source build, retain its dependency lock and use the script's
`-DependencyLockFile` parameter to reproduce that graph. Do not upgrade or rebuild
the Activity package as a routine deployment step.

The direct test command, deployment wrapper and editor test task explicitly exclude
opt-in `RoutingEvalTests`, which exercise a deployed model when their Foundry test
environment is configured. Local validation must not trigger these live evaluations
through ambient configuration; excluding them is not model acceptance.

`-PublishOnly` runs tests and stages a **framework-dependent `linux-x64` local
validation/comparison bundle** in `.artifacts\dotnet-agent`, without accessing Azure
deployment state.
The bundle contains `ZavaFinance.One.Host.dll`, its runtime configuration and
project/native dependencies, with generated `.agentignore` and `.azdignore`
files for manual handling of that bundle. Foundry supplies the .NET runtime.

`azure.yaml` points to the real `src\dotnet\ZavaFinance.One.Host` project, not the
validation folder. The validated `azure.ai.agents` **beta.13** packager independently
publishes that project for `linux-x64` and packages its output. There is no `dist`
setting. See [hosted-code packaging](../../docs/deployment.md#net-hosted-code-packaging)
for the package-only command and the distinction from a Teams installation ZIP.

The deployment script uses the existing pinned feed, not a package from another
SDK build. `-NoRestore` requires compatible restored assets for both tests and
the wrapper's `linux-x64` publish; it does not make azd reuse that output.
Keep the host lock and runtime-specific graph consistent;
do not disable lock checks to hide a dependency mismatch.

The Activity entrypoint expects the protected Foundry ingress/runtime context.
Do not expose it as an anonymous local finance endpoint or fabricate runtime
identity variables. Native SDK boundary tests provide local host validation.

## Configuration

- Set `ONE_SESSION_KEY_SALT` once for a new environment and preserve it across
  releases. The finance store is `zavafinance-one-sessions`.
- Caller partitions use HMAC-SHA256 over length-prefixed identity fields.
  Deploying this release establishes new .NET state partitions: users start fresh
  finance context, while retained records are not deleted and expire by TTL.
  Preserve the salt; this partition transition is not a salt rotation.
- Framework model-history schema version **2** discards version-1 history without
  replaying or re-persisting it; normal model turns persist version-2 history.
  This schema version is separate from the deployed Foundry agent version.
- The deployed host receives .NET configuration keys such as `Obo__TenantId`,
  `Obo__ClientId`, `Obo__Audience`, `Fabric__SqlEndpoint` and `Resolver__SearchEndpoint`.
- `ActivityOptions` still binds the `Orchestrator` configuration section; its
  namespace does not change environment keys such as `Orchestrator__SessionKeySalt`.
- The repository [deployment manifest](../../azure.yaml) maps the approved azd
  environment settings into that contract. Keep secret values out of tracked files.
- The named `mcs` OAuth handler obtains a human assertion for the finance OAuth
  audience. Runtime/Bot credentials are not finance-user credentials.
- `ModelDeployment=gpt-5.4-mini` and `ModelReasoningEnabled=true` select low
  reasoning effort without temperature.

See the [administration checklist](../../docs/it-admin-catalogue.md#configuration-checklist)
for the complete cross-service configuration review.

## Deployment and app installation

From the repository root, with `$searchResourceId` set to the approved Search ARM ID:

```powershell
.\scripts\deploy-dotnet.ps1 `
    -EnvironmentName zavafinance `
    -SearchResourceId $searchResourceId
```

This tests, creates the local validation bundle, lets azd publish/deploy the host
project for `zavafinance-one` only, and runs the guarded configuration/app-package
step. Keep the tested source unchanged through packaging and deployment.
For configuration-only reconciliation after a verified deployment:

```powershell
.\scripts\configure-channel.ps1 `
    -Variant DotNet `
    -EnvironmentName zavafinance `
    -SearchResourceId $searchResourceId
```

A missing `mcs` connection requires explicitly approved `-UserAuthResource` and
`-DelegatedScopes` (or `ONE_USER_AUTH_RESOURCE` / `ONE_DELEGATED_SCOPES`).
`-TokenExchangeResource` is an alias for `-UserAuthResource`. The script validates
the selected Bot's token-exchange URI separately from scopes exposed by the shared
finance OAuth application; those resource URIs need not match. It does not invent
scopes, replace an existing connection or modify shared Entra consent.
`ONE_APP_PACKAGE_VERSION` can select an intentional package update.

Install the generated `appPackage\dotnet\build\zavafinance-one.zip`, not the hosted-code
ZIP under `.artifacts\azd-packages` or a generic SDK-generated app ZIP.
In Teams: **Apps → Manage your apps → Upload an app → Upload a
custom app**, then **Add**, subject to tenant policy. The branded package keeps
Bot routing and delegated OAuth identity settings intact.

Code-only deployment with unchanged package/auth settings does not require an
app reinstall. Test in a fresh conversation and verify the actual runtime version;
`reset` does not switch versions.

## Operating limits

Native queues and OAuth continuation are process-owned. There is **no durable
in-flight recovery**: a restart can lose an accepted turn. A successful connector
acknowledgement is required for ordinary answers, but is not an exactly-once guarantee.
Do not log user assertions or finance-result bodies to diagnose failures.

Fresh silent SSO and current-build live finance/card/two-user checks are release
gates; see [operations](../../docs/operations.md). Staging/production Cosmos state,
monitoring, Key Vault and private connectivity are planned, not deployed.
