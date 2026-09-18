# Administrator deployment runbook

This runbook prepares and verifies **independent dev/staging/prod environments**.
It does not authorize deploying staging or production. The templates and documentation
have been compiled/reviewed locally; only explicitly recorded demo checks below are cloud
deployment evidence.
Read the [IT-admin catalogue](it-admin-catalogue.md) for all identities, configuration,
dependencies, customer policy questions and acceptance gates.
Before approving a release, also review the dated
[availability and support register](availability-and-support.md). A GA hosting service does not
remove the application's prerelease SDK or preview API exceptions.

## Activity-protocol branch: choose the application first

This runbook's tier provisioning and Channel rollout steps describe the **original
two-host deployment**. The branch also contains **Zava Finance One**. Never run an
unscoped `azd deploy` to update one of them: the manifest also contains a separate Python service.

| Target | Deployment boundary | Application guide |
| --- | --- | --- |
| Original Zava Finance | Explicit `zavafinance` service and separately approved Channel deployment | This runbook |
| Zava Finance One | Explicit `zavafinance-one` service, then guarded One configuration script | [One build/deploy guide](../src/ZavaFinance.One/README.md#reproducible-build-and-isolated-deployment) |
| Zava Finance One Python | Explicit `zavafinance-one-python` service and `configure-one-channel.ps1 -Variant Python` | [Standalone Python guide](../src/ZavaFinance.One.Python/README.md) |

One uses a portable, locked, framework-dependent .NET publish and native Activity
2.0.0 with BotServiceRbac. It does not use the Channel Function App, P1v3 plan,
delivery Storage or DTS. Do not run `main.bicep`, repoint the original Bot, or
remove its resources for One. No crash recovery is enabled.

Use [the branded One ZIP](../appPackage/one/build/zavafinance-one.zip), not the
generic One.Host `appPackage.zip`. One is installed and interactive authentication
works, but initial silent SSO remains unresolved. Local rejected-invoke fixes
and passing tests are newer than deployed v1. Fabric remains paused; obtain
approval before resuming it for finance acceptance and pause it afterward.

## Choose the smallest deployment boundary

| Artifact | Creates/changes | Does not create/change |
|---|---|---|
| `infra\main.bicep` | Channel UAMI, dedicated P1v3 plan/Function App, private host/state Storage, VNet/Storage PEs/DNS, DTS, Bot/Teams channel, monitoring, scoped roles | Foundry project/Agent/models, Search, Entra app/consent/OAuth connection, Fabric, Copilot Studio |
| `infra\search.bicep` | Dedicated Search service through **AVM 0.13.0**, RBAC-only auth, paid semantic ranker, optional PE/DNS group and diagnostics, runtime/publisher roles | Existing Channel/Storage/Foundry resources; Search index/documents; hosted VNet injection; new DNS zones/VNet links |
| `infra\model-access.bicep` | Runtime and optional publisher **Cognitive Services OpenAI User** roles on an existing model account | Model deployment or quota; no model/account replacement; `assignPublisherAccess=false` preserves an existing publisher grant |
| `infra\foundry-access.bicep` | Channel project access and optional project MI/account access | Hosted Agent version, endpoint protocol migration or Entra permissions |
| `infra\one-oauth.bicep` | Only the separate One Bot's `mcs` OAuth child | Original Bot or shared Entra app/consent; does not establish silent SSO |
| `infra\one-search-access.bicep` | Search Index Data Reader for One's runtime on existing Search | Search service/index or publisher permissions |
| `scripts\configure-one-channel.ps1` | Guarded One OAuth, missing Search/model grants, tags and branded app package | Original Bot, new finance permissions, recovery or tenant-wide publication |
| `infra\registry-access.bicep` | Platform image-pull and optional CI image-push roles on an existing ACR | Registry creation/network changes; no runtime push access |
| `infra\environments\*.bicepparam` | Tier-specific ARM inputs read from process environment | No implicit subscription selection; no stored secret defaults |
| `infra\environments\*.agent.env.example` | Exact application settings contract with tier-specific input references | No automatic dotenv loader, no replacement of `azure.yaml`, no actual secret values |
| `scripts\Publish-ResolverData.ps1`, `Test-ResolverData.ps1`, `Publish-ResolverSearch.ps1` | Explicitly invoked Fabric/catalogue/Search publication stages | Azure ARM resources, Entra consent or end-user access grants |

**Adding the resolver to the demo:** use the independent Search and model-access templates.
Do not apply `main.bicep` or a new-tier channel parameter file merely to add Search. That would
also reconcile existing App Service, settings, RBAC and networking and could change state IDs.

For this original-app add-on, use standalone Search/model-access ARM operations and **code-only `azd deploy zavafinance`**
for the hosted Agent. Do not run global `azd provision`, `azd provision --preview` or `azd up`:
global infrastructure discovery can select the Channel's `infra/main.bicep` and request its
unrelated parameters, including `botAppId`. That is not a reason to provision the Channel again.

### Approved demo resource selection

| Component | Demo target |
|---|---|
| Search resource group | Existing `rg-zavafinance-swc` |
| Search service | **New independent** `srch-zavafin-dev` in `swedencentral`; never reuse a different solution's Search service |
| Demo Search sizing/network | **Basic, 1 replica / 1 partition**, approved public Entra-authenticated endpoint with no IP allowlist or service bypass; generic tier templates retain their separate Standard sizing recommendations |
| Version-specific Search index | `zava-resolver-2026-09-16-v1`; semantic configuration `resolver-semantic`, vector profile `resolver-vector` |
| Existing Foundry/model account | `rsc-fdr-swc`; no replacement or new model account |
| Routing and constrained reranking | Existing `gpt-4.1-mini` deployment |
| Embeddings | Existing **Standard** `text-embedding-3-large` deployment, requested at **1536 dimensions** |
| Alternate embedding deployment | `text-embedding-3-large-2` is **GlobalStandard**; do not select it silently or provision a duplicate deployment |

The active demo catalogue version is `2026-09-16-v1`. Activation writes exactly one
`resolver_release` row with `catalog_version='2026-09-16-v1'`,
`search_index='zava-resolver-2026-09-16-v1'`, `embedding_deployment='text-embedding-3-large'`
and `embedding_dimensions=1536`. Do not activate before Search publication/probes succeed.
The four fields travel together; there is no independent runtime index environment alias.

**Verified demo publication, 16 September 2026:** 114 catalogue rows (90 org + 18 KPI +
six families), 336 scope rows, one calendar row and **one active release row** matching the
index/deployment/dimensions above. Activation followed exact Search ID/count/version checks
and a hybrid/semantic top-three candidate-recall probe. Gross margin ranked second for the
test paraphrase; fuzzy suggestions require confirmation rather than trusting rank one.
All 13 delegated SQL invariants/reference checks passed again after activation.

Runtime Search Reader and embedding inference assignments are verified; publisher permissions
remain separate. The optional publisher toggle produces a no-change model-access what-if
against the existing runtime grant without duplicating the publisher grant. Hosted v13 rollout
and signed-in M365 resolver checks are recorded in the [handover](../HANDOVER.md), separately
from data publication: exact figures, KPI/org selections, semantic retrieval, numbered/ordinal
continuation, hierarchy scopes and stale-card rejection passed. The initial Channel package's
mixed text/card response-ID defect is corrected by one attachment-only activity containing
numbered choices/fallback. The Agent still returns numbered text and typed options.
Final combined regressions: **332 passed**. Fresh v13 testing verified one card per clarification,
six confirmed post-persistence completions, zero exceptions in the test window and three
cards/three finance answers retained after browser reload. Empty message IDs still fail;
delivery remains at-least-once, not exactly-once.

**Channel UX follow-up, 18:05 UTC:** new clarification cards now submit directly from numbered
clickable rows, without radio inputs or Continue. The Channel-only ZIP passed 90 targeted tests
and signed-in KPI/org click and numeric-reply checks; Agent v13, settings, MI/RBAC and data were
not redeployed. Three cards and three answers survived reload. Historical/cached cards retain
their original layout. See the [handover](../HANDOVER.md) for the deployment and delivery evidence.

The dev Search parameter file defaults to this approved name/region. Subscription, runtime/
publisher principal IDs, network decision and monitoring scope are still explicit inputs.
Validate the existing model endpoint's runtime/publisher access and network path; use
`model-access.bicep` only for missing grants. A regional model restriction requires a documented
decision before changing model deployment type or reindexing. Staging/production remain
undeployed and must not share this demo index.

The hardened `main.bicep` now declares private Storage explicitly instead of relying on Policy,
defaults to P1v3, parameterizes tier capacity/IPs/retention, sets host Entra telemetry auth and
makes unused queue RBAC optional. These changes require their **own reviewed what-if** before
an existing Channel is reconciled. Incremental mode does not revoke old role assignments.

## 1. Record approved inputs

Before running ARM commands, identify the tenant, subscription, resource groups, regions,
effective policies, network/DNS owners and secure input mechanism. Create required Entra
registrations/consent and Foundry project/runtime through their separately approved processes.
Record the **actual hosted runtime principal object ID**, the project infrastructure MI object
ID and the Channel UAMI object/client IDs separately.

For each tier, use the `ZAVA_DEV_*`, `ZAVA_STAGING_*` or `ZAVA_PROD_*` variables documented
in the catalogue and referenced by the corresponding `.bicepparam`. The hosted Agent templates
add non-secret OBO audience, Fabric workspace/database/SQL/data-agent coordinates, Copilot Studio
environment/schema, Search service endpoint and embedding account endpoint inputs for that same
tier. Runtime index, catalogue version, embedding deployment and dimensions come from the
single delegated Fabric release row.

Do not set a subscription implicitly from a workstation default. Every deployment example
below takes `$subscriptionId` and a resource group explicitly. Assign Search and model roles to
**different** runtime and publisher principals; reject identical object IDs before proceeding.

### Network input examples (choose one, after approval)

```powershell
# Existing private endpoint subnet and privatelink.search.windows.net zone ARM IDs.
# The zone must already be linked/forwarded to both the Agent and publisher networks.
$env:ZAVA_DEV_SEARCH_NETWORK = @{
    mode = 'Private'
    privateEndpointSubnetId = $privateEndpointSubnetId
    searchPrivateDnsZoneId = $searchPrivateDnsZoneId
} | ConvertTo-Json -Compress

# Alternatively, only where the customer approved public Search access:
$env:ZAVA_DEV_SEARCH_NETWORK = @{
    mode = 'Public'
    allowedIpRanges = $approvedPublisherAndRuntimeIpv4Cidrs
} | ConvertTo-Json -Compress
```

An empty public `allowedIpRanges` array means all source IPs can reach the **Entra-authenticated**
endpoint. It is not “private except managed identities”. For managed hosted egress without
stable addresses, do not invent an IP allowlist; configure supported private egress or obtain
an explicit public-access decision. No service bypass is enabled by the Search template.

Channel `schedulerIpAllowlist` must allow the Channel's actual outbound source addresses;
`[]` denies all. If approved egress restriction requires NAT/firewall/route-all, build and
validate that network change separately; this template does not silently supply it.

### Secure inputs

Read secrets from the organization's approved secret store into the provisioning process.
The channel parameter files reference `ZAVA_<TIER>_SESSION_KEY_SALT` and
`ZAVA_<TIER>_BOT_CLIENT_SECRET`, and the Agent template references the matching values.
Never place secrets on a literal CLI command line, commit rendered parameter files, print
compiled secure parameter JSON, or turn on shell tracing/transcription for secret handling.
Clear secret environment variables after the operation. Certificates are not implemented
as an Agent configuration path; a certificate-only policy needs the change identified in
the catalogue, not an invented PFX setting.

## 2. Local compilation — no deployment

Use the installed Azure CLI/Bicep; do not install/upgrade tooling unless a required capability
is missing. These commands compile to stdout and discard the JSON so no generated ARM files
are left in the repository:

```powershell
$templates = 'main', 'search', 'model-access', 'foundry-access', 'registry-access'
foreach ($name in $templates) {
    az bicep build --no-restore --file ".\infra\$name.bicep" --stdout | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Bicep compilation failed: $name" }
}
```

If the Search build reports **BCP190 module not restored**, restore the pinned published AVM
and repeat the compile (this downloads compiler dependencies, not Azure resources):

```powershell
az bicep restore --file .\infra\search.bicep
if ($LASTEXITCODE -ne 0) { throw 'AVM restore failed.' }
```

After setting a tier's input environment, compile its parameter files without printing secrets:

```powershell
az bicep build-params --file .\infra\environments\dev.search.bicepparam --stdout | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Search parameters invalid.' }
az bicep build-params --file .\infra\environments\dev.channel.bicepparam --stdout | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Channel parameters invalid.' }
```

Compilation is not Azure Policy, quota, permission, regional feature availability or network
validation. The installed Bicep 0.40.2 compiler warns that the existing DTS `2026-02-01` resource
types have no local type definitions; compilation succeeds, but service-side preflight remains
mandatory. Do not claim those warning-bearing resource properties were schema-validated locally.

## 3. ARM preflight and independent Search provisioning

The following commands are **operator instructions**, not commands executed by this change.
Review the what-if, policy effects, billing and identity/network inputs before approving create.
Use an existing target RG; do not silently create subscriptions/resource groups.

```powershell
az deployment group what-if `
    --subscription $subscriptionId --resource-group $searchResourceGroup `
    --parameters .\infra\environments\dev.search.bicepparam
if ($LASTEXITCODE -ne 0) { throw 'Search what-if failed.' }

# Execute only after the environment's approved change authorization.
az deployment group create `
    --subscription $subscriptionId --resource-group $searchResourceGroup `
    --name zavafinance-dev-search `
    --parameters .\infra\environments\dev.search.bicepparam `
    --mode Incremental
if ($LASTEXITCODE -ne 0) { throw 'Search ARM deployment failed.' }
```

Capture `searchEndpoint`, `searchServiceId`, `runtimeReaderAssignmentId`,
`publisherDataAssignmentId` and, if enabled, `publisherSchemaAssignmentId` from the deployment
outputs. No secret or API key is output. Use the endpoint as `Resolver__SearchEndpoint`.
The Search index name is chosen by publication, not generated by ARM.

For an existing embedding account in its own RG, review/apply only its assignments:

If the publisher already has **Cognitive Services OpenAI User** at the model account, verify the
effective assignment and set `assignPublisherAccess=false`. Azure rejects a duplicate
principal/role/scope assignment created with another GUID; the flag provisions runtime access
without trying to recreate the publisher grant. It defaults to `true` for new publishers.
`publisherPrincipalId` remains a required input; when skipped, `publisherEmbeddingAssignmentId`
is empty, meaning **not managed by this deployment**, not that the publisher lacks access.
The flag never revokes an existing assignment.

```powershell
az deployment group what-if `
    --subscription $modelSubscriptionId --resource-group $modelResourceGroup `
    --template-file .\infra\model-access.bicep `
    --parameters modelAccountName=$modelAccountName agentPrincipalId=$agentPrincipalId `
        publisherPrincipalId=$publisherPrincipalId publisherPrincipalType=$publisherPrincipalType `
        assignPublisherAccess=false
if ($LASTEXITCODE -ne 0) { throw 'Embedding-access what-if failed.' }
```

After approval, the same scoped template and parameters can be used with
`az deployment group create --mode Incremental`. It only adds model-account RBAC; an administrator
must separately ensure the approved `text-embedding-3-large` deployment exists with the agreed
version/quota/network and 1536-dimensional request contract. Reuse the tier's `gpt-4.1-mini`
routing deployment for constrained reranking. Do not create an extra model merely for reranking.

Apply `foundry-access.bicep` in the existing Foundry account's RG with `foundryAccountName`,
`foundryProjectName`, `channelPrincipalId` and, when needed, `projectPrincipalId`. Keep
`channelUsesProjectApi=true` for the existing project Responses/conversations client.
Use `registry-access.bicep` in the existing registry RG if explicit image grants are missing;
set `registryUsesAbac` to the registry's actual permission mode and supply
`imagePullPrincipalId` (project infrastructure MI). CI image publishing is optional and separate.
Reconcile existing assignment GUIDs/scopes rather than duplicating identical assignments.

## 4. New-tier Channel provisioning

Use the intended `dev.channel.bicepparam`, `staging.channel.bicepparam` or
`prod.channel.bicepparam` **only** for a separately approved environment. The parameter files
carry separate app names, task hubs, settings inputs, CIDR examples, replica/storage recommendations
and tags. No staging/production deployment is part of the current change.

Run resource-group what-if with that file, inspect all changes and policy outcomes, then follow
the customer's deployment authorization. The Bicep outputs provide Function name/hostname,
messaging endpoint, Bot name, UAMI IDs, Storage/state URI, DTS endpoint/hub, VNet/PE subnet IDs,
and monitoring coordinates.

Then separately:

1. Configure the Bot `mcs` OAuth connection and Entra/Teams SSO as documented in the catalogue.
2. Publish the .NET 10 isolated Channel package to the **dedicated** plan using the approved
   existing application deployment workflow; ARM deployment alone does not upload code.
3. Verify `/health`, function indexing, Storage private DNS, host leases, table diagnostics,
   DTS HTTP2/MI access, Foundry Responses **and** conversation creation.
4. Validate telemetry ingestion separately for the Functions host and isolated worker. Do not
   disable Application Insights local authentication until the actual exporters prove Entra auth.

The Bicep Storage PEs include blob, queue and table; current DTS-backed code does not require
Storage queue RBAC, and `enableQueueStorageRole` defaults false. Old queue role assignments
must be explicitly reviewed/revoked; omitting them from an incremental deployment does not
remove access. The optional DTS dashboard operator role is operational, not read-only.

## 5. Foundry, Fabric, Copilot Studio and publication are separate stages

ARM templates cannot complete these hand-offs:

- **Foundry:** account/project/model quota and infrastructure first; then deploy the hosted Agent
  **data-plane** version with the existing code/image workflow. Inject application settings using
  the tier's Agent template/`azure.yaml` bindings. The runtime owns `FOUNDRY_*` and `AGENT_*`.
  Record source/image version, deployed Agent version, project MI and actual runtime identity.
- **Fabric:** approved capacity/workspace/lakehouse and user sharing; create/run the additive
  catalogue notebook, wait for SQL sync, and test metadata/fact authorization.
- **Search publication:** execute the data runbook's staged export, embedding/index publication,
  hybrid/semantic probe and release activation. Activate `catalog_version`, `search_index`,
  `embedding_deployment` and `embedding_dimensions` together in `dbo.resolver_release`.
  Runtime reads that binding, not a separate index/model alias from environment settings.
  Search publication needs the separate privileged publisher.
- **Entra:** consent, API exposure, app assignments, client preauthorization, OAuth configuration,
  credential/federation rotation and Conditional Access tests.
- **Power Platform/Copilot Studio:** separate tier environment, authenticated/published KPIpedia,
  environment/schema values, user sharing, DLP and knowledge-source scope validation.
- **Teams/M365:** separate app package per tier, current manifest version, production-approved
  privacy/terms/developer URLs, governed publishing and signed-in acceptance on both surfaces.
  The Agent retains numbered clarification text and typed options; the Channel constructs
  Adaptive Cards from the structured result.

No source facts or original financial formulas should be rewritten to fit Search. Fabric remains
the authority; the current four additive resolver tables and versioned index are described in
[resolver-data.md](resolver-data.md). Leave the old release active on any publication failure.

## 6. Verify before exposing users; rollback without widening access

Use the catalogue's acceptance checklist. In particular, confirm Search runtime **write denial**,
delegated SQL metadata visibility with two users, fresh-conversation behavior, safe expiration/
reset/forged clarification handling and end-to-end numerical correctness. Public network
access, a successful ARM result or `/readiness` alone proves none of those.

**Presentation acceptance:** test a known exact ambiguity and a fuzzy suggestion. Verify that
the Agent result contains numbered text plus matching typed options and that the Channel builds
the Adaptive Card from those options. Exercise supported card and numbered-text submissions;
both must resolve the same authorized pending choice and reject expired, tampered,
cross-user/cross-conversation or changed-release selections. Plain direct Responses callers
may retain text compatibility without moving card construction into the Agent.

To roll back, select the previously validated Agent/Channel release and restore the matching
four-field Fabric release binding using approved release operations. Preserve the prior immutable
catalogue/index until retention approval permits deletion. Do not share another environment's
salt, app ID, token or index, and do not disable RLS/authentication to restore availability.
Do not delete the existing F2 capacity or original finance lakehouse as “resolver cleanup”.
