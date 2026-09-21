# Customer onboarding and local development

Review date: **21 September 2026**. This runbook applies to a customer development
environment, not staging/production. Start with the
[IT catalogue](it-admin-catalogue.md) for the service bill of materials, customer
input record, owners and networking/identity approvals.

**Choose one implementation first.** .NET and Python are independent applications,
not two components that must both run. They can share approved dependencies, but
must not share a Bot registration, state-store name or session-key salt.

## Supported developer workflow

| Loop | Where it runs | Credentials / prerequisites | What success establishes |
| --- | --- | --- | --- |
| Edit, debugger and deterministic unit tests | Developer workstation or approved build runner | Source and restored packages; tests use controlled fakes/mocks | Business logic, validation, framework integration and simulated Activity/auth behavior |
| Optional .NET routing-model evaluation | Local test runner calling the customer Foundry model | Approved developer Azure credential, endpoint, deployment and model access | Live model tool-selection behavior, not real finance tools, SSO or channel hosting |
| Package/startup checks | Local build output; hosted runtime for final native-dependency proof | Approved toolchain and the selected host's required configuration | Build/package integrity and startup dependencies, not full financial access |
| Authenticated integration checks | Customer's isolated hosted development agent | Approved Azure identities, Bot OAuth, user permissions and real network paths | Selected model/Search/Fabric/KPIpedia integrations |
| Teams/M365 acceptance | Installed pilot app targeting that agent | Correct app assignment, fresh delegated sign-in and customer policies | Actual channel UX, SSO, cards, delayed replies and caller isolation |

The normal inner loop is **local code/tests; deploy to Azure development for the real
identity/channel loop**. This repository is not a fully emulated local Foundry service.

### Important localhost limitations

- `az login` gives Azure CLI a developer identity; it does not reproduce a Foundry
  hosted identity, the Bot token service, a real delegated finance assertion or
  platform-managed state.
- The .NET resolver explicitly uses a system-assigned `ManagedIdentityCredential`
  for Search/embeddings/reranking. Both OBO implementations use a managed-identity
  assertion unless an approved confidential-client secret is supplied. Do not
  assume a laptop login makes every downstream integration work.
- Setting a client secret does not fix the other platform/MI/network dependencies.
  The customer must approve any separate cloud-connected local harness; it is not
  part of the turnkey workflow documented here.
- Starting a local HTTP server or receiving a health response is not proof that
  Teams/M365 can reach it, obtain an OAuth token or execute a finance request.
- Agents Playground can be useful for a future/local **simulated** Activity harness,
  but no ready-to-run Playground authentication configuration is supplied here.
  A tunnel does not establish the native Foundry/BotServiceRbac/SSO trust contract.
  Do not repoint the shared development Bot to a developer tunnel.
- Do not disable assertion verification, accept invented users/tokens or substitute
  the developer's own broad data permissions to make a local scenario pass.

For debugging, set breakpoints in the language's unit tests and invoke the relevant
scenario with controlled dependencies. For a real integration failure, use the isolated
hosted development agent and sanitized correlation logs. Never enable full token,
OAuth-state, Activity or finance-result logging in a debugger shared with others.

## Workstation and build-runner catalogue

| Requirement | Who needs it | Provisioning / policy considerations |
| --- | --- | --- |
| Windows workstation and PowerShell 7 | Repository scripts | Corporate-managed device, approved execution/signing policy, Git and certificate/proxy trust |
| Git and approved repository access | All developers | Use a customer-authorized source location; do not copy local secret files or another developer's `.azure` folder |
| VS Code or approved IDE | All developers | .NET/C# or Python debugger as appropriate; Excalidraw viewer only if editing diagrams |
| .NET 10 SDK | .NET developers; current resolver publication tooling also reuses the .NET SQL client | Use the tested toolchain from the .NET guide; do not replace the pinned package graph during onboarding |
| Python 3.13 and isolated virtual environment | Python developers | Native SQL driver requires compatible interpreter/OS/architecture and platform libraries |
| Azure CLI | Cloud-authenticated developer/admin tasks and publication scripts | Correct customer tenant/subscription; endpoint, CA and tenant-restriction policies apply |
| Azure Developer CLI and Foundry extension | Hosted-code packaging/provisioning/deployment | Use an approved tested pair; record versions. The demonstrated pair is azd 1.33.0 / `azure.ai.agents` 1.0.0-beta.13 |
| Approved NuGet, Python and public-source access | Restore and the .NET Activity compatibility build | Enterprise feeds/mirrors must contain the exact dependencies and source revision; source download is a supply-chain approval item |
| Access to real Azure/Fabric/Power Platform services | Integration loop only | Local unit tests should not require finance permissions or cloud provisioning |

The [language guides](../README.md#choose-an-implementation) are authoritative for
dependency pins. The .NET Activity build is unsigned and unofficial public-source
compatibility code: require explicit customer support/provenance approval rather than
assuming it is a Microsoft-supported binary. Do not use internal-only packages.

No Docker engine, ACR or Kubernetes tooling is required for this selected direct-code
workflow. Corporate proxy configuration must support each tool separately; a working
browser does not establish that NuGet, pip, Git, azd or Fabric SQL will work.

## Fresh checkout

Obtain source from the approved repository or customer mirror. The commands below
assume a checkout at `C:\src\ZavaFinance`; substitute the chosen local directory if
different. Do not copy ignored `.azure`, `.env`, `.venv` or credential files from
the demonstration environment.

For a new checkout only:

```powershell
$repositoryUrl = Read-Host 'Approved repository or customer mirror URL'
git clone $repositoryUrl C:\src\ZavaFinance
Set-Location C:\src\ZavaFinance
```

Use an approved branch/commit and record it in the release evidence. Do not run this
clone command over an existing working directory; use the existing checkout and
preserve uncommitted work. Restore only through approved package/source endpoints.

## Offline .NET development

The application needs .NET 10 **and** the approved local Activity feed; installing the
SDK alone is insufficient. The source-build script uses pinned public source and
defaults to SDK **10.0.303** and its documented dependency profile. Use PowerShell
**7.2 or later**. Have the build/supply-chain owner either provide the approved feed
bundle or authorize its creation.

For an initial source build with no retained feed lock, choose a dedicated work
directory **outside** the repository:

```powershell
Set-Location C:\src\ZavaFinance
$sdkWorkDirectory = Join-Path $env:LOCALAPPDATA 'ZavaFinance\activity-sdk-build'
.\scripts\build-experimental-activity-sdk.ps1 `
    -WorkDirectory $sdkWorkDirectory `
    -OutputDirectory .\.artifacts\one-sdk
```

The script requires the selected SDK to be installed and source/feed downloads to be
allowed. It rejects incompatible reuse of an existing source/version/dependency
profile; do not bypass that guard or delete another developer's workspace. An
enterprise mirror is acceptable only if it supplies the exact reviewed source and
packages; review the script's `-NuGetSource` option with the build owner.

**Reproducibility gate:** the optional upstream build dependency lock is **not
supplied by a pristine clone**. A source-pinned first build can resolve a different
transitive graph from a prior feed build. To reproduce an approved feed, obtain its
retained `packages.lock.json` artifact and pass it explicitly:

```powershell
$approvedSdkLock = Read-Host 'Absolute path to the approved feed dependency lock'
.\scripts\build-experimental-activity-sdk.ps1 `
    -WorkDirectory $sdkWorkDirectory `
    -OutputDirectory .\.artifacts\one-sdk `
    -DependencyLockFile $approvedSdkLock
```

These are **alternative acquisition/reproduction paths**, not instructions to rebuild
the feed twice on every code change. Retain the accepted feed, provenance and lock
in the customer's artifact system. Ordinary application package locks are not a
substitute for that separate source-build lock.

Once the feed exists, run from the repository root:

```powershell
dotnet test .\src\dotnet\tests\ZavaFinance.Tests\ZavaFinance.Tests.csproj `
    -c Release --filter 'FullyQualifiedName!~RoutingEvalTests'
.\scripts\deploy-dotnet.ps1 -PublishOnly
```

The first command restores/builds as needed and runs the offline suite. The second
reruns offline tests and creates a framework-dependent **linux-x64** validation bundle
in `.artifacts\dotnet-agent`; it performs no Azure/channel configuration. The wrapper
will not overwrite an unmarked publish directory or follow an unsafe link/reparse
point. Investigate such a failure instead of bypassing its cleanup guard.

This bundle is not the source directory used by azd. The Foundry extension independently
publishes the actual host project; see
[.NET hosted-code packaging](deployment.md#net-hosted-code-packaging).
`-NoRestore` affects the wrapper's local work, not that independent packaging step.
The protected hosted entrypoint is not an anonymously callable local finance server.

## Offline Python development

Run each step only after the previous one succeeds. Create the isolated environment
once, then rerun dependency installation when the dependency manifests change:

```powershell
Set-Location C:\src\ZavaFinance
Push-Location .\src\python
py -3.13 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt
.\.venv\Scripts\python.exe -m pip check
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
Pop-Location
```

Use that virtual environment as the IDE interpreter. Explicit executable paths
avoid interpreter ambiguity and do not require relaxing PowerShell execution policy
to activate an environment. Keep separate environments for unrelated projects.

Normal Python code changes need only the selected tests and then the full offline
suite above; do not reinstall packages every time. The Python deployment wrapper
expects this repository-local Windows virtual environment and reruns dependency
consistency/tests before its cloud write.

`requirements-dev.txt` includes `requirements.txt`, which applies `constraints.txt`;
install the complete development requirements rather than only a debugger or a
hand-picked subset of runtime libraries.

The hosted Python SQL path also has **native Linux dependencies**. Its readiness
check verifies `ltdl`, `krb5`, `gssapi_krb5` and the bundled `mssql-python` provider.
A passing Windows unit suite is not proof those libraries exist in the selected
managed runtime. Check hosted startup/readiness after deployment; a missing library
is a runtime compatibility issue, not a reason to bypass SQL TLS or user authentication.

The local `.env.example` is a configuration contract, not a working customer
environment. Supply real approved values only for a deliberately authorized integration
experiment, and do not fabricate Foundry ingress/platform variables. The production
entrypoint is hosted-only; `/activity/messages` must not be exposed anonymously.

### Existing VS Code tasks

Use **Terminal > Run Task** after installing/restoring the selected toolchain:

- **Zava Finance .NET: tests**
- **Zava Finance .NET: publish** - local `-PublishOnly`, not an Azure deployment
- **Zava Finance Python: tests**

The repository supplies these in [tasks.json](../.vscode/tasks.json), not a complete
cloud-connected F5 launch profile. Use the IDE's test debugger for the offline inner
loop; do not invent a launch configuration that bypasses native hosting authentication.

## Optional local routing-model evaluation

The .NET suite includes an explicitly cloud-connected golden-set evaluation using
`DefaultAzureCredential`. After offline restore/tests, run it only with customer
approval for the model requests and charges, from an allowed network and with the
developer's approved Azure identity. It uses test tools; it is **not** a full local
finance-channel test.

In a dedicated PowerShell session at the repository root:

```powershell
$env:FOUNDRY_PROJECT_ENDPOINT = Read-Host 'Approved customer Foundry project endpoint'
$env:ModelDeployment = Read-Host 'Approved routing model deployment name'
dotnet test .\src\dotnet\tests\ZavaFinance.Tests\ZavaFinance.Tests.csproj `
    -c Release --no-restore --filter 'FullyQualifiedName~RoutingEvalTests'
```

Set `ModelReasoningEnabled` only according to the approved model configuration.
Without the required endpoint/deployment settings, these tests are **skipped**, not
evidence of a successful integration. Close this dedicated shell afterwards so the
opt-in variables do not unexpectedly enable live tests during the ordinary inner loop.

`FOUNDRY_PROJECT_ENDPOINT` is valid here as a **local test** input. Do not copy it,
or other reserved `FOUNDRY_*` variables, into the hosted manifest's application
environment; the platform injects its hosted endpoint.

## Customer administrator bootstrap

The deployment wrappers are **not a complete subscription/tenant provisioning
product**. They rely on pre-existing Foundry and business-service configuration.
Do not tell a new customer to run `azd up` and expect Azure, Entra, Microsoft 365,
Power Platform and Fabric to be configured automatically.

### Infrastructure automation actually supplied

| Repository asset | What it does | What it does not establish |
| --- | --- | --- |
| [Search template](../infra/search.bicep) and [development Search parameters](../infra/environments/dev.search.bicepparam) | Create/update a named Search service using an Azure Verified Module, semantic configuration capability, configured network mode/tags and role assignments | Foundry, Bot, finance data, Entra consent, complete private-network plumbing or a populated/active resolver index |
| [Runtime Search access](../infra/runtime-search-access.bicep) | Add scoped runtime reader access to an existing Search service | Create Search or grant financial SQL access |
| [Model access](../infra/model-access.bicep) | Assign model/embedding access on an existing account, with optional publisher access | Create the model deployments or request quota |
| [Bot OAuth template](../infra/oauth.bicep) and [parameters](../infra/oauth.bicepparam) | Configure the named Bot connection with the supplied approved OAuth inputs | Create the Bot, register the finance app, grant consent, create or rotate the app password |
| [Native agent manifest](../azure.yaml) and selected deployment wrapper | Package/deploy the selected native application through the Foundry extension, then reconcile its deployed Bot | An end-to-end greenfield landing zone or all cross-service/tenant prerequisites |

There is no top-level complete `infra\main.bicep` greenfield deployment in this
repository. The platform owner must establish the approved Foundry native-hosting/
publishing foundation and verify the resulting Bot/runtime identity and azd outputs
before channel reconciliation. Use the supported Foundry provisioning/publication
workflow for that target; do not create an unrelated generic Bot and assume its
identity will match the extension's selected service.

Official platform contracts are documented in
[source-code hosting](https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent-code),
[native Activity configuration](https://learn.microsoft.com/azure/foundry/agents/concepts/azure-yaml-reference#additional-runtime-protocols-and-activity-endpoints)
and [Teams/M365 publishing](https://learn.microsoft.com/azure/foundry/agents/how-to/publish-copilot-virtual-network).
These references are not a replacement for the selected extension's validated
workflow; generic image/Responses examples are not drop-in native Activity recipes.

Before choosing private-only resources, resolve the
[three-boundary networking and public Activity-route requirements](it-admin-catalogue.md#foundry-private-networking-and-the-microsoft-365-public-route).
The exact native Activity/private-egress/managed-state combination needs validation;
the documented Teams route retains a scoped public ingress exception. Also obtain
the [Fabric and Power Platform approvals](it-admin-catalogue.md#fabric-and-power-platform-administrator-prerequisites):
Azure resource access alone does not enable their tenant features, licensing or data access.

Complete these work packages in order; the customer can use approved portal
procedures or its own reviewed IaC/automation for steps outside this repository:

| Stage | Customer work | Exit evidence |
| --- | --- | --- |
| 1. Approve the development boundary | Resolve the [catalogue's decision gates](it-admin-catalogue.md#1-scope-and-decision-gates), select one/both variants and name pilot owners | Approved inputs, policy decisions, licence/capacity plan and network design |
| 2. Prepare Azure platform dependencies | Create/select Foundry account/project, model/embedding deployments and Search; validate region, quota, supported native hosting and effective policy | Exact resource IDs/endpoints, deployment names, feature-compatible Search configuration and access owners |
| 3. Prepare business services | Fabric capacity/workspace/lakehouse/data/SQL endpoint and published Data Agent; published Copilot Studio KPIpedia | Active dependencies, real IDs/schema, approved data contract and successful owner-side checks |
| 4. Prepare identity | Finance OAuth registration/service principal, enabled exposed scope, downstream delegated permissions/consent, credential mechanism and pilot user access | Consent/access record and approved first-time Bot OAuth inputs; if selected, federation is bound when the actual runtime principal exists |
| 5. Prepare source/toolchain/configuration | Fresh-clone validation, approved .NET transport build if selected, customer manifest/resource/branding review, separate local azd environment | No demonstration tenant/resource endpoints in the selected deployment; packages/configuration approved |
| 6. Publish finance terminology | Stage catalogue, wait for SQL synchronization, verify, create/populate/probe versioned Search index, activate release | One coherent Fabric catalogue/Search/embedding binding and independently reconciled sample values |
| 7. Deploy the selected native agent | Execute the targeted wrapper only after its preconditions; obtain the actual agent/Bot/runtime identity | Selected customer agent version and distinct Bot/identity readback |
| 8. Complete scoped runtime/channel grants | RBAC/identity owner approves model/Search grants, the selected runtime OBO credential path and the named `mcs` connection; build branded app package | Ingress and downstream identities/scopes agree; secret never printed or put in the package |
| 9. Install/assign the pilot app | Follow the customer's M365 app policy; personally upload only if permitted, otherwise organizational distribution | App installed/assigned to intended pilot users; source ZIP is not the Teams manifest ZIP |
| 10. Accept the customer deployment | Run fresh-auth, all-tool, two-user, network, financial and operational tests | Signed [customer acceptance evidence](it-admin-catalogue.md#customer-development-acceptance-and-handover) |

Stages 7 and 8 are combined by the full wrapper when its principal is permitted to
perform the channel/RBAC work. If the wrapper stops after a successful agent deployment
because a scoped grant or OAuth prerequisite is missing, do not redeploy repeatedly.
Have the relevant administrator complete the approved prerequisite and run the
targeted channel reconciliation described in [deployment](deployment.md#reconcile-bot-configuration).

The catalogue generator and calculator are Zava finance contracts, not a generic
importer for arbitrary customer tables. Agree mappings, formulas, scope security and
known-answer tests before replacing synthetic data. The publication scripts also
reuse built .NET SQL-client dependencies: a Python-only application deployment does
not mean the current **publisher tooling** is Python-only.

### Sign in and create a separate customer azd environment

From the repository root, enter **non-secret approved values**. These commands are for
the customer to execute after approval; they were not executed against a customer
subscription when preparing this document:

```powershell
$tenantId = Read-Host 'Approved customer tenant ID'
$subscriptionId = Read-Host 'Approved customer development subscription ID'
$location = Read-Host 'Approved Azure region'
$environmentName = Read-Host 'New customer development azd environment name'

az login --tenant $tenantId --output none
az account set --subscription $subscriptionId
az account show --query '{tenant:tenantId,subscription:id,name:name}' --output table
azd auth login --tenant-id $tenantId
azd env new $environmentName --subscription $subscriptionId --location $location
```

Confirm the readback belongs to the intended customer before continuing. Azure CLI
and azd authentication are separate; a successful login to one is not proof of the
other's selected account. Use the customer's approved interactive/automation identity
flow; device-code login is not a workaround for Conditional Access.

`azd env new` creates local environment configuration and changes the local default;
it does **not** provision Azure resources, change embedded manifest literals, create
Entra consent, grant data access or install the Teams app. Use an explicit
`--environment`/`-EnvironmentName` in subsequent commands.

**Isolation warning:** the manifest fixes agent/service names to `zavafinance-one`
and `zavafinance-one-python`. Two azd environment names pointing at the same Foundry
project and same agent name do not create two independent deployments. Use a distinct
approved Foundry project for environment isolation, or deliberately adapt and test
all name-dependent tooling. Do not rename only the YAML service key; wrappers,
identity-output lookup and app-package paths depend on those names.

### Populate the required customer configuration

The wrapper's [environment initializer](../scripts/Agent-Deployment.ps1) requires
all the following keys. Most are shared integration inputs, not per-user credentials:

| azd input | Meaning / host mapping |
| --- | --- |
| `AZURE_SUBSCRIPTION_ID` | Customer subscription, normally set by `azd env new` |
| `AZURE_RESOURCE_GROUP` | Approved target RG used by deployment/channel administration |
| `AZURE_AI_PROJECT_ENDPOINT` | Approved existing Foundry project used by deployment tooling |
| `OBO_CLIENT_ID`, `OBO_TENANT_ID`, `OBO_AUDIENCE` | Approved finance OAuth application, tenant and expected user assertion audience; .NET maps to `Obo__*`, Python to `OBO_*` |
| `OBO_CLIENT_SECRET` | **Secret. Required by the current wrappers**, passed into the application configuration and consumed for missing Bot OAuth setup |
| `COPILOT_STUDIO_ENVIRONMENT_ID`, `COPILOT_STUDIO_SCHEMA_NAME` | Published KPIpedia connection; .NET maps to `CopilotStudioAgent__*`, Python keeps these names |
| `FABRIC_SQL_ENDPOINT`, `FABRIC_DATABASE` | Fabric SQL host/database; .NET maps to `Fabric__SqlEndpoint`/`Fabric__Database` |
| `FABRIC_WORKSPACE_ID`, `FABRIC_DATA_AGENT_ID` | Published Data Agent location; .NET maps to `Fabric__WorkspaceId`/`Fabric__DataAgentId` |
| `RESOLVER_SEARCH_ENDPOINT`, `RESOLVER_EMBEDDING_ENDPOINT` | Approved Search and embedding HTTPS endpoints; .NET maps to `Resolver__SearchEndpoint`/`Resolver__EmbeddingEndpoint` |

Set only **non-secret** inputs using the approved handover values:

```powershell
$nonSecretKeys = @(
    'AZURE_RESOURCE_GROUP', 'AZURE_AI_PROJECT_ENDPOINT',
    'OBO_CLIENT_ID', 'OBO_TENANT_ID', 'OBO_AUDIENCE',
    'COPILOT_STUDIO_ENVIRONMENT_ID', 'COPILOT_STUDIO_SCHEMA_NAME',
    'FABRIC_SQL_ENDPOINT', 'FABRIC_DATABASE',
    'FABRIC_WORKSPACE_ID', 'FABRIC_DATA_AGENT_ID',
    'RESOLVER_SEARCH_ENDPOINT', 'RESOLVER_EMBEDDING_ENDPOINT'
)
foreach ($key in $nonSecretKeys) {
    $value = Read-Host "Approved customer value for $key"
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "A customer-approved value is required for $key."
    }
    azd env set $key $value --environment $environmentName
    if ($LASTEXITCODE -ne 0) {
        throw "Could not set $key; stop before deploying."
    }
}
```

Have the credential owner deliver `OBO_CLIENT_SECRET` into the chosen ignored azd
environment through an approved secure process. It is stored in local environment
configuration, not magically encrypted by azd. Do not put it in a command example,
shell history, ticket, tracked configuration or app manifest. If local plaintext
secrets or client secrets are prohibited, **stop for a reviewed deployment/configuration
change**; the current wrapper cannot satisfy that policy merely by omitting the key.

The runtime code supports MI-federated OBO, but the current wrappers require the
secret and the manifest supplies it to the runtime. Therefore federation is **not
the secretless deployment default**. Separating the runtime credential from the
Bot's credential requires deliberate manifest/tooling changes, federation setup and
end-to-end validation. `configure-channel.ps1` consumes an existing secret; it does
**not create or rotate** the Entra application password.

Additional per-variant settings:

| Input / value | Handling |
| --- | --- |
| `ONE_SESSION_KEY_SALT` / `PYTHON_SESSION_KEY_SALT` | Wrapper generates a salt if absent, never rotates an existing one. Treat as secret; preserve it for that environment/variant |
| Per-variant token-exchange resource | Supply the approved value explicitly with `-UserAuthResource`; do not derive it from the other variant |
| First-time delegated scopes | Supply the enabled finance API scopes explicitly with `-DelegatedScopes`; validate independently from the token-exchange resource |
| `ONE_APP_PACKAGE_VERSION` / `PYTHON_APP_PACKAGE_VERSION` | Deliberate three-part package version updates, not automatic code-deployment versioning |
| Search ARM resource ID | Supply separately via `-SearchResourceId`; it must match `RESOLVER_SEARCH_ENDPOINT` |
| Routing model/reasoning and semantic configuration | Review the **literal manifest values**, not only azd keys: both variants select `gpt-5.4-mini`, reasoning `true` and `resolver-semantic` |
| Content capture | Keep Python GenAI content capture disabled and .NET subagent-text logging disabled; privacy settings are not debugging shortcuts |

Setting an azd value does not override a literal in [azure.yaml](../azure.yaml).
Review both selected-service configuration and language defaults before changing a
model or semantic configuration. Re-run the relevant model/resolver tests after
such a change. Keep the active catalogue's embedding deployment and dimensions coherent.

Review the fixed display names/branding and package metadata too. The channel script
currently applies `purpose=demo` and `owner=gbelenky` to the selected Bot. If customer
tag policy requires different values or additional tags, make an approved source/
configuration adaptation **before** execution; changing the azd environment name
does not change those tag literals.

## First deployment and real-channel testing

After customer configuration, catalogue publication and identity preparation, follow
[build and deploy one implementation](deployment.md#build-and-deploy-one-implementation).
The wrapper performs the selected variant's validation, targeted azd deployment,
channel reconciliation and branded-package generation. It is **not a no-side-effect
developer build command**.

For first-time `mcs` creation, the approved token-exchange resource and delegated
finance API scopes must be supplied explicitly, together with the securely delivered
finance client credential. No scopes are inferred. The script rejects a conflicting
existing connection instead of silently replacing it; make an identity change
through the customer's approval process.

When first-time channel setup has been approved, run **one** of the following
from the repository root. The secret must already have been securely supplied to
the selected azd environment; the prompted values below are not secret:

```powershell
$searchResourceId = Read-Host 'Approved customer Search ARM resource ID'
$userAuthResource = Read-Host 'Approved token-exchange resource for the selected variant'
$delegatedScopes = Read-Host 'Approved finance API scope value for the mcs connection'

# .NET
.\scripts\deploy-dotnet.ps1 `
    -EnvironmentName $environmentName `
    -SearchResourceId $searchResourceId `
    -UserAuthResource $userAuthResource `
    -DelegatedScopes $delegatedScopes
```

Or, after the Python environment and tests are ready:

```powershell
.\scripts\deploy-python.ps1 `
    -EnvironmentName $environmentName `
    -SearchResourceId $searchResourceId `
    -UserAuthResource $userAuthResource `
    -DelegatedScopes $delegatedScopes
```

Use the selected variant's approved values if switching variants; do not reuse a
token-exchange resource by assumption. The Bot connection needs an enabled exposed
finance scope, **not** a downstream `.default` request or a wildcard. The channel
script validates it against the finance registration.

Verify the actual generated package and its corresponding Bot:

| Variant | azd service | Installable app package |
| --- | --- | --- |
| .NET | `zavafinance-one` | `appPackage\dotnet\build\zavafinance-one.zip` |
| Python | `zavafinance-one-python` | `appPackage\python\build\zavafinance-one-python.zip` |

The full wrappers target only one service, but share the customer input dependencies.
Do not use an unscoped deployment command to update a single variant. Never install
the .NET hosted application-code ZIP as a Teams app.

Start a new chat in the installed/assigned customer app and test all three tools,
typed/clicked clarification and follow-ups. Then test fresh sign-in and a second
restricted user, verify statement values independently, and review analytical
narratives separately. Record failures with sanitized IDs; a successful administrator
test does not prove a pilot user's access or financial correctness.

## Day-to-day change and release discipline

1. Edit only the selected implementation, or deliberately change both when modifying
   a shared business contract. Preserve native framework/tool/session abstractions.
2. Run the smallest relevant unit-test selection, then the selected variant's release
   validation before deployment. Keep offline tests independent of cloud credentials.
3. For a terminology/formula change, follow the
   [finance change contract](finance-controller-architecture.md#making-a-business-change).
   The resolver catalogue is not activated by deploying application code.
4. Deploy only the selected service in the customer's development environment using
   the [deployment runbook](deployment.md). A change to code is not automatic authority
   to modify Entra consent, network policy or another variant's Bot.
5. Read back the deployed version and open a **new conversation in that agent**.
   A native session can remain pinned to an older version; `reset` clears finance
   context, not its runtime selection or authentication.
6. Capture test evidence, active catalogue binding and artifact/version information.
   Obtain customer finance/security acceptance before expanding the pilot.

Code-only changes with unchanged app identity/manifest/authentication do not require
reinstallation of an already installed package. App name/version/OAuth changes do
require a reviewed package update and the customer's install/distribution process.

## Shared development and secret handling

- Coordinate deployments to a shared agent: one developer's publish affects other
  testers and can interrupt process-owned work. Use separate approved agent/Bot/state
  boundaries when independent cloud experiments are needed; a new Git branch alone
  is not runtime isolation.
- Use separate per-variant state stores and stable random salts. Do not regenerate
  salts during ordinary deployments or copy one from another environment.
- Prefer approved identity federation for runtime OBO. A developer workstation does
  not have that hosted managed identity; any alternate local credential path is a
  separately approved exception, not an instruction to copy a hosted secret.
- Keep secrets outside Git and generated packages. Ignored files are not encrypted
  vaults. Protect local caches/configuration with corporate device controls and
  delete them according to policy when offboarding.
- Store no user access tokens in configuration or test fixtures. Inspect application
  packages, publish folders and CI logs for accidental credentials before release.
- Do not paste secret-bearing environment dumps into tickets or AI assistants.
  Share redacted key names, sanitized error codes and resource identifiers instead.

## Completion and retirement

Complete the [customer acceptance matrix](it-admin-catalogue.md#customer-development-acceptance-and-handover)
against the customer's actual deployed version. Existing
[demonstration evidence](operations.md) is a useful regression baseline, not customer
sign-off. In particular, independently test fresh SSO, two-user authorization/isolation
and financial correctness of exploratory output.

For rollback, preserve a compatible code artifact, configuration, app package and
catalogue binding. Validate state schema compatibility before selecting an older
release; do not “roll back” by rotating a salt or changing a Bot to another variant.

For teardown, have each owner distinguish exclusive from shared resources and
retention obligations. Remove only the authorized pilot installation/deployment and
dedicated resources. Do not run a blanket resource-group deletion or `azd down` against
shared Foundry, Search, Fabric, identity or KPIpedia resources.
