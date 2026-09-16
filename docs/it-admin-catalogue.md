# ZavaFinance — IT administration and policy review catalogue

**Audience:** Azure platform, networking, Entra, Fabric, Power Platform, security and service
operations teams. **Review date:** 16 September 2026. This document contains no tenant-specific
identifiers or credentials and can be shared with a customer before their Azure Policies arrive.

This is a **provisioning and acceptance specification**, not evidence of a production deployment.
Dev is the existing demonstration environment; staging and production are **designs only and
remain undeployed**. No resources are deployed by the templates merely being present.
Sizing below is a recommendation, not a measured throughput or availability guarantee.

## 1. Architecture and non-negotiable boundaries

1. Teams and Microsoft 365 Copilot use Azure Bot Service, which calls the .NET 10 Azure Functions
   **Channel** on a dedicated Linux App Service plan. The channel validates Bot Service JWTs,
   obtains user sign-in, acknowledges quickly, and delivers the eventual answer proactively.
   The Agent returns numbered clarification text plus typed options; the Channel constructs
   Adaptive Cards from those options rather than asking the model to generate card JSON.
2. The Channel calls the .NET 10 **Foundry hosted Agent** with its managed identity in
   `Authorization` and the user's assertion in `x-client-user-token`. The Agent independently
   validates signature, issuer, tenant, audience, lifetime and user identity before loading state.
   The header is confidential user authentication material, not a telemetry field.
3. Microsoft Agent Framework uses `gpt-4.1-mini` native function calling for routing.
   `get_statement` uses a **local, in-process resolver**, not a new HTTP service. It resolves
   Fabric-owned identifiers, expands an organization into distinct fact-key pairs, and executes
   fixed parameterized SQL. The same routing model may perform constrained candidate reranking;
   a separate reasoning-model deployment is not required.
4. Resolver discovery uses Azure AI Search keyword/vector hybrid retrieval plus semantic ranker,
   with a compatible embedding model. Search is a **rebuildable metadata projection**, not a
   finance database or authorization authority. It contains catalogue metadata, not finance facts.
5. Fabric SQL, Fabric Data Agent and KPIpedia in Copilot Studio run **as the signed-in user**.
   Azure RBAC, Fabric item/SQL authorization and Power Platform permissions are separate systems.
   No managed identity is granted app-only access to an end user's finance as a substitute.
6. **Search does not inherit Fabric RLS.** The Agent must validate candidate IDs and their
   metadata visibility through the caller's delegated Fabric permissions **before** returning
   labels/paths/definitions or sending candidates to a reranking model. It must recheck catalogue
   the full release binding, executable KPI and organization scope before execution and after
   clarification. The binding includes catalogue version, Search index, embedding deployment
   and embedding dimensions, not just a version string.
   RLS on fact tables alone does not establish metadata visibility.
7. Agent history/state is in the **Foundry platform state store** (`zavafinance-sessions`).
   The Channel's `agent-state` blob container holds delivery/session state and cached replies;
   it is not the Agent's platform state store. DTS holds orchestration identifiers. Treat all
   these stores as confidential because a channel reply or clarification can contain user data.

### Availability and support status

| Capability | Planning status |
|---|---|
| .NET 10, dedicated App Service, Storage, Entra, Azure AI Search hybrid/semantic features used here | GA service/runtime capabilities; regional SKU/quota and configuration still require validation |
| Foundry hosted runtime/code deployment and its pinned agent-server SDK contract | **Preview in this application's release contract**; record exact SDK, protocol and platform version and obtain explicit approval |
| Fabric Data Agent, its MCP integration and selected reasoning runtime | **Preview**; enable only for approved users/workspaces and approved geography |
| Foundry role display names | `Azure AI User` was renamed **Foundry User**; the role GUID is unchanged. This is a rename, not a reason to recreate assignments |
| System-provided hosted agent identity/network behavior | Evolving platform contract; inspect the deployed project/agent rather than assuming an old preview's identity model |

The current Agent pins `Azure.AI.AgentServer.Responses` **1.0.0-beta.8** (Preview);
the deployment uses the `azure.ai.agents` extension **>=1.0.0-beta.4**, `dotnet_10`
and Responses protocol **2.0.0**. Its stable dependencies include Microsoft Agent Framework
`Microsoft.Agents.AI` **1.21.0**, `Microsoft.Agents.AI.Foundry` **1.5.0**, Copilot Studio client
**1.8.77**, Azure AI Projects **2.0.1** and MCP Core **2.2.0**. The Channel stays on GA Agents SDK
**1.8.77**, Functions isolated worker **2.52.0** and Durable/DTS extensions **1.16.4/1.6.0**;
it does not adopt Preview durable-agent hosting packages. Project files are authoritative for
the full direct/transitive package graph and must accompany release/SBOM review.

Infrastructure pins Search AVM **0.13.0**. The inherited Bot ARM API is
`2023-09-15-preview`; that **API-version policy exception** is distinct from Azure Bot Service
availability. Search document queries use API `2024-07-01`; embeddings use `2024-10-21`.
Record these contracts with each release rather than treating an unpinned “latest” dependency
as automatically production-approved.

The current first-party hosted-agent documentation distinguishes a **dedicated agent runtime
identity** from the **project infrastructure managed identity**. Older projects/releases may use
a different runtime identity contract. Record both object IDs, verify which principal the
container actually uses, and assign Search/model access to that principal. Do not copy the
project ID into `agentPrincipalId` without checking. [Hosted permissions][hosted-permissions]

**Identity discovery and release check:** for this pinned hosted API, read
`GET <project-endpoint>/agents/<agent-name>?api-version=2025-11-15-preview` and inspect
`versions.latest.instance_identity.principal_id` when the latest version is the deployed target.
Record the version and runtime principal in the environment's restricted deployment inventory,
not in this shareable catalogue. For a pinned older version, inspect that version's instance
identity rather than assuming the latest record describes existing conversations.

The per-agent instance principal—not the account MI or project infrastructure MI—is the input
to `agentPrincipalId` for Search **Index Data Reader** and direct embedding **Cognitive Services
OpenAI User** assignments. The runtime's `ManagedIdentityId.SystemAssigned` credential selection
does not mean “the Foundry account's system-assigned identity”. Keep project infrastructure
grants, such as image pull, separate; never grant all three identities to bypass uncertainty.
After each deployment/recreation, re-read the active runtime identity and verify continuity
and real data-plane access. If it changed, explicitly reconcile narrowly scoped assignments
and review removal of superseded grants after old-version retirement.

## 2. Component and dependency inventory

| Component / resource provider | Depends on / purpose | Provisioning owner and boundary |
|---|---|---|
| Resource groups, policy assignments, tags, locks | Approved subscription, regions, budget, quota, naming and retention | Azure platform; independently scoped resource groups per tier |
| Entra bot/OBO application and enterprise application | Single tenant, delegated scopes, admin consent, SSO exposure, confidential-client credential | Entra administrator; not created by ARM Bicep |
| Teams/M365 custom engine app package | Bot app ID, `webApplicationInfo`, trusted domains, branding/privacy/terms, app publication policies | Teams/M365 administrator; distinct package ID per tier |
| Azure Bot (`Microsoft.BotService`) + Teams channel + `mcs` OAuth connection | Entra application, publicly reachable authorized messaging endpoint, sign-in callback | Azure/Bot administrator; Bicep creates bot/channel, OAuth connection is separate |
| Channel Function App + dedicated plan (`Microsoft.Web`) | .NET isolated v4/.NET 10, UAMI, host Storage, VNet integration, DTS, Foundry endpoint | Azure application platform; `infra/main.bicep` |
| Channel UAMI (`Microsoft.ManagedIdentity`) | Host storage, state, DTS, Foundry invocation, telemetry | Azure identity owner; no delegated finance permissions assigned to this MI |
| Private Storage (`Microsoft.Storage`) | Blob host leases/state, table host diagnostics; private blob/queue/table endpoints | Azure platform; shared keys and public network disabled |
| VNet, subnets, endpoints, DNS (`Microsoft.Network`) | Non-overlapping IPs; delegated Function subnet; private endpoint subnet; DNS links/forwarders | Network team; hosted-agent subnet is a **separate** additional integration |
| DTS scheduler/task hub (`Microsoft.DurableTask`) | Channel MI RBAC and HTTPS/HTTP2 access to its actual scheduler endpoint | Azure platform; MI authentication, separate hub/scheduler per tier |
| Foundry account/project (`Microsoft.CognitiveServices`) | Approved hosted runtime, region, model quota, networking, identities and deployment roles | Foundry administrator; existing demo account/project retained |
| Hosted Agent version/session runtime | Code package or image, Responses protocol, environment configuration, OBO credential | Foundry **data plane** deployment; ARM resources alone do not create a working agent |
| Model deployments | `gpt-4.1-mini` routing/reranking; `text-embedding-3-large` at **1536 dimensions** for the current publisher contract | Foundry model administrator; deployment name/version/SKU and capacity approved separately |
| ACR / remote build infrastructure (`Microsoft.ContainerRegistry`) | If used by code/image deployment: image publisher + platform image pull, registry permission mode, reachable endpoints | Foundry/platform deployment team; actual generated or selected registry must be inventoried |
| Dedicated Azure AI Search (`Microsoft.Search`) | Semantic ranker, versioned index, runtime Reader, separate publisher roles, model access and network paths | Azure/Search administrator; independent `infra/search.bicep` |
| Log Analytics + Application Insights (`Microsoft.OperationalInsights`, `Microsoft.Insights`) | Access-controlled telemetry, diagnostics, workspace retention, alerts and budget | Operations; content-free runtime telemetry by default |
| Existing **Fabric F2** capacity | Active capacity, approved Fabric/AI tenant settings; supports the demo lakehouse/data agent | Fabric capacity administrator; **retain F2**, do not provision another database |
| Per-tier Fabric workspace, lakehouse and SQL analytics endpoint | Existing finance facts/dimensions/calculators plus additive resolver tables, SQL synchronization and user authorization | Fabric administrator/data engineering; not Azure SQL/Cosmos DB provisioning |
| Fabric Data Agent item + published MCP endpoint | Lakehouse item access, approved runtime, user sharing, KPI/formula instructions, publication | Fabric owner; publishing and testing are separate from item creation |
| KPIpedia Copilot Studio agent | Power Platform environment, authentication, end-user sharing, connectors/knowledge sources, published version | Power Platform/Copilot Studio owner; not Azure RBAC |
| Catalogue publisher workstation/automation | Fabric notebook/lakehouse write, delegated SQL read for supplied scripts, Search schema/data write, embedding inference | Data engineering; separate from all runtime principals |

No separate resolver Function, Azure SQL server, Cosmos DB account, cache, vector database,
APIM instance or extra Fabric capacity is required by this design. A Key Vault is optional
when required by customer credential policy, not an implicit dependency already provisioned.

## 3. Independent environment tiers

Use separate security boundaries, not deployment slots with shared state. A slot can support
a release within a tier but is **not** the dev/staging/prod separation mechanism.

| Configuration / boundary | Dev | Staging | Production |
|---|---|---|---|
| Subscription / RGs | Approved demo subscription/RGs | Non-production subscription/RGs | Production subscription/RGs; separate from non-production |
| Bot/OBO app registrations, enterprise app assignments, OAuth connection | Dev identities/consent only | Staging identities/consent only | Production identities/consent only |
| Teams/M365 application/package | Distinct dev app ID | Distinct staging app ID | Production app ID and governed publication |
| Function App / UAMI / plan | P1v3 × 1 starting point | P1v3 × 1; performance test configuration | P1v3 × 2 starting point; load/zone/failover design review |
| Storage/account/container | Separate account; `agent-state`; LRS | Separate account; same container name allowed; ZRS recommendation | Separate account; ZRS recommendation and approved restore/retention controls |
| Session key salt | Secure random dev value | Different secure random value | Different secure random value; controlled rotation |
| DTS | Separate scheduler; `zavafinancedev` | Separate scheduler; `zavafinancestaging` | Separate scheduler; `zavafinanceprod`; verify Consumption vs Dedicated needs |
| Foundry account/project, Agent, state store boundary | Existing demo can remain; `zavafinance-dev` for a new tier | Independent project/account/deployments; `zavafinance-staging` | Independent project/account/deployments; `zavafinance-prod` |
| Routing/reranking | Tier-local `gpt-4.1-mini` deployment | Same approved version/settings for acceptance | Same approved version; quota/capacity pinned and monitored |
| Embeddings | Tier-local `text-embedding-3-large`, 1536 dimensions | Same model/version/dimensions | Same model/version/dimensions; reindex for any embedding contract change |
| Search | Dedicated Standard S1, 1 replica/1 partition recommendation | Dedicated S1, 2 replicas/1 partition recommendation | Dedicated S1, 3 replicas/1 partition recommendation |
| Search network | Explicit approved Public or Private decision | Private recommended **only with a working hosted egress path** | Private recommended; otherwise approved exception or deployment blocked |
| Search index / release | `zava-resolver-<version>` on the dedicated dev service; current target `zava-resolver-2026-09-16-v1` | `zava-resolver-staging-<version>` | `zava-resolver-prod-<version>`; promote a validated version, not a mutable dev index |
| Fabric | Existing F2; dev workspace/lakehouse/catalogue | Distinct workspace/lakehouse/catalogue and user groups | Distinct workspace/lakehouse/catalogue and least-privilege groups |
| Fabric capacity | Keep current F2 | May share F2 only if isolation/availability/capacity contention are acceptable | Capacity isolation is a policy/sizing decision, **not** a mandatory additional DB or automatic F2 upgrade |
| Copilot Studio | Dev Power Platform environment and agent | Staging environment, connectors and published agent | Governed production environment, DLP and published agent |
| Diagnostics / logs | 30-day workspace recommendation | 30-day recommendation | 90-day recommendation, subject to policy/legal requirements |
| Agent CPU/RAM | Current starting point: 1 vCPU / 2 GiB | Same initial setting; measure concurrency/memory | Measured setting + approved replica/session limits; no unverified HA claim |
| Network address examples | `10.10.0.0/16` | `10.20.0.0/16` | `10.30.0.0/16`; all require IPAM approval |

Separate workspaces on a shared Fabric capacity are logical security boundaries, not dedicated
compute. Likewise, replicas do not create multi-region disaster recovery. Search's documented
query versus indexing SLA replica requirements and regional zone support must be reviewed;
the tiny current catalogue is not a throughput sizing exercise. [Search reliability][search-reliability]

The existing demonstration names can differ from new-tier defaults. Never apply a new-tier
parameter file over the demo without reconciling resource names, task hub, app identity and salt.
The same tier's Channel and Agent must use the agreed session salt; **never reuse it across tiers**.
The resolver demo uses an explicitly approved **Basic 1-replica/1-partition** Search service.
The Standard S1 values above remain new-tier recommendations; they are not the demo's
deployed configuration or an instruction to upgrade it.

## 4. Azure RBAC catalogue

Assignments below are resource-scoped and use **object/principal IDs**, not application/client
IDs. No subscription-wide runtime roles are needed. Role names/GUIDs were verified in
Microsoft's published references linked below. Propagation can take time; test with the
intended identity, not an administrator's cached token.

| Principal → target scope | Exact role and GUID | Why / automation |
|---|---|---|
| Channel UAMI → its host Storage account | **Storage Blob Data Owner** `b7e6dc6d-f1e8-4753-8033-0f276bb0955b` | Functions host-required storage; covers the colocated `agent-state` blobs. `main.bicep` |
| Channel UAMI → its host Storage account | **Storage Table Data Contributor** `0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3` | Functions host diagnostic events; not a finance table. `main.bicep` |
| Channel UAMI → a separately provisioned state-only blob container, **if split later** | **Storage Blob Data Contributor** `ba92f5b4-2d11-453d-a403-e96b0029c9fe` | Sufficient for channel state alone; account-level Blob Owner is needed for the host, not a general state-reader role |
| Channel UAMI → Storage account, **only if queue/blob triggers or Storage durable backend are added** | **Storage Queue Data Contributor** `974c5e8b-45b9-4653-ba55-5f855dd0fb88` | **Not required by current DTS backend.** `enableQueueStorageRole=false` by default |
| Channel UAMI → DTS scheduler | **Durable Task Data Contributor** `0ad04412-c4d5-4796-b79c-f76d14c8d402` | Schedule/query/manage its durable work. `main.bicep`; own scheduler per tier |
| Explicit DTS operator → scheduler | Same **Durable Task Data Contributor** GUID | Optional privileged operational/dashboard access, **not read-only**; no default human grant |
| Channel UAMI → dedicated Foundry project | **Foundry User** (formerly Azure AI User) `53ca6127-db72-4b80-b1b0-d745d6d5456d` | Existing client creates **project-level conversations** and calls project Responses APIs. `foundry-access.bicep` keeps the compatible project scope; broader than endpoint-only invocation |
| Endpoint-only consumer → Foundry project or individual agent | **Foundry Agent Consumer** `eed3b665-ab3a-47b6-8f48-c9382fb1dad6` | Preferred narrower role for a **validated dedicated-agent endpoint client**; do not substitute blindly for the existing project-conversation path |
| Actual hosted runtime identity → dedicated Search service | **Search Index Data Reader** `1407120a-92aa-4202-b7e9-c0e197c71c8f` | Query only; no schema/document writes. `search.bicep` |
| Separate catalogue publisher → dedicated Search service | **Search Index Data Contributor** `8ebe5a00-799e-43f5-93ac-243d3dce84a7` | Publish/verify document contents. `search.bicep`; treat as privileged metadata access |
| Catalogue index administrator/publisher → dedicated Search service | **Search Service Contributor** `7ca78c08-252a-4471-8644-bb5ff32d4ba0` | Current publisher also creates versioned schemas. Disable `grantPublisherSchemaManagement` for a document-only publisher after separating schema administration |
| Hosted runtime identity and catalogue publisher → embedding model account | **Cognitive Services OpenAI User** `5e0bd9bd-7b93-4f28-af87-19fc36ad61bd` | Direct account-level embedding inference. `model-access.bicep`; no model deployment-write role |
| Foundry project infrastructure MI → own Foundry account | **Foundry User** `53ca6127-db72-4b80-b1b0-d745d6d5456d` | Platform/project infrastructure dependency described by Foundry. Optional explicit reconciliation in `foundry-access.bicep` |
| Dedicated hosted-agent identity → its own project inference + state | **Implicit platform grant on current dedicated-agent runtime** | No extra role in the standard new-runtime case. For older/advanced deployments without implicit access, verify and grant Foundry User at **project** scope or a reviewed narrower custom role |
| Foundry project image-pull MI → its ACR | **Container Registry Repository Reader** `b93aa761-3e63-49ed-ac28-beffa264f7ac` (ABAC registry) **or AcrPull** `7f951dda-4ed3-4680-a7ca-43fe172d538d` (legacy RBAC registry) | Match actual registry permission mode; `registry-access.bicep`. No runtime push role |
| Image build/publish principal → its ACR | **Container Registry Repository Writer** `2a1e307c-b015-4ebd-883e-5b7698a07328` (ABAC) **or AcrPush** `8311e382-0749-4cb8-b61a-304f252e45ec` (legacy) | Separate optional CI grant in `registry-access.bicep`; not the finance-user delegation |
| Channel UAMI → its Application Insights component | **Monitoring Metrics Publisher** `3913510d-42f4-4e42-8a64-420c390055eb` | Entra ingestion; host authentication setting included in `main.bicep`. Verify worker and host telemetry before disabling local ingestion for the component |

**Least-privilege qualifications:**

- Search schema resources are managed through Search's service API. The supplied publisher needs
  the service-level schema role as well as the data role; generic ARM `Contributor` is not a
  substitute for document authorization. A dedicated service limits the scope across immutable
  versioned indexes. A shared Search service would require a separately reviewed index-scope
  assignment strategy and is not this template's default.
- `Search Service Contributor` can manage security-related service configuration, so the
  publisher is deliberately distinct from runtime. API keys are disabled in the resource;
  policy should prevent re-enabling local authentication.
- `foundry-access.bicep` exposes `channelUsesProjectApi`. Keep `true` for this client. To reduce
  privilege further, first validate current API operations and a custom role or dedicated
  endpoint/Consumer migration; do not grant account/subscription Owner to resolve a 403.
- This app does **not** send Foundry's `x-ms-user-identity` header. Do not add the separate
  `UserIdentityImpersonation` data action merely because it forwards `x-client-user-token`.
- Existing role assignments are not deleted by incremental deployment when a template flag is
  disabled. Review/revoke obsolete queue grants, broad demo grants or replaced roles explicitly
  through approved access changes. Do not create a second identical assignment under a new GUID.
- `model-access.bicep` defaults `assignPublisherAccess=true`. Set it to `false` after verifying an
  existing publisher **Cognitive Services OpenAI User** grant to avoid duplicate-assignment errors
  while adding runtime access. Its publisher assignment output is then empty; the existing grant
  is left unchanged, and `publisherPrincipalId` remains a required template input.
- Optional platform evaluation/Insights integrations can require additional telemetry-reader
  grants; these are not required to call finance tools. Add only the role requested by the
  selected, approved integration at the actual workspace scope.

References: [Storage and host roles][functions-mi], [Storage GUIDs][storage-roles],
[Search/model GUIDs][ai-roles], [DTS GUID][integration-roles], [registry GUIDs][container-roles],
[monitor GUIDs][monitor-roles], [Foundry authorization and rename][foundry-rbac].

### Provisioning permissions are not runtime permissions

The deployment principal needs the relevant ARM resource write operations in the target RG,
`Microsoft.Resources/deployments/*`, and role-assignment write permissions on the resources
where assignments are created. **Role Based Access Control Administrator**
`f58310d9-a9f6-439a-9e8d-f62e7b41a168` is the built-in role for the latter; constrain its scope and,
where practical, assignment conditions. [Role definition][rbac-administrator].
Shared subnet/Private DNS linkage needs explicit access
to those network resources. Creating Foundry agents needs Foundry data-plane permissions;
ARM Contributor alone does not provide them. Entra consent, Fabric capacity/workspace rights
and Power Platform environment administration are **not** obtained through Azure RG ownership.

## 5. Entra, Teams and confidential-client configuration

### Per-tier registration checklist

The baseline uses the same single-tenant application ID for the Azure Bot and OBO confidential
client within a tier. Separate application registrations are possible only with an explicitly
validated token-exchange/audience design; do not mix a bot registration from one tier with
another tier's OBO app.

1. Create the app registration and enterprise application in the intended tenant.
   Record **tenant ID, application/client ID and service-principal object ID separately**.
   Assign environment-specific test/production user groups; keep registration owners controlled.
2. Expose the application ID URI `api://botid-<bot-app-id>` to match the packaged
   `webApplicationInfo.resource`. Define the delegated SSO scope (normally `access_as_user`)
   with approved consent text and admin-only consent where required. Record its **scope GUID**.
   Set the access-token version required by Teams SSO (v2).
3. Preauthorize only the documented trusted Teams/M365 client applications for that scope.
   Use the application/client IDs below in `api.preAuthorizedApplications[].appId`, with the
   exposed SSO scope GUID in `delegatedPermissionIds`. Confirm the current M365 client
   requirements before publication; never preauthorize an arbitrary test client or broadly
   enable all clients.
4. Add the **Web** redirect URI `https://token.botframework.com/.auth/web/redirect`.
   Do not use wildcard callbacks, SPA/public-client configuration or implicit flow as a
   replacement for the server-side OBO flow.
5. Configure an Azure Bot OAuth connection named **`mcs`**, Microsoft Entra ID v2 provider:
   the tier's tenant/client ID and confidential credential, the exposed API as the token-exchange
   resource, and the scopes required to acquire that API's delegated assertion. The resulting
   assertion audience must match `Obo__Audience`; verify an actual SSO token rather than
   assuming an identifier URI and v2 token `aud` are interchangeable.
6. Add the downstream **delegated** API permissions below and grant tenant admin consent.
   `.default` uses already-consented scopes; it does not create consent. Keep proof of the
   consent grant, app assignments and a signed-in OBO test for **each** audience.
7. Publish the Teams/M365 package with the correct bot ID and app URI. Preserve `personal`
   and explicit `copilot` scopes, `copilotAgents.customEngineAgents`, and trusted domains.
   Replace demo `example.com` privacy/terms/developer URLs with customer-approved endpoints
   before customer distribution. Use a distinct package/app ID per tier and bump package
   version on changes.

| Trusted client application | First-party application/client ID |
|---|---|
| Teams mobile/desktop | `1fec8e78-bce4-4aaf-ab1b-5451cc387264` |
| Teams web | `5e3ce6c0-2b1f-4285-8d4b-75ee78787346` |
| Microsoft 365 web | `4765445b-32c6-49b0-83e6-1d93765276ca` |
| Microsoft 365 desktop | `0ec893e0-5785-4de6-99da-4ed124e5296c` |
| Microsoft 365 mobile / Outlook desktop | `d3590ed6-52b3-4102-aeff-aad2292ab01c` |

Source: [Teams/M365 SSO registration][teams-sso]. Authorize only the clients in the approved
distribution scope; extra Outlook/Edge clients listed by Microsoft are not required merely
because the app supports Teams and M365. These are trusted **client application IDs**, not
tenant service-principal object IDs or the ZavaFinance app ID.

The baseline defines **no custom Entra application role** and consumes no `roles` claim to
grant finance access. Keep `appRoles` empty unless the customer approves and implements an
additional application-authorization contract. Enterprise-app user/group assignment can
gate sign-in but does not replace Fabric item/SQL permissions. Do not add Graph/Fabric
application permissions as a substitute for the delegated permissions below.

| Downstream API | Delegated permission in the current baseline | Token audience / checks |
|---|---|---|
| Microsoft Graph | `User.Read` | Sign-in/profile baseline; do not add broad Graph application permissions |
| Copilot Studio | `CopilotStudio.Copilots.Invoke` | Scope derived by `CopilotClient.ScopeFromSettings` from environment/schema/cloud; do **not** hand-build the scope |
| Azure SQL Database resource (Fabric SQL analytics endpoint) | `user_impersonation` | `https://database.windows.net/.default`; SQL principal/RLS still decides rows |
| Power BI Service / Fabric published Data Agent | `Item.Execute.All` | `https://api.fabric.microsoft.com/.default`; published Data Agent and underlying item authorization still required |

These permission display names refer to the API's **delegated** scopes, not Azure roles.
Discover the service principal and scope IDs in the actual tenant; do not reuse another tenant's
service-principal object IDs. API scope consent does not grant lakehouse/table access.

### Secrets, certificates and managed identity

- `sessionKeySalt` is an ARM **secure** parameter; provide at least 32 cryptographically random
  bytes encoded for configuration, unique per tier. Channel and Agent configuration must agree.
  Rotating it changes all session keys and orphans old sessions: approve a reset/retention plan.
- Current channel `ServiceConnection` and `MCSConnection` settings use `AuthType=ClientSecret`;
  `botClientSecret` is an ARM secure parameter. The Agent supports `Obo__ClientSecret`.
  Inject values from an approved vault/secret store into environment inputs, not source,
  checked-in JSON, command history, build logs, ARM outputs or the catalogue.
- **MI alone does not replace the confidential-client credential in user OBO.** It authenticates
  a workload, not a finance user. A federation assertion can replace a stored client secret
  only after the app registration explicitly trusts the supported workload identity.
- The Agent's `ConfidentialClientFactory` uses the secret when set; otherwise it requests
  `api://AzureADTokenExchange/.default` using the configured MI. A managed-identity FIC uses
  issuer `https://login.microsoftonline.com/<tenant-id>/v2.0`, subject = **MI object ID**,
  audience `api://AzureADTokenExchange`. Microsoft's documented app-trust pattern requires a
  supported **user-assigned** managed identity in the same tenant. Prove that the selected
  hosted runtime exposes that identity/token capability before removing the secret; a project
  identity or new dedicated agent identity is not automatically an interchangeable UAMI.
- If customer policy mandates certificate-based OBO, record a **code/configuration work item**:
  the current Agent factory does not load a certificate. A certificate file or password is
  not a supported hidden setting. Supply certificates via approved secure delivery only
  after implementing/validating certificate authentication; do not put PFX files in the repo.
- Key Vault references can be used for Function settings only with the matching identity's
  vault access and network path. Foundry hosted environment values are **not automatically
  App Service Key Vault references**. No Key Vault or certificate loader is created here.

[OBO protocol][obo] · [Managed identity federation restrictions][fic]

## 6. Fabric and Power Platform authorization

### Fabric — keep the existing lakehouse and F2

The baseline requires an active **paid F2 or higher** capacity and the relevant Fabric/Data Agent
tenant switches. Keep the existing F2 for the demo. Sharing that capacity across tier workspaces
is allowed only after operational/isolation approval. Provision **additional workspaces/items**,
not a second finance database service.

- End users need the appropriate Fabric workspace/item sharing and SQL access, plus published
  Data Agent access. Prefer item-level sharing and constrained SQL grants/RLS; do not grant
  Contributor/Member to every user just to make queries succeed.
- The data owner must define permissions for existing finance facts/dimensions and for
  `resolver_catalog`, `resolver_scope`, `resolver_calendar` and `resolver_release`.
  Catalogue paths/aliases/definitions can be confidential even when no figures are present.
  Ensure delegated catalogue queries expose **only authorized metadata**; authorization must
  constrain relevant scope mappings as well as facts. A broad `SELECT` on every resolver row
  is not an implementation of per-user metadata security.
- The runtime uses delegated user SQL and delegated Data Agent MCP tokens. Runtime Search/Foundry
  MI roles neither grant nor bypass Fabric permissions. Test with two genuinely different users,
  including denied regions/KPIs/metadata, not only an administrator.
- Data engineering publishers need create/run notebook and lakehouse write permissions in the
  target workspace. The supplied PowerShell scripts also acquire a delegated SQL token for
  verification/publication; they are not an unattended app-only finance pipeline.
  Any service-principal automation must separately prove support for its chosen Fabric APIs and
  approved data-engineering grants. Never reuse that identity to answer end-user finance questions.
- Fabric SQL synchronization is asynchronous. Creating Delta tables is insufficient; wait until
  SQL exposes the version and validate references/counts before Search publication.
- Stage immutable catalogue versions, publish/verify a versioned Search index, then activate
  the release. Never activate a version with an unverified or inaccessible index. Keep the
  previous version for rollback and invalidate stale clarifications.
- Review Data Agent instructions, KPI definitions/ratios, permitted lakehouse items, sharing,
  published status, MCP endpoint and selected runtime. A successful MCP call is not proof of
  numerical correctness. Approve AI cross-geo processing/storage settings instead of enabling
  them silently to bypass a regional block.

Detailed ordered commands and invariants: [resolver publication runbook](resolver-data.md).
First-party prerequisites: [Fabric Data Agent tutorial][fabric-agent],
[Fabric Data Agent tenant settings][fabric-settings].

### Power Platform / Copilot Studio — KPIpedia

Create/select a separate approved environment per tier, with its security group, region,
licensing/capacity, Dataverse/connector prerequisites and DLP classification. Publish the
authenticated KPIpedia agent, record **EnvironmentId** and **SchemaName**, share it with the
intended users and verify the downstream delegated invocation permission.

Review the exact knowledge-source scope, connector authentication, connection references,
environment variables and solution promotion strategy. A source's configured file/path does
not automatically prove answers cannot cite other content; test negative access cases. Do not
switch to unauthenticated/public access or shared owner credentials to avoid delegated 401s.
Azure subscription Contributor and Search RBAC do not grant any of these permissions.

## 7. Configuration contract

Use double underscores for environment variables; `:` denotes the equivalent .NET configuration
path. Every ID/endpoint below is **tier-specific**, even when its display name is the same.
Do not copy `.azure` values from a workstation into customer documents.

### Channel settings

| Exact setting | Required value / default / owner |
|---|---|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `FUNCTIONS_EXTENSION_VERSION` | `~4`; .NET 10 isolated hosting stack |
| `AzureWebJobsStorage__accountName` | Tier's private host Storage name |
| `AzureWebJobsStorage__credential` | `managedidentity` |
| `AzureWebJobsStorage__clientId` | Channel UAMI **client ID** |
| `AZURE_CLIENT_ID` | Same Channel UAMI client ID for Azure SDK credential selection |
| `WEBSITE_RUN_FROM_PACKAGE` | `1`; dedicated plan zip deployment, not a Storage account key |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Tier component ingestion coordinates; not an OBO credential |
| `APPLICATIONINSIGHTS_AUTHENTICATION_STRING` | `ClientId=<channel-uami-client-id>;Authorization=AAD`; verify both host and worker export |
| `DurableTask__ConnectionString` | `Endpoint=<actual-scheduler-endpoint>;Authentication=ManagedIdentity;ClientID=<uami-client-id>;TaskHub=<tier-hub>` |
| `DURABLE_TASK_SCHEDULER_CONNECTION_STRING` | Same MI connection descriptor for the Functions durable extension |
| `TASKHUB_NAME` | Same hub as both connection descriptors and `host.json` |
| `State__ContainerUri` | Full `https://<storage>.blob.core.windows.net/agent-state` URI; accessible through private DNS |
| `HostedAgent__ResponsesEndpoint` | Full **project** Responses endpoint including required API version; obtained from the actual project |
| `HostedAgent__ConversationsEndpoint` | Optional override; by default replaces `/responses` with `/conversations`; confirm both URLs belong to the same project |
| `HostedAgent__AgentName` | Deployed Agent name, default `zavafinance`; new tiers use explicit suffixes |
| `HostedAgent__UserTokenHeader` | `x-client-user-token`; must equal Agent setting |
| `HostedAgent__Timeout` | `00:05:00` default; coordinate with downstream timeouts and delivery behavior |
| `TokenValidation__Audiences__0` | Bot application ID; additional values only by explicit security review |
| `TokenValidation__TenantId` | Intended tenant ID |
| `TokenValidation__ValidIssuers__0`, subsequent numeric indexes | Optional override of trusted issuer list; normally leave unset |
| `TokenValidation__AzureBotServiceOpenIdMetadataUrl` | Optional; SDK's trusted Bot Service metadata default |
| `TokenValidation__OpenIdMetadataUrl` | Optional; trusted Entra metadata default |
| `TokenValidation__AzureBotServiceTokenHandling` | `true` default; do not disable Bot JWT handling to resolve auth errors |
| `TokenValidation__OpenIdMetadataRefresh` | Optional TimeSpan; default Microsoft identity-library automatic refresh |
| `Orchestrator__SessionKeySalt` | Required **secret**, tier-local and coordinated with Agent |
| `Orchestrator__UserAuthorizationHandler` | `mcs` default; matches OAuth and authorization handler |
| `Orchestrator__AcknowledgementText` | `Working on that…` default; deliberately tool-neutral |
| `Connections__ServiceConnection__Settings__AuthType` | `ClientSecret` baseline |
| `Connections__ServiceConnection__Settings__ClientId` | Bot app client ID |
| `Connections__ServiceConnection__Settings__ClientSecret` | Required **secret** in the baseline; secure injection |
| `Connections__ServiceConnection__Settings__AuthorityEndpoint` | `https://login.microsoftonline.com/<tenant-id>` |
| `Connections__ServiceConnection__Settings__Scopes__0` | `https://api.botframework.com/.default` |
| `Connections__MCSConnection__Settings__AuthType` | `ClientSecret` baseline |
| `Connections__MCSConnection__Settings__ClientId` | Same baseline bot/OBO app ID |
| `Connections__MCSConnection__Settings__ClientSecret` | Baseline app **secret**, independently rotated/configured through secret management |
| `Connections__MCSConnection__Settings__AuthorityEndpoint` | Same intended tenant authority |
| `AgentApplication__StartTypingTimer` | `false` in committed Channel settings; delivery uses explicit progress behavior |
| `AgentApplication__RemoveRecipientMention`, `AgentApplication__NormalizeMentions` | `true` in committed settings |
| `AgentApplication__UserAuthorization__AutoSignIn` | `true` |
| `AgentApplication__UserAuthorization__DefaultHandlerName` | `mcs` |
| `AgentApplication__UserAuthorization__Handlers__mcs__Settings__AzureBotOAuthConnectionName` | `mcs` |
| `AgentApplication__UserAuthorization__Handlers__mcs__Settings__OBOConnectionName` | `MCSConnection` |
| `ConnectionsMap__0__ServiceUrl`, `ConnectionsMap__0__Connection` | `*`, `ServiceConnection`; SDK mapping after inbound JWT validation, not permission for unauthenticated inbound messages |

The old `Orchestrator__LogSubagentText` setting on the Channel is not a Channel option:
the Channel does not route or call finance subagents. The Agent's setting below controls it.

Clarification delivery introduces **no additional Channel environment settings**.
Shared Contracts fixes `x-client-reply-format: zava-finance-v1` for typed reply negotiation
(JSON envelope schema `zava-finance.v1`) and optional `x-client-clarification` for a base64-encoded
UTF-8 JSON submission. The submission is bounded to 1024 decoded bytes and must still pass
pending-state, user/conversation, catalogue-version and delegated authorization checks.
Base64 is encoding, not encryption or authorization. Preserve these `x-client-*` headers on
the Channel-to-Foundry path; do not log submission payloads or the user-token header.
`Orchestrator__UserAuthorizationHandler` governs both message and invoke SSO paths.

### Clarification presentation responsibility

The Agent result retains **numbered clarification text plus typed clarification options**.
The Channel service builds the Adaptive Card attachment (`application/vnd.microsoft.card.adaptive`)
from those options. Card layout belongs to the Channel; the model does not produce arbitrary
card JSON. Numbering remains part of the result rather than being removed for card rendering.

Whether a choice arrives through a card submission or supported numbered-text handling,
the server must validate the user/conversation, pending request, selected choice, expiry,
release binding and current delegated SQL visibility before execution. A button click or
ordinal alone grants no authority. Plain direct Responses callers may retain text compatibility;
typed reply negotiation supplies the Channel with the structured options needed to construct cards.

### Hosted Agent baseline settings

| Exact setting | Required value / default / owner |
|---|---|
| `FOUNDRY_PROJECT_ENDPOINT` | **Platform-injected** in hosted runtime. Set explicitly only when running outside it; do not declare reserved `FOUNDRY_*`/`AGENT_*` variables in hosted deployment |
| `ModelDeployment` | `gpt-4.1-mini` default; neutral deployed env name. Existing azd input: `AZURE_AI_MODEL_DEPLOYMENT_NAME` |
| `Foundry__ProjectEndpoint`, `Foundry__ModelDeployment` | Local/test configuration aliases only; **not** hosted environment declarations |
| `Orchestrator__SessionKeySalt` | Required tier-local **secret**; existing azd input `ORCHESTRATOR_SESSION_KEY_SALT` |
| `Orchestrator__SessionTimeToLive` | `30.00:00:00` default; sliding session retention. Not a universal deletion policy for other stores |
| `Orchestrator__SubagentTimeout` | `00:03:00` default for Copilot Studio |
| `Orchestrator__MaxHistoryMessages` | `20` default; minimum `3`; whole-turn trimming |
| `Orchestrator__LogSubagentText` | `false`; enabling requires content logging approval |
| `Obo__ClientId` / `Obo__TenantId` / `Obo__Audience` | Required; azd inputs `OBO_CLIENT_ID`, `OBO_TENANT_ID`, `OBO_AUDIENCE`; audience of the actual forwarded assertion |
| `Obo__ClientSecret` | Supported confidential-client **secret**; azd input `OBO_CLIENT_SECRET`. Omit only after federation is proven |
| `Obo__ManagedIdentityClientId` | Optional **client ID**, only for a supported attached UAMI federation path; not a principal/object ID |
| `Obo__UserTokenHeader` | `x-client-user-token` default |
| `CopilotStudioAgent__EnvironmentId` | Required for KPIpedia; azd input `COPILOT_STUDIO_ENVIRONMENT_ID` |
| `CopilotStudioAgent__SchemaName` | Published agent schema name; azd input `COPILOT_STUDIO_SCHEMA_NAME` |
| `Fabric__SqlEndpoint` | Host only, e.g. `<endpoint>.datawarehouse.fabric.microsoft.com`; azd input `FABRIC_SQL_ENDPOINT` |
| `Fabric__Database` | Exact lakehouse SQL database/display name; azd input `FABRIC_DATABASE` |
| `Fabric__WorkspaceId` | Tier workspace GUID; azd input `FABRIC_WORKSPACE_ID` |
| `Fabric__DataAgentId` | Published item GUID; azd input `FABRIC_DATA_AGENT_ID` |
| `Fabric__SqlTimeout` | `00:00:20` default |
| `Fabric__DataAgentTimeout` | `00:05:00` default |
| `Fabric__DataAgentApiVersion` | `2024-05-01-preview` legacy OpenAI-compatible option; current MCP endpoint does not consume this value |

The built-in store name `zavafinance-sessions` is not an environment setting. Isolation requires
a separate Foundry project/agent state boundary and salt, not only a different name in a document.
Azure SDK workload token scopes are `https://ai.azure.com/.default` (Foundry),
`https://search.azure.com/.default` (Search), and `https://cognitiveservices.azure.com/.default`
(account-level OpenAI). None are delegated finance-user permissions.

### Local resolver settings

These names match `ResolverOptions` and are set on the **Agent only**, not the Channel.
No API key, finance service-principal token, separate resolver service or separate reranking
model setting is supported.

| Exact setting | Required value / default |
|---|---|
| `Resolver__SearchEndpoint` | `https://<tier-search>.search.windows.net` from `search.bicep` output |
| `Resolver__SemanticConfigurationName` | `resolver-semantic`; matches the publisher's index schema |
| `Resolver__EmbeddingEndpoint` | HTTPS account-level OpenAI endpoint used for indexing and runtime query embeddings |
| `Resolver__Timeout` | `00:00:20` default for retrieval/reranking; greater than zero and at most two minutes |
| `Resolver__ClarificationTimeToLive` | `00:15:00` default; greater than zero and at most one day; expired choices require a new request |
| `Resolver__MaxCandidates` | `8` default, supported range 1–25; bounded choices, not a permission filter |

Both endpoint strings are needed for `IsSearchConfigured`. Configuring only one is rejected
at startup. Leaving both unset disables Search retrieval and is **not** a fully configured
hybrid-search release. Exact catalogue resolution still requires the Fabric tables and an
active valid release. Runtime reads only IDs/version from Search before delegated catalogue
validation. The routing `ModelDeployment` is also the constrained reranker; Responses output
storage is disabled.

### Atomic Fabric release binding

`dbo.resolver_release` has **exactly one active row**, read using the current caller's delegated
SQL token. Its four non-null fields are one authoritative binding:

| SQL field | Type / contract |
|---|---|
| `catalog_version` | String, at most 64 characters; starts alphanumeric, then alphanumeric/dot/hyphen; identifies staged immutable catalogue/scope rows |
| `search_index` | String, 3–128 lowercase alphanumeric/hyphen characters, starting/ending alphanumeric; points to the verified version-specific index |
| `embedding_deployment` | String, at most 64 characters; starts alphanumeric, then alphanumeric/dot/underscore/hyphen; exact model deployment used to publish that index |
| `embedding_dimensions` | Integer, 1–3072; **1536** for this release and exactly equal to the index vector dimensions |

There are **no runtime index, embedding-deployment or dimensions environment overrides**.
The removed `Resolver__SearchIndexName`, `Resolver__EmbeddingDeploymentName` and
`Resolver__EmbeddingDimensions` settings must not be carried forward as an independent alias.
Runtime selects the index, catalogue filter and query-embedding contract from the same release
and rechecks the binding before finance execution. Missing, null, duplicate or invalid release
rows fail closed; a changed binding invalidates the request/clarification rather than silently
switching scope.

Stage catalogue/scope rows first, publish and probe the matching immutable Search index, then
replace the release row with all four fields together using the approved publication notebook.
Keep the previous active row on any Search failure and wait for Fabric SQL synchronization
after activation. Search endpoint and embedding **account** endpoint remain deployment settings;
changing either still requires matching network, RBAC and release/index verification.

### Search projection schema

The publisher's exact snake_case fields below are the runtime contract; do not rename them
to .NET property names or index finance facts. `scripts/Publish-ResolverSearch.ps1` owns the
schema and data-plane publication; the ARM module creates the dedicated service and RBAC only.

| Field | Search type / behavior |
|---|---|
| `entity_id` | `Edm.String`; document **key**, filterable; stable catalogue ID |
| `catalog_version` | `Edm.String`; filterable; equals the published release version |
| `entity_kind` | `Edm.String`; filterable; `org`, `kpi` or `kpi_group` |
| `canonical_name` | `Edm.String`; searchable; `en.microsoft` analyzer; semantic title |
| `aliases` | `Collection(Edm.String)`; searchable; `en.microsoft` analyzer; converted from Fabric `aliases_json` |
| `definition` | `Edm.String`; searchable; `en.microsoft` analyzer; semantic content |
| `hierarchy_path` | `Edm.String`; searchable; semantic content |
| `parent_id` | `Edm.String`; filterable; hierarchy link |
| `kpi_code` | `Edm.String`; filterable; executable KPI mapping where applicable |
| `region_code` | `Edm.String`; filterable; organization metadata, not user authorization |
| `department_code` | `Edm.String`; filterable; organization metadata |
| `department_group` | `Edm.String`; filterable; reporting-group metadata |
| `is_reportable` | `Edm.Boolean`; filterable; catalogue flag, not a substitute for fixed calculator validation |
| `content_vector` | `Collection(Edm.Single)`; searchable, **not retrievable**; 1536 dimensions for this release |

Semantic configuration: **`resolver-semantic`**; title `canonical_name`, content
`definition` + `hierarchy_path`, keywords `aliases`. Vector profile: **`resolver-vector`**,
backed by **`resolver-hnsw`**, HNSW cosine. Runtime keyword searches use
`canonical_name,aliases,definition,hierarchy_path`, vector queries use `content_vector`,
and retrieval selects only `entity_id,catalog_version` before delegated authorization.

The demo release projects **114 entities** (90 organization nodes, 18 executable KPIs,
six non-executable families), with **336** Fabric scope mappings and **one** calendar row.
Those are version-specific acceptance counts, not fixed limits for every customer catalogue.
During first-version staging, `resolver_release` remains empty until explicit activation;
that inactive state must not be reported as a working finance release. Current demo deployment
evidence is recorded separately in the [handover](../HANDOVER.md).

`infra/environments/{dev,staging,prod}.agent.env.example` documents the complete baseline and
resolver environment contract. It is an input template, not an automatically loaded `.env`
file and not a replacement for the existing `azure.yaml` deployment binding. Resolve its
references through the approved deployment/secret mechanism, never commit rendered secrets,
and never declare the platform-reserved `FOUNDRY_*` or `AGENT_*` namespaces.

### ARM parameter and secret-input contract

`infra/environments/{dev,staging,prod}.channel.bicepparam` and the matching `.search.bicepparam`
read environment-specific inputs. Replace `<TIER>` with `DEV`, `STAGING` or `PROD`.
All referenced values must be available in the provisioning process; there are no committed
secret defaults.

| Input | Meaning |
|---|---|
| `ZAVA_<TIER>_LOCATION` | Approved Channel/DTS region |
| `ZAVA_<TIER>_TENANT_ID`, `_BOT_APP_ID`, `_RESPONSES_ENDPOINT` | Tier's Entra and Foundry endpoints |
| `ZAVA_<TIER>_SESSION_KEY_SALT`, `_BOT_CLIENT_SECRET` | **Secure inputs**, not values to put into a shared environment example |
| `ZAVA_<TIER>_DTS_IP_ALLOWLIST` | JSON array of approved source CIDRs; `["0.0.0.0/0"]` is an explicit public-egress exception, `[]` denies all |
| `ZAVA_<TIER>_TAGS` | Optional JSON object for customer-required tags/owner; solution and tier preserved |
| `ZAVA_DEV_APP_NAME` | Optional override of new-dev default; not sufficient alone to reconcile an existing environment |
| `ZAVA_<TIER>_SEARCH_NAME`, `_SEARCH_LOCATION` | Globally unique dedicated Search name and approved semantic-capable region; dev defaults to `srch-zavafin-dev` / `swedencentral`; staging/prod require explicit inputs |
| `ZAVA_<TIER>_AGENT_PRINCIPAL_ID` | Actual hosted runtime object ID |
| `ZAVA_<TIER>_PUBLISHER_PRINCIPAL_ID`, `_PUBLISHER_PRINCIPAL_TYPE` | Different publisher object ID; type `ServicePrincipal` default, or approved `User`/`Group` |
| `ZAVA_<TIER>_SEARCH_NETWORK` | JSON `{ "mode":"Private", "privateEndpointSubnetId":"<ARM ID>", "searchPrivateDnsZoneId":"<ARM ID>" }` **or** `{ "mode":"Public", "allowedIpRanges":["<IPv4/CIDR>"] }`; empty public ranges explicitly mean all networks |
| `ZAVA_<TIER>_LOG_ANALYTICS_ID` | Existing tier workspace ARM ID; empty only with approved diagnostics exception |
| `ZAVA_<TIER>_OBO_AUDIENCE` | Actual audience of the Bot-forwarded user assertion |
| `ZAVA_<TIER>_COPILOT_STUDIO_ENVIRONMENT_ID`, `_COPILOT_STUDIO_SCHEMA_NAME` | Published authenticated tier KPIpedia coordinates |
| `ZAVA_<TIER>_FABRIC_SQL_ENDPOINT`, `_FABRIC_DATABASE`, `_FABRIC_WORKSPACE_ID`, `_FABRIC_DATA_AGENT_ID` | Approved tier Fabric SQL/lakehouse and published Data Agent coordinates |
| `ZAVA_<TIER>_SEARCH_ENDPOINT`, `_EMBEDDING_ENDPOINT` | Search ARM output and matching embedding model account endpoint for the Agent template; index/deployment/dimensions are read from `resolver_release`, not environment aliases |

`main.bicep` also accepts `environmentName`, `appServiceSku`, `appServiceInstanceCount`,
`storageSku`, `logRetentionInDays`, `taskHubName`, `schedulerIpAllowlist`,
`vnetAddressPrefix`, `appSubnetAddressPrefix`, `privateEndpointSubnetAddressPrefix`,
`additionalTags`, `enableQueueStorageRole`, and optional `operatorPrincipalId`.
The tier parameter files set recommendations; edit only after a recorded policy/capacity decision.

`search.bicep` accepts `sku`, `replicaCount`, `partitionCount`, `grantPublisherSchemaManagement`,
`enableQueryLogs` (default false), `logAnalyticsWorkspaceId`, and `additionalTags`. The explicit
network object is required; there is no implicit promise that a newly created private endpoint
is reachable from a hosted runtime. Index schema and documents are data-plane publication, not
ARM resource outputs.

## 8. DNS, ingress and egress approval

All hostnames below are **Azure public-cloud examples**, not a sovereign-cloud support claim.
Use actual generated endpoints and documented regional dependencies in firewall rules. DNS
must resolve through the runtime's resolver path; successful resolution on an admin laptop
is insufficient.

| Caller → destination | Protocol / purpose | Network and DNS requirement |
|---|---|---|
| Bot Service → `<channel>.azurewebsites.net/api/messages` | Inbound HTTPS 443, validated Bot JWT | Current topology needs a Bot-reachable public endpoint. A private-only app without an approved ingress solution is blocked |
| Health monitor → Channel `/health` | HTTPS 443 | Restrict operational visibility as required; readiness does not test every downstream dependency |
| Deployment runner → `<channel>.scm.azurewebsites.net` | HTTPS 443, zip/SCM deployment | SCM access is distinct from Bot ingress; approved admin/build network and credentials |
| Channel → actual Foundry project endpoint (`<account>.services.ai.azure.com`) | HTTPS 443, Responses + Conversations, MI | Public access or approved Foundry PE/DNS reachable from Channel VNet |
| Channel → `*.blob.core.windows.net`, `*.table.core.windows.net` and provisioned queue endpoint | HTTPS 443, host/state/diagnostics | Private IPs through `privatelink.blob.core.windows.net`, `privatelink.table.core.windows.net`, `privatelink.queue.core.windows.net`; queue PE exists for compatibility, not permission to use queues |
| Channel → actual `*.durabletask.io` scheduler endpoint | HTTPS 443 / HTTP2 gRPC | Preserve HTTP2 through proxies; source IP allowlist must match **real egress**, not inbound app IP |
| Channel/Agent → `login.microsoftonline.com` | HTTPS 443, OpenID metadata/signing keys/OBO | Entra egress/service tag; certificate/CRL and SDK dependencies per customer proxy policy |
| Channel → `login.botframework.com`, `token.botframework.com`, `api.botframework.com` and authenticated regional Bot Connector service URLs | HTTPS 443, JWT metadata, OAuth/token exchange, proactive replies | Approve documented Bot domains; do not rewrite trusted service URLs to arbitrary hosts |
| Agent → `<search>.search.windows.net` | HTTPS 443, MI hybrid/semantic query | Public approved sources or `privatelink.search.windows.net` PE + hosted VNet egress/DNS |
| Agent/publisher → `<model-account>.openai.azure.com` or selected `*.cognitiveservices.azure.com` endpoint | HTTPS 443, embeddings via Entra | Match configured embedding endpoint; `privatelink.openai.azure.com` / `privatelink.cognitiveservices.azure.com` as applicable |
| Agent → tier Foundry project endpoint | HTTPS 443, routing/reranking and platform state | Runtime identity and project private/public routing; platform-managed state endpoints must remain reachable |
| Agent/publisher → `<sql-endpoint>.datawarehouse.fabric.microsoft.com` | TCP 1433, encrypted TDS, delegated SQL | Fabric-specific approved connectivity. Azure Storage PE does **not** make Fabric SQL private |
| Agent → `api.fabric.microsoft.com/v1/mcp/workspaces/<workspace>/dataagents/<item>/agent` | HTTPS 443, streamable HTTP MCP, delegated user | Published endpoint, long-running calls and response streaming allowed through egress proxy |
| Publisher → `api.fabric.microsoft.com`, `onelake.dfs.fabric.microsoft.com` / `onelake.blob.fabric.microsoft.com` as used | HTTPS 443, notebook/item administration and lakehouse operations | Fabric tenant/workspace network controls and permitted publisher network |
| Agent → Copilot Studio/Power Platform regional service endpoints | HTTPS 443, delegated authenticated conversation | Derive from environment/schema and SDK cloud configuration; approve exact regional `*.environment.api.powerplatform.com` and documented connector/knowledge-source dependencies |
| Platform/build runner → selected `<registry>.azurecr.io` and regional registry data endpoints | HTTPS 443, image build/pull | Registry mode, DNS, trusted platform path; ACR PE uses `privatelink.azurecr.io` |
| Hosts/platform → region-specific Application Insights/Monitor ingestion and control endpoints | HTTPS 443, diagnostics | Validate actual connection-string endpoints, Azure Monitor service tags/AMPLS if selected |
| Runtime/publisher networks → approved DNS resolver | UDP/TCP 53 | Private DNS zones linked to all needed VNets or forwarded via Azure DNS Private Resolver; on-premises DNS requires the correct forwarding path |

**Hosted private networking gate:** current documentation supports network-isolated Foundry
resources and a dedicated agent subnet delegated to `Microsoft.App/environments` (minimum /27,
/24 recommended), separate from `Microsoft.Web/serverFarms` Function integration. Each Foundry
resource needs its own agent subnet. Bring-your-own/private networking must be configured on
the Foundry resource/project and verified **inside the hosted container**.

The supplied `main.bicep` creates the Channel network and Storage endpoints; `search.bicep`
can attach Search to an existing PE subnet/zone. Neither configures the Foundry runtime's
VNet injection, central firewall, DNS forwarding, Fabric private links, private ACR or a private
Bot ingress proxy. If any is mandatory, obtain that platform design before deployment.
Current documentation says private ACR image pulls are supported for Foundry projects created
**after 25 June 2026**; older projects require a reachable public ACR endpoint. Verify project
creation date and rollout, not just the registry's settings. [Foundry private networking][hosted-network]

## 9. Customer policy and architecture decision matrix

Return this table with policy definition/initiative IDs, scopes, effect (`Deny`, `Modify`,
`DeployIfNotExists`, `Audit`), exemptions and owner/expiry. A verbal “private Azure” or
“managed identities only” requirement is not enough to build a correct deployment.

| Area / decision needed | Evidence requested from customer | Blocker / action before promotion |
|---|---|---|
| Subscriptions, tenant, region allowlist | Allowed Azure/Fabric/Power Platform regions, cloud and resource placement | Hosted runtime, semantic ranker, DTS, P1v3 and chosen model/SKU must all be supported with quota |
| Preview use | Explicit hosted runtime, SDK, Fabric Data Agent/MCP/runtime approval | Denied Preview means architecture/release change, not relabeling as GA |
| Resource providers / allowed types | Registration rights and allowed `Microsoft.Web`, `Storage`, `Network`, `ManagedIdentity`, `DurableTask`, `BotService`, `CognitiveServices`, `Search`, `Insights`, `OperationalInsights`; ACR/Key Vault if selected | Provider/type denial blocks its dependent component; Entra/Fabric/Power Platform may have separate gates |
| Allowed SKUs/capacity | P1v3/Linux, LRS/ZRS, Search S1/replicas, DTS Consumption/Dedicated, model deployment SKU/quota, existing Fabric F2 | No silent upgrade, new capacity or downgrade to unsupported Free Search |
| Public endpoints | Per-service decision for Bot ingress, SCM, Foundry, model, Search, DTS, Fabric, Power Platform | A deny-public policy can make a service unreachable despite successful ARM deployment |
| Private link and egress | VNet/subnet IDs/delegation/IPAM, PE approvals, route tables, DNS ownership, firewall FQDN/service tags, proxy/TLS inspection, registry age restriction | Search PE alone does not create hosted network integration; incompatible hosted/proxy configuration is a hard gate |
| Identity and credentials | Allowed UAMI/dedicated identities, FIC support, client-secret/certificate policy, vault requirements, credential lifetime | If secrets are banned and supported federation is unavailable, current OBO path is blocked; MI app-only finance is not an alternative |
| RBAC and privileged access | Role allowlist, assignment delegation, PIM, principal restrictions, Azure deny assignments | Resource-scoped assignments must be possible; no catch-all Owner/Contributor workaround |
| Entra consent and app governance | Delegated permission approval, enterprise app group assignment, scope/preauthorization rules, app ownership | Lack of consent/SSO audience agreement blocks downstream calls even with Azure access |
| Conditional Access | MFA, compliant-device/sign-in-frequency, claims challenges, guest/cross-tenant rules, workload identity policies | OBO cannot show an interactive challenge itself; verify re-sign-in/user experience on Teams and M365 |
| Fabric authorization | User groups, item sharing, SQL grants/RLS, **metadata visibility**, publisher rights, Data Agent tenant/runtime settings | Two-user negative testing required; unrestricted Search/catalogue metadata is not acceptable by default |
| Power Platform / DLP | Tier environments, security groups, connectors/knowledge sources, DLP business/nonbusiness grouping, publishing rules | Disallowed connector/cross-boundary data movement blocks KPIpedia; do not use shared owner credentials |
| Data residency / AI processing | Model deployment type (`Standard`/regional vs Global/DataZone), Search region, Fabric AI cross-geo processing/storage, PP location | Global inference and Fabric cross-geo switches need explicit approval; region labels alone do not prove processing locality |
| Encryption / customer-managed keys | Services requiring CMK, key rotation/access/network rules, approved crypto | Current templates use service-managed encryption. Mandatory CMK requires reviewed additions; no hidden Key Vault is supplied |
| Tags/naming/locks | Required tags and values, cost center, data classification, owner, naming constraints, deletion locks | Supply `additionalTags`; defaults `purpose=demo`, `owner=gbelenky` are recommendations and must be replaced for customer ownership |
| Diagnostics / content logging | Workspace destinations, ingestion auth/private link, allowed fields, query-log approval, SIEM export, alert requirements | Never log assertions/tokens or subagent finance answers. Search query logs can contain sensitive terminology |
| Retention / deletion / backup | Agent session TTL, Channel state/replay/replies, DTS history, Search versions, Fabric tables/notebooks, telemetry, image retention | One 30-day Agent TTL does not purge all stores. Define service-specific retention and restore/deletion evidence |
| Reliability / operations | SLO/RTO/RPO, zone/multi-region requirements, maintenance windows, capacity pause/resume, incident owners | P1v3/S1 recommendations are not validated HA; test dependency failure, long-running delivery and recovery |
| Networked build/deployment | Admin runner reachability, signed images/packages, ACR permission mode, SCM access, artifact scanning | Private resources need an approved connected runner; do not temporarily open all networks without approval |
| Teams/M365 distribution | Tenant app policies, scopes, test users, manifest approval, privacy/terms URLs, client availability/licensing | Both surfaces require independent signed-in acceptance; upload success is not runtime access proof |
| Release / rollback | Immutable catalogue + Search index/model mapping, promotion sign-off, old-version retention | Activate only verified releases; changed embedding dimensions require a new index; stale cards must fail safely |

### Acceptance evidence required from each tier

- ARM what-if and policy evaluation reviewed; effective identity IDs/role assignments recorded.
- Container readiness and Channel health pass **and** real downstream network/auth probes pass.
- Search rejects unauthenticated/API-key access; runtime can read but cannot publish; separate
  publisher can create/upload/query only in the correct tier.
- User A and B have different Fabric permissions; denied catalogue labels and facts do not appear
  in cards, model prompts or replies; stale, cross-user and cross-conversation choices fail closed.
- All OBO audiences work with admin-consented scopes, and Conditional Access/expired-token paths
  prompt reauthentication rather than falling back to a workload finance token.
- Known finance statements/ratios, hierarchy aggregation and ambiguous aliases pass the
  publication tests; Data Agent numeric accuracy and citations receive separate business review.
- A fresh conversation on both Teams and M365 receives exactly one final answer/card, no late
  progress after final delivery, and safe cancellation/retry behavior.
- Logs contain no assertions, bearer tokens, secrets or permissioned answer text. Retention and
  rollback runbooks cover all state stores, Search versions, Fabric releases and deployment versions.

## 10. Deployment ownership and ordered hand-offs

1. **Architecture/policy approval:** complete sections 3, 5, 6, 8 and 9; approve capacity costs
   and feature status. Keep staging/prod undeployed until approved.
2. **Entra/Bot bootstrap:** create per-tier registrations, app exposure/credentials and consent.
   Obtain actual IDs and secure values. No ARM template creates these directory objects.
3. **Azure ARM resources:** use the existing demo unchanged where appropriate. For a new tier,
   provision the approved Foundry account/project/model infrastructure and Channel resources.
   `main.bicep` creates Channel/Storage/network/DTS/Bot/monitoring; it does not create Foundry,
   Entra OAuth consent, Fabric or Copilot Studio.
4. **Foundry data plane:** deploy code/image and hosted Agent version to that tier's project,
   respecting .NET 10/Responses adapter contract and reserved environment namespaces. Record
   agent name/version, project and dedicated identity IDs, deployment model versions and image/
   source digest. Do not enable end-user traffic until the required Fabric release and Search
   index are published; an unconfigured Search fallback is not full deployment acceptance.
5. **Independent Search/model/identity add-on:** deploy `search.bicep` to the intended Search RG
   using the actual runtime/publisher IDs and approved network. Apply `model-access.bicep`,
   `foundry-access.bicep` and `registry-access.bicep` in their respective existing resource RGs
   where those grants are needed. **Do not redeploy `main.bicep` just to add Search.**
6. **Fabric data engineering:** create tier-specific workspace/lakehouse items where absent;
   retain current facts/calculators. Stage additive metadata/aliases/scope/calendar, validate SQL
   synchronization, publish embeddings/index, probe hybrid+semantic query, then activate release.
   See [resolver publication runbook](resolver-data.md).
7. **Fabric/Power Platform owners:** grant end-user access, publish the Data Agent and KPIpedia,
   check tenant AI settings/DLP/knowledge scope, and verify delegated calls separately.
8. **Release operations:** inject the complete tier settings/secret inputs, publish the Channel
   and Agent, package Teams/M365, and execute acceptance with fresh conversations. A redeployed
   hosted version may not replace containers already pinned to old conversations.

Commands and template boundaries: [deployment runbook](deployment.md).

[ai-roles]: https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/ai-machine-learning
[storage-roles]: https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/storage
[integration-roles]: https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/integration
[container-roles]: https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/containers
[monitor-roles]: https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/monitor
[rbac-administrator]: https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/privileged#role-based-access-control-administrator
[functions-mi]: https://learn.microsoft.com/azure/azure-functions/manage-connections?pivots=functions-auth-identity&tabs=host
[foundry-rbac]: https://learn.microsoft.com/azure/foundry/concepts/rbac-foundry
[hosted-permissions]: https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agent-permissions
[hosted-network]: https://learn.microsoft.com/azure/foundry/agents/how-to/virtual-networks
[teams-sso]: https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad
[obo]: https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow
[fic]: https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity
[fabric-agent]: https://learn.microsoft.com/fabric/data-science/data-agent-end-to-end-tutorial
[fabric-settings]: https://learn.microsoft.com/fabric/data-science/data-agent-tenant-settings
[search-reliability]: https://learn.microsoft.com/azure/reliability/reliability-ai-search
