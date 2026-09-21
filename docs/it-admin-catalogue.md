# Customer IT catalogue: development environment

**Document review: 21 September 2026.** Audience: customer Azure, Entra, Microsoft 365,
Fabric, Power Platform, networking, endpoint-management and application teams.
This catalogue applies to both implementations; a customer may deploy **either .NET
or Python, or both**. Running both is not required.

This is an implementation and approval catalogue, not evidence that the customer's
tenant has been provisioned or tested. The repository's existing deployment evidence
belongs to its demonstration environment. Staging and production are **planned
independently and not deployed**.

For customer administrators creating and configuring their own resources without
supplier provisioning scripts, share the **[standalone installation guide (PDF)](customer-it-installation-guide.pdf)**.
Its [editable source](customer-it-installation-guide.md) covers portal procedures,
public Microsoft deployment tools, application-artifact handover and acceptance.
The repository-oriented runbooks below serve the application development team.

| Document | Use |
| --- | --- |
| [Architecture and environment diagram](architecture-diagram.md) | Shared design and Development versus Staging/Production boundaries |
| **[Customer development and onboarding runbook](development.md)** | Fresh-clone setup, local development, customer bootstrap and first deployment |
| [Deployment runbook](deployment.md) | Packaging, targeted redeployment, Bot configuration and app installation |
| [Resolver publication](resolver-data.md) | Finance metadata, Search projection and release activation |
| [Availability and support](availability-and-support.md) | Service/feature status and official references |
| [.NET guide](../src/dotnet/README.md) / [Python guide](../src/python/README.md) | Language-specific prerequisites, configuration and package pins |
| [Operations evidence](operations.md) | What was actually tested in the demonstration environment, including remaining gates |

## 1. Scope and decision gates

The initial customer development baseline is **one Microsoft Entra tenant, Azure
public cloud, synthetic or explicitly approved non-production finance data, and a
small named pilot group**. Azure, Microsoft 365, Fabric and Power Platform must all
be available to that group; an Azure subscription alone is insufficient. Sovereign
clouds, cross-tenant/B2B scenarios and private-only networking need a separate
compatibility assessment; current public-cloud endpoints and tenant-pinned identity
validation are not a multi-cloud or multi-tenant implementation.

Obtain these decisions **before** purchasing resources or running provisioning:

| Gate | Customer decision / required evidence | Stop condition |
| --- | --- | --- |
| Preview features | Approve the preview-versioned Activity API route and Foundry-managed state feature, not just the Foundry service | Policy prohibits Preview or requires an SLA for development state |
| Application dependencies | Review package provenance, redistribution/licensing and the .NET source-built Activity transport described in the language guide | Customer will not accept that dependency; do not substitute an untested package |
| Tenant and channels | Select pilot users, supported Teams/M365 experiences, app approval route and licence entitlements | Custom agents/apps or required delegated sign-in are prohibited |
| Identity | Approve app registration, exposed API scope, downstream consent, Bot OAuth connection and selected runtime credential mechanism | Required scopes or the selected credential mechanism cannot be approved |
| Network | Approve actual hosted ingress/egress and workstation paths separately | Private-only/deny-public policy cannot be met by the selected native Activity deployment |
| Data and AI | Approve metadata projection, model deployment geography, prompts, transcripts and exploratory analysis | Data residency, DLP or finance-data policy excludes any required processing hop |
| Capacity | Validate hosted runtime, model and Fabric region/capacity/quota availability | Requested features cannot run in the permitted regions |
| Ownership | Assign an application support owner and separate data/security approvers | No owner for credentials, financial correctness, incidents or costs |

Do not solve a failed gate by disabling Conditional Access, widening SQL permissions,
allowing all networks, creating an alternate ungoverned tenant, or replacing delegated
finance access with an application identity.

## 2. Service bill of materials

Quantities below are a **logical starting point**, not a fixed SKU quotation. Existing
customer services can be reused only after their owners approve capacity, isolation,
cost allocation and access. For two implementations, share approved platform/data
dependencies where appropriate, but create two hosted agents, Bots and state boundaries.
Availability is **service/feature status only**, not SDK status. Recheck the linked
[availability register](availability-and-support.md) when approving the deployment.

| Item | Development requirement and quantity | Availability / qualification | Capacity, licence and approval considerations |
| --- | --- | --- | --- |
| Azure subscription and resource group(s) | One approved development subscription; dedicated RG or customer-approved split RGs | GA | Budget/cost centre, permitted regions/providers/resource types, tags, locks and deployment principal |
| Microsoft Foundry account and project | One approved account/project baseline; customer-created resources, not demonstration IDs | GA core service | Check native Activity hosting availability, isolation, regional runtime quota and project permissions |
| Foundry hosted agent | One per selected implementation | GA hosting service; **Activity API route is preview-versioned** | `dotnet_10` or `python_3_13` native code hosting; configured replicas consume resources even during low usage |
| Azure Bot Service | One per implementation, pointing to that agent's Activity endpoint | GA | Teams/M365 channel, supported identity configuration, OAuth connection and package must agree |
| Foundry-managed durable state | One named store and stable HMAC salt per implementation in the project | **Public Preview; no SLA** | No separately provisioned customer Storage or Cosmos account for this application state; approve retention and loss/retry tolerance |
| Azure OpenAI routing deployment | One approved tool-calling deployment, shareable by both variants | GA selected model; deployment/version/region must be verified | Repository baseline is GPT-5.4-mini. Reserve token/request capacity; review model retirement and Global/Data Zone/regional processing choice |
| Azure OpenAI embedding deployment | One approved deployment bound to the catalogue release | GA selected model | Baseline is text-embedding-3-large at **1536 requested dimensions**. Model default dimensions must not silently replace this binding |
| Azure AI Search | One approved service; one versioned index per retained catalogue release | GA | Must support vector search and semantic ranker in the chosen region/tier. Budget semantic queries, vector capacity and retained indexes; do not assume the free tier suffices |
| Microsoft Entra ID | Customer tenant, finance OAuth application, service principals and per-agent runtime/Bot identities | GA core identity capabilities | App-creation policy, consent workflow, federation, Conditional Access and credential lifecycle; advanced controls may require additional licensing |
| Microsoft Fabric capacity/workspace | One approved active capacity and development workspace, or an authorized existing allocation | GA core platform | Capacity/licensing must support the chosen Data Agent/runtime features and test users. A paused capacity is not a usable dependency |
| Fabric lakehouse and SQL analytics endpoint | Approved finance facts/dimensions plus resolver tables and delegated SQL access | GA | Schema/formulas are application contracts, not automatic discovery of an arbitrary customer finance database |
| Published Fabric Data Agent / MCP endpoint | One published, permissioned finance Data Agent | Standard runtime **GA**; reviewed MCP documentation does not establish a separate MCP GA declaration | Approve tenant settings, runtime, capacity, published item access and MCP consumption. Do not infer every integration's status from core service GA |
| Copilot Studio KPIpedia | One published KPI-definition agent in an approved Power Platform environment | GA service | Environment access, author/publisher entitlement, usage capacity/credits, agent sharing and DLP approval are separate from Azure RBAC |
| Microsoft Teams / Microsoft 365 Copilot | Named pilot group and approved custom-app installation/distribution route | GA services; verify the selected custom-agent experience | Microsoft 365 licensing and admin policies are not provisioned by the Azure deployment; a licence for one surface is not proof of entitlement to every surface |
| Application Insights and Log Analytics | Recommended development monitoring, or an approved existing workspace | GA | **Full application wiring is not assumed implemented.** Verify actual telemetry ingestion, redaction, alerts, retention and access before relying on it |
| Azure Key Vault | Recommended for managed credential/salt custody where required by policy | GA | **No automatic Key Vault configuration-provider integration is claimed.** Approve a secret-delivery process or implement/test it before enforcing Key Vault-only delivery |
| VNet, Private Link, Private DNS, firewall/proxy | Conditional on customer networking requirements | GA services; each connected feature has separate restrictions | A private endpoint does not automatically provide hosted egress, SaaS reachability or Bot ingress; see the network matrix |
| Cosmos DB for NoSQL | **Not required for this development baseline** | GA service | Planned separately for staging and production; the application adapter and its operational controls are not implemented |

**Not separate runtime requirements for this native-code path:** AKS, Container Apps,
App Service, Azure Functions, a customer VM, ACR/Docker, Service Bus or a customer
Storage account for finance memory. Do not add these merely because another hosting
tutorial uses them. The source repository, package feeds and deployment tooling are
development dependencies, not additional Azure application services.

The documented Microsoft 365 **publish operation uses `v1`**, whereas its Activity
messaging route uses **`2025-05-15-preview`**. These are different APIs; the former
does not establish GA status for the latter. See the
[current publishing guide](https://learn.microsoft.com/azure/foundry/agents/how-to/publish-copilot-virtual-network).

### Cost and capacity ownership

The Azure owner should estimate Foundry hosting/replicas, model input/output tokens,
embedding generation, Search capacity/semantic queries, diagnostics retention and any
private networking. Fabric capacity, Copilot Studio usage and Microsoft 365 licences
can be billed/managed outside the application's Azure RG. Include all three budgets.

Record the expected pilot users, concurrent conversations, messages per user/day,
peak tests, maximum tool duration, catalogue size/vector dimensions and retained
catalogue releases. Start with the smallest **feature-compatible** capacity and load
test it; neither a cheapest SKU nor a blanket high-tier recommendation is a sizing
exercise. Set budget alerts and a named spending owner; alerts are not hard spend caps.
Schedule idle capacity changes only when its owner confirms no shared workload is affected.

## 3. Ownership and access-request catalogue

One person may hold several roles in a small development team, but approvals remain
separate. Use customer groups/PIM and scoped, time-bound elevation where available.

| Work item | Accountable customer owner | Deliverable to application team |
| --- | --- | --- |
| Subscription landing area, provider registration, quota and resource deployment | Azure subscription/platform team | Approved subscription/RGs/regions, role assignments, budget and policy-exception decisions |
| Entra registrations, enterprise applications, exposed scope, credentials and consent | Identity/security team with authorized application/consent administrators | Approved OAuth configuration, service principals, consent evidence, credential owner and expiry/rotation process |
| Conditional Access, tenant restrictions, user risk and B2B | Identity/security team | Supported pilot sign-in scenario and tested end-to-end OBO result |
| DNS, routing, VPN, firewall, proxy and TLS trust | Network and endpoint-management teams | Approved path matrix, required workstation trust configuration, runtime connectivity evidence |
| Teams/M365 app approval, upload, installation and user assignment | Microsoft 365/Teams administrators | Pilot app publication/install policy and assigned user group |
| Power Platform environment, DLP, KPIpedia sharing and publishing | Power Platform/Copilot Studio administrator | Environment ID, published agent schema/name, endpoint/access and approved usage |
| Fabric capacity, tenant switches, workspace/items and SQL permissions | Fabric administrator and data platform owner | Active capacity, approved workspace/lakehouse/Data Agent IDs, SQL endpoint/database, least-privilege pilot permissions |
| Reporting hierarchy, formulas, RLS/visibility and test data | Finance data owner/controller | Approved data contract, metadata visibility, known-answer tests and analytical review criteria |
| Build tools, public-source dependency approval and artifact scanning | Developer platform/software supply-chain team | Approved tool versions, feed access/mirrors, provenance and reproducible build evidence |
| Code, configuration, app package and targeted deployment | Application/development team | Reviewed artifact/configuration, test results and selected-agent version |
| Catalogue staging/Search publication/activation | Privileged data publisher | Verified immutable release and rollback binding; separate from runtime read access |
| Logs, alerts, incidents, retention and retirement | Application operations with security/data owners | Runbook, safe log access, retention rules and approved teardown inventory |

Resource **Contributor is not** role-assignment authority, Entra app-administration
authority, tenant-wide consent, Fabric SQL access, Power Platform access or Microsoft
365 app approval. Prefer an approved platform provisioning role scoped to the target
RG and separately controlled RBAC assignment rights; do not make every developer an
Owner. Determine the exact Foundry data-plane/deployment roles for the selected
service operation, rather than treating a role title as proof all operations will work.

## 4. Customer input and configuration handover

Maintain one approved configuration record per environment, with per-implementation
entries where indicated. This is a request form: **no demonstration IDs or secrets
should be copied into it**. The [onboarding runbook](development.md) explains how
these inputs map to the repository and azd.

| Required input | Supplied/approved by | Handling and validation |
| --- | --- | --- |
| Tenant ID; subscription ID; RGs; permitted regions; environment name | Azure/identity owners | Non-secret identifiers; verify Azure CLI and azd select the same intended customer context |
| Resource naming, DNS and required tags/cost centre | Platform team | Review literal defaults in the repository; tags and names must satisfy customer policy |
| Foundry account/project IDs and project endpoint | Foundry administrator | Exact approved resource IDs; establish supported native hosting and region/quota |
| Routing deployment/model version/reasoning setting | AI platform owner | Deployment name is not just catalogue model name; run routing regressions after a model change |
| Embedding endpoint/deployment/dimensions | AI platform/data publisher | Must match the active resolver release and vector schema |
| Search resource ID and HTTPS endpoint | Search administrator | Both identify the same authorized service; semantic ranker and network mode confirmed |
| Fabric workspace/lakehouse/Data Agent IDs, SQL host/database, published runtime | Fabric owner | Same development data boundary; obtain SQL host from Fabric, not from an Azure SQL example |
| KPIpedia environment ID and published schema/name | Power Platform owner | Validate a real delegated invocation, not only portal visibility |
| Finance OAuth client ID, tenant, accepted audience and enabled exposed scope | Identity owner | Scope identifier, consent and access-token version explicitly reviewed |
| Per-variant Bot identity and runtime principal/client IDs | Provisioning owner | Read back after provisioning; distinguish application/client IDs from object/principal IDs |
| Per-variant `mcs` token-exchange resource and package identity/version | Identity and M365 owners | Must agree with the approved connection; need not equal the shared finance client's identifier URI |
| Finance client credential and any approved runtime OBO federation | Identity owner | Current wrappers require the approved secret; no tokens/secret values in tickets, Git, screenshots or command history |
| Per-variant stable random session salt and state-store name/retention | Application/security owner | Salt is secret and stable across redeployments; separate store/key material for each environment and variant |
| App name/description/publisher, support/website/privacy/terms URLs and icons | Customer application/M365 owner | Replace demonstration branding and links before customer distribution |
| Pilot users, permitted/restricted finance scopes and test questions | Finance/M365 owners | Include a restricted user, not only an administrator account |
| Network evidence, telemetry destination, retention and support contacts | Network/operations owners | Include the hosted runtime path, not just a laptop test |

Never use an access token as a configuration value or a checked-in test fixture.
Local azd environment files and user-secret stores are **not an enterprise vault**;
review filesystem permissions, endpoint protection and accidental artifact inclusion.

## Identity and authorization boundaries

| Identity/boundary | Responsibility |
| --- | --- |
| Per-implementation Bot registration | Route Teams/M365 Activities to the corresponding native Foundry endpoint |
| Foundry runtime managed identity | Access its approved model deployments, embeddings and metadata Search index |
| Signed-in human | Authorize Fabric SQL, Fabric MCP and KPIpedia through delegated OBO |
| Finance OAuth confidential client | Exchange validated user assertions; may be reused by authorized Bots in the same environment |
| Catalogue publisher | Stage Fabric metadata and publish Search schemas/documents; separate from runtime |
| Deployment/operator principal | Deploy/configure scoped resources and inspect approved diagnostics; not a substitute finance user |

**The Bots are distinct.** Sharing an authorized finance OAuth client does not share
Bot registrations, package IDs, endpoint routing or state. Do not infer resource
IDs from naming patterns. Read back each deployed Bot and its Activity endpoint
when approving a release.

The host validates the user assertion's signature, issuer, tenant, audience, lifetime
and user identity, and cross-checks the Activity caller before state access.
Bot-service authentication alone does not grant financial permissions. No app-only
finance fallback is allowed.

### OAuth and app package checklist

- Match the Bot's `mcs` connection, approved delegated scope, OAuth client,
  token-exchange resource and package `webApplicationInfo`.
- Validate delegated finance scopes against the OAuth application's enabled API
  scopes and check its supported access-token version. Validate the Bot's
  token-exchange resource separately. For the documented new-installation SSO
  pattern, use the finance SSO application's Application ID URI, also matching
  `webApplicationInfo.resource`. A release may accept a selected Bot URI that is
  not an identifier URI on that application; that acceptance does not prove fresh
  silent SSO. Validate any such contract before customer acceptance. Treat
  audience/consent changes as explicit security changes.
- Keep Bot/custom-engine routing IDs distinct from the finance OAuth client; the
  package builder enforces this application contract. Teams supports that separation,
  so the difference alone is not an SSO fault.
- Prefer managed identity/workload federation where the confidential-client flow
  supports it. An implemented federation path still requires the actual Entra
  credential and grant configuration; do not assume a secret has disappeared.
- Runtime OBO federation and **Azure Bot OAuth connection credentials are different
  paths**. The first-time channel configuration can require a client secret even
  when the hosted OBO code uses a federated managed identity. A tenant that prohibits
  client secrets must approve a supported alternative and its implementation before
  bootstrap; creating a Key Vault alone does not remove this requirement.
- The **current wrappers require `OBO_CLIENT_SECRET` and the manifest injects it
  into the runtime**. The implemented federation capability is not the default
  secretless deployment path. Review tooling/configuration changes and test the
  resulting identity flow before claiming a fully federated deployment. Channel
  configuration consumes an existing password; it does not create or rotate it.
- Store remaining secrets outside Git; planned staging/production use Key Vault
  with expiry monitoring and rotation tests.
- Use the branded package for the selected implementation. Tenant custom-app
  policy governs installation; Developer Portal import alone is not installation.
- Prove fresh silent SSO separately from interactive login or cached-token replies.

## Least-privilege access

| Principal and scope | Required access |
| --- | --- |
| Native deployment/publishing operator → Foundry project | **Foundry Project Manager** for the source-code path; verify effective permissions |
| Bot resource/channel manager → publishing RG | **Azure Bot Service Contributor Role**, separate from Foundry project permissions |
| Pilot users/group → selected agents | **Foundry Agent Consumer** for endpoint-only access under BotServiceRbac; use the documented CLI procedure for narrow agent/project scope |
| Runtime → resolver Search service | **Search Index Data Reader** |
| Runtime → model/embedding account | **Cognitive Services OpenAI User** for direct inference calls |
| Publisher → Search service | **Search Index Data Contributor**, plus **Search Service Contributor** when creating versioned schemas |
| Publisher → embedding account | **Cognitive Services OpenAI User** |
| Runtime/platform → Foundry project/agent | Verify the current runtime's platform grants and BotServiceRbac ingress requirements; add only necessary scoped grants |
| Human → Fabric | Approved workspace/item and SQL access, including visibility of resolver metadata and financial scope |
| Human → Copilot Studio | Environment/agent access and consent required by the selected delegated client |
| Planned runtime → Cosmos | Cosmos **data-plane** RBAC scoped to required database/container operations; ARM Contributor is not data access |
| Planned runtime → Key Vault | Only needed secret/certificate operations at the approved scope |
| Operators → telemetry | Least-privilege read access and approved retention/export controls |

Publication's **Just you** option retains BotServiceRbac; **People in your
organization** selects **BotServiceTenant** and is an authorization change, not
merely app distribution. The current official publisher-role guidance is
inconsistent for Foundry User. See the standalone [installation guide](customer-it-installation-guide.md#9-rbac-and-effective-permissions)
for verified role names, assignment limits and publication choices.

Provisioning needs resource-write and, when creating assignments, role-assignment
write permissions at the relevant scopes. These do not grant Entra consent, Fabric
finance permissions or Power Platform administration. Do not use subscription
Owner or broad data access to work around a failed dependency check.

Search does not inherit Fabric row-level security. Revalidate candidate visibility
through the caller's delegated Fabric access **before** exposing labels, paths or
definitions and before any model reranking. Recheck the complete catalogue release
and selected organization scope at execution and after clarification.

### Token and authorization flow

1. Teams/M365 reaches the selected Bot and the protected native Activity endpoint.
   That hop authenticates the service, **not the finance user's data access**.
2. The Bot OAuth connection obtains a delegated assertion for the approved finance
   API. The application validates signature, tenant, issuer, audience, lifetime,
   delegated user identity and agreement with the Activity caller before accessing state.
3. The finance confidential client exchanges that assertion separately for Fabric SQL,
   Fabric REST/MCP and Copilot Studio. The delegated user must also have access to
   the target data/item/agent; admin consent alone is insufficient.
4. The hosted managed identity calls models/embeddings and Search. It may authenticate
   the confidential client's OBO exchange through federation, but does **not** become
   the employee or grant employee data access.

Fabric SQL uses `https://database.windows.net/.default`; Fabric MCP uses
`https://api.fabric.microsoft.com/.default`. These are **runtime token-request scopes**,
not instructions to enter `.default` in the Bot's named finance OAuth connection.
Copilot Studio's token scope is derived by its client from the environment/connection
settings; do not guess an audience. The identity owner must record the actual
downstream delegated permission IDs and consent on the finance application.

An OBO exchange cannot simply display an MFA prompt to the downstream API. Conditional
Access claims challenges, consent changes and expired/revoked sessions must be tested
in the real channel. No end-to-end claims-challenge recovery certification is claimed
for this application. Preserve policy and escalate the incompatibility; do not
recommend excluding all pilot users from MFA or compliant-device requirements.

### Common identity and tenant-policy blockers

| Policy | Likely symptom / affected step | Required customer action |
| --- | --- | --- |
| Developers cannot register applications or create enterprise applications | First bootstrap cannot create/configure the finance app or credential | Identity administrator supplies approved registrations/service principals and explicitly authorizes required changes |
| User consent disabled / admin consent workflow required | Login succeeds, downstream OBO lacks the delegated permission or returns consent-required | Approved consent administrator reviews actual API/scopes and grants required consent; do not ask users to approve broader alternatives |
| Enterprise application requires user assignment | Only some pilot users can sign in despite app installation | Assign the approved pilot group and verify both allowed and intentionally excluded users |
| Client secrets prohibited, short maximum lifetime, or password creation blocked | First-time Bot `mcs` setup fails or later sign-in breaks after expiry | Review the channel credential path separately from runtime federation. Stop if policy cannot be met; do not create a longer-lived secret elsewhere |
| Certificate-only confidential clients | A registration certificate exists but the runtime/channel configuration does not consume it | Current OBO code supports secret or MI-federated assertion, not a drop-in certificate configuration. Implement and test a supported path before approving this policy combination |
| Federated credentials/workload identities restricted | Runtime cannot authenticate the finance confidential client | Identity owner approves issuer/subject/audience for the actual runtime principal and validates the hosted exchange; Azure RBAC alone does not create this trust |
| MFA, authentication strength, compliant device, sign-in frequency or risk policy | Cached requests work while fresh login/OBO produces a claims challenge or sign-in loop | Test the full channel with enforced policy and review Entra sign-in logs; resolve supported client/claims handling rather than disable the policy |
| Cross-tenant/B2B restrictions or wrong tenant login | User's tenant/object ID or audience fails validation; downstream guest permissions differ | Start with the same-tenant pilot; separately assess guests and resource-tenant identities before enabling them |
| Restricted Azure role assignment | Deployment succeeds but model/Search use fails, or wrapper stops during grants | RBAC owner performs only the required assignments at approved scope; do not grant subscription Owner to the runtime |
| Teams custom app upload/install/availability restricted | Package builds but users cannot upload/find/add it | M365 admin uses the approved organizational distribution and pilot assignment path; do not bypass app policy |
| Power Platform DLP / agent publication or sharing restrictions | KPIpedia is visible to its maker but cannot be invoked by pilot users | Power Platform owner reviews agent/channel/data-source/endpoint policies and publishes/shares the approved configuration |
| Fabric tenant AI/MCP/cross-geo settings restricted | Data Agent unavailable although ordinary SQL works | Fabric owner validates the exact runtime requirements and data-residency approval; enabling a tenant switch is not an application-developer repair |
| Finance permission changes | An existing chat/card refers to metadata or data no longer permitted | Keep delegated access revalidation; test denial and reset/retry behavior rather than cache a privileged answer |

### Fabric and Power Platform administrator prerequisites

The Fabric owner should record these separately from Azure RBAC:

- **Capacity and publication:** the documented Data Agent prerequisites include paid
  **Fabric F2 or higher**, or an existing eligible **P1 or higher with Fabric enabled**.
  Publish the Data Agent and authorize the pilot users to the item and its sources.
  This is a feature minimum, not a performance-sizing recommendation.
- **Tenant/capacity switches:** review "Users can use Copilot and other features
  powered by Azure OpenAI" and "Capacities can be designated as Fabric Copilot
  capacities" against the current
  [Data Agent tenant settings](https://learn.microsoft.com/fabric/data-science/data-agent-tenant-settings).
  Scope enablement to the approved pilot where supported; allow documented
  propagation time before diagnosing the application.
- **Geography:** the documented Data Agent and source-workspace capacities must be
  in the same region. Where the capacity is outside the US/EU Data Boundary,
  review the required cross-geography **processing and storage** approvals.
  Such approval does not remove the same-region source limitation.
- **Authentication:** Fabric MCP now documents user and service-principal tokens.
  **This application deliberately requires delegated human access**; service
  capability is not permission to introduce an app-only fallback. Only enable
  service-principal Fabric API settings for separately approved automation that
  actually requires them, with the appropriate group scope.
- **Private sources versus private MCP:** selected Lakehouse/Warehouse/SQL private
  source scenarios are documented. That does not prove private access from this
  hosted application to the external MCP endpoint. Current private-source limitations
  exclude Kusto, semantic models and mirrored sources in those scenarios, and
  cross-region Private Link SQL access. Validate the exact source/runtime combination.

The Power Platform owner must approve KPIpedia's environment, publication, sharing,
connectors, channels and applicable DLP grouping. Copilot Studio DLP governs that
part of this application; it is **not a firewall for the native Foundry runtime's
direct SQL or MCP calls**. Power Platform connector endpoint filtering is itself
**Preview**, limited to supported/static design-time endpoints and documented
authentication exceptions. Do not treat it as universal runtime egress enforcement.
See the [official control references](#official-policy-and-networking-references).

## State and environment design

| Requirement | Development | Staging and production, separately |
| --- | --- | --- |
| Compute | Customer-approved Foundry project and explicit language service | Independent approved Foundry resources/project per environment |
| State service | Foundry-managed durable state **Public Preview, no SLA** | Dedicated **GA Cosmos DB for NoSQL** account per environment |
| Adapter | Existing allowlisted JSON load/save | **Not implemented**; implement version-aware serialization/migration |
| Isolation | Distinct store names and stable HMAC salts per implementation | Separate account/container scope, identity, salt and configuration |
| Concurrency and lifecycle | Existing process-level turn serialization and bounded state lifetime | ETag optimistic concurrency, TTL, conflict tests and measured limits |
| Continuity | Memory persistence only; user retries interrupted work | Backup/restore policy and drills; still no durable in-flight recovery |
| Operations | Verify available runtime logs and actual telemetry wiring | Application Insights, Log Analytics, alerts and support ownership |
| Secret management | Approved ignored configuration | Key Vault and reviewed rotation procedures |
| Connectivity | Approved development endpoints | Validate Private Link/VNet/DNS and all required SaaS paths |

Do not implement a Cosmos adapter as an unrestricted transcript store. Preserve
schema versioning, caller binding, allowed-field validation, expiry, reset and
history pairing. Persist no delegated tokens or permissioned tool-result bodies.
Maintain separate .NET/Python state even when both are deployed into one environment.

Stable HMAC salts are part of the identity-to-state mapping. Rotating them without
a migration plan orphans existing conversations. Backup/restore plans must include
compatible schema and key material, while applying authorization afresh on use.
Test expired, incompatible, conflicting and foreign state, not only a happy-path read.

The managed-state documentation specifies a **1 MB maximum value** and a default
**30-day idle expiration**, renewed by writes rather than reads. These are service
limits/defaults, not a promise that application history is retained for 30 days.
Record the actual application expiry and approved retention. Platform-context user
isolation cannot be proven by a laptop test; verify the application's validated
caller binding, HMAC keys and store boundaries with two real deployed users.

The current state documentation does not establish a standalone private-endpoint/
firewall contract or a blanket prohibition on private networking. Its use with the
selected native Activity/private-egress configuration remains a **verification
gate**, not a claim of either universal support or universal non-support.

Cosmos support does not resolve the **Preview Activity API** approval. None of
these planned controls should be reported as deployed simply because they appear
in a diagram or template.

## Configuration checklist

Use validated language configuration contracts, not copied secret-bearing files.

| Group | Values to validate |
| --- | --- |
| Foundry/model | Project endpoint, selected deployment, reasoning settings, real runtime identity and model region/SKU |
| State | Per-implementation store name, stable secret salt, retention and schema |
| OBO | Tenant, confidential-client ID, expected audience, credential/federation and named OAuth connection |
| KPIpedia | Power Platform environment and published agent schema |
| Fabric SQL | SQL endpoint/database, timeout, delegated metadata and fact permissions |
| Fabric analysis | Workspace, published Data Agent ID/runtime/endpoint and bounded analysis timeout |
| Resolver | Search endpoint, semantic configuration, embedding endpoint and candidate/clarification limits |
| Release binding | Fabric-owned catalogue version, versioned Search index, embedding deployment and dimensions |
| App package | Routing IDs, OAuth resource, display name, version and allowed domains for the selected implementation |
| Telemetry | Content-capture disabled, redaction proven, operator access and correlation identifiers |

The active catalogue release is authoritative for index/deployment/dimensions.
Do not independently change one value in runtime settings. The [publication runbook](resolver-data.md)
documents stage/verify/publish/activate responsibilities.

## Network and data governance review

Approve **three distinct boundaries**: the developer workstation/build network, the
hosted agent's outbound network, and Bot/M365 ingress to the published agent.
An administrator's successful laptop request proves none of the other two.

The following are flow requirements, **not a complete firewall allowlist**. Use actual
resource endpoints, regional service destinations and current Microsoft service
documentation to obtain the approved destination set. A token audience such as
`https://database.windows.net/` is not the SQL server's network hostname.

| Source / caller | Destination and protocol | Purpose / policy issue | Required proof |
| --- | --- | --- | --- |
| Managed developer workstation or build runner | Approved source/package/tool repositories, HTTPS 443 | Git clone, NuGet/pip restore, .NET public-source Activity build, CLI/extension downloads; proxy/package-mirror restrictions | Reproducible restore/build through approved feeds and certificate trust, without disabling TLS verification |
| Workstation/deployer | Entra HTTPS 443; Azure Resource Manager and Foundry management/data endpoints HTTPS 443 | Sign-in, provisioning, code upload, deployment readback; tenant restriction and CA controls | Correct tenant/subscription and allowed tool authentication; reach both control and deployment data planes |
| Foundry code provisioning/build environment | Documented platform image/authentication endpoints, HTTPS 443; approved package feeds for remote builds | Even bundled deployment retains platform dependencies; Python `remote_build` also restores application packages in the service | Approved hosted provisioning egress and successful cloud restore/startup; local NuGet/pip success is not this evidence |
| User's Teams/M365 client | Microsoft 365 and sign-in endpoints HTTPS 443 | App discovery, installation, sign-in and conversation | Tenant-approved client/device and Microsoft 365 endpoint policy; the browser reaching Azure portal is not sufficient |
| Azure Bot/channel infrastructure | Foundry published native Activity endpoint HTTPS 443 | Authenticated inbound delivery | The exact Bot/agent route works under the chosen Foundry network configuration; do not assume VPN-only ingress works |
| Hosted Activity application | Bot Connector/token-service endpoints HTTPS 443 | OAuth token retrieval and asynchronous replies | Runtime can obtain the user token and send a late reply; static health alone is insufficient |
| Hosted application | Entra OIDC metadata, signing keys and token endpoints HTTPS 443; platform managed-identity facility | Assertion verification, key rollover, OBO and application credentials | Runtime DNS/trust/identity work; arbitrary supplied JWTs or a copied developer token are not substitutes |
| Hosted application | Foundry project/model and embedding endpoints HTTPS 443 | Routing, embeddings and constrained reranking | Actual runtime identity, resource RBAC, quota and network reachability |
| Hosted application | Foundry managed-state service via supported platform route | Filtered state read/write | First authenticated conversation can load/save; readiness does not exercise lazy state access |
| Hosted application | Approved Search endpoint HTTPS 443 | Hybrid/vector/semantic metadata retrieval | Service DNS and network policy plus runtime read RBAC; a private endpoint requires a supported route and DNS from this runtime |
| Hosted application / authorized SQL publisher | Fabric SQL analytics hostname, TLS over TCP 1433 | Delegated metadata and fact queries | SQL connectivity from the hosted boundary and publisher network; an HTTPS-only proxy is not a SQL tunnel |
| Hosted application | Published Fabric MCP URL on `api.fabric.microsoft.com`, HTTPS 443 | Delegated analytical requests; JSON/streamed responses | Tenant/item permission, long-lived response handling, proxy/gateway timeouts and runtime policy |
| Hosted application | Environment-specific Copilot Studio/Power Platform endpoints, HTTPS 443 | KPIpedia delegated conversation | Published agent, environment policy/DLP, allowed endpoints and delegated scope |
| Privileged catalogue publisher | Fabric API/OneLake, SQL, Search and embedding destinations | Notebook stage/activation, SQL validation, vector publication | Distinct approved publication path; additional Fabric/OneLake destinations discovered from the selected workflow |
| Runtime/operators, if wired | Approved Monitor ingestion/query and Key Vault endpoints, HTTPS 443 | Diagnostics and secrets | Evidence of instrumentation/secret delivery, permissions and network access; not just provisioned resources |

### Foundry private networking and the Microsoft 365 public route

For a private Foundry resource, Microsoft's documented publishing design enables a
**route-scoped public exception** using
`agent_endpoint.protocol_configuration.activity.enable_m365_public_endpoint`.
Teams/M365 channel adapters cannot use the project's private endpoint. The public
Activity path is:

```text
https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/activityProtocol?api-version=2025-05-15-preview
```

Foundry's edge provides TLS, Microsoft-managed channel source-IP restrictions and
authentication/authorization. Other project protocols and management routes can
remain private. This is **not private Teams ingress**, and the customer VNet does
not protect this SaaS ingress. Do not add a customer public IP, DNAT or reverse proxy
as an assumed prerequisite. A policy forbidding **any** public agent route blocks
this documented design even in development.

The setting describes the platform contract, **not a claim that this repository
configures the exception**. The platform owner must use the supported publication
workflow and read back the deployed settings. See the
[REST publishing/private-network guide](https://learn.microsoft.com/azure/foundry/agents/how-to/publish-copilot-virtual-network).

Private ingress and private egress are independent. A private endpoint on Foundry or
Search does not attach the hosted runtime to the customer's network. The
[managed-VNet documentation](https://learn.microsoft.com/azure/foundry/how-to/managed-virtual-network)
describes Responses-based agents; it does not establish the complete combination of
this application's **native Activity, direct-code runtime, selected private egress,
region and managed state**. That combination is **no-go for deployment sign-off
pending validation**. A separately approved proof-of-capability can resolve the gap;
do not label the combination universally unsupported.

Validate both permitted connections **and denied/public alternatives** from the
actual hosted runtime. Managed-network FQDN rules support ports **80/443**, not
SQL 1433. ZavaFinance establishes direct Fabric SQL connections as well as MCP
requests, so it needs a supported SQL-capable path in addition to HTTPS egress.
Use the current [Fabric SQL connectivity requirements](https://learn.microsoft.com/fabric/data-warehouse/connectivity)
to review SQL-aware filtering and required service coverage; a single host allow rule
may be insufficient. Do not apply HTTPS inspection to the SQL port.

The [source-code deployment guide](https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent-code#firewall-requirements-for-private-virtual-networks)
also lists Microsoft Container Registry and authentication dependencies during
provisioning, even for bundled code. That does **not** make a customer ACR a
requirement. Network owners must review those platform destinations and remote-build
feeds separately from the application's steady-state egress.

### Common network and Azure Policy blockers

| Policy or symptom | Impact | Safe resolution / owner |
| --- | --- | --- |
| Deny public network access / require private endpoints | Can block Foundry publishing, managed-state use or access to Search/models/Fabric depending on the selected feature path | Network/platform owners validate the **native Activity** combination end to end before provisioning. Obtain a documented exception if appropriate, or stop for a supported redesign |
| Mandatory VNet integration / fixed egress IP | The application does not supply its own customer subnet/NAT deployment | Verify supported hosted-network controls; creating a NAT gateway or resource private endpoint alone cannot attach this runtime to it |
| Private DNS linked only to corporate VNet | Laptop resolves privately while hosted runtime cannot resolve/reach the same target | Approve zone links, forwarding and routes for every actual caller; test DNS and connection from each boundary |
| HTTPS inspection or custom corporate CA | NuGet/pip/CLI/SDK TLS failures or broken streams | Endpoint team supplies trusted CA/proxy configuration supported by each tool/runtime; never use insecure TLS flags or `TrustServerCertificate` as a workaround |
| Denied TCP 1433 | Statement/catalogue SQL fails while MCP or portal works | Network team approves the exact Fabric SQL service path and applicable service requirements; do not open a generic SQL port to the internet |
| Proxy buffering / idle timeout | Fabric/KPIpedia answer times out, or an accepted turn never delivers a final reply | Validate streaming and tool-duration budgets at every hop; test a real delayed response rather than only an HTTP 200 |
| Allowed locations/SKUs, no Global model processing | Resource deployment denied or approved resource region does not meet inference residency requirements | AI/platform/data owners select a supported allowed deployment and capacity; physical resource region alone is not proof of processing geography |
| Denied resource type/provider registration, mandatory tags, inherited locks | Bot/host/model role assignment or channel reconciliation fails | Platform owner reviews effective management-group/subscription/RG policy and controlled provider registration; do not remove shared locks |
| Disabled local/key authentication | Can be compatible with runtime model/Search MI but not every bootstrap credential path | Verify provisioning, publisher and Bot OAuth flows separately; do not re-enable keys or add an app-only finance fallback |
| Resource deployment allowed but role-assignment writes denied | Host exists, but Search/model/channel setup fails | Have the RBAC administrator apply the reviewed scoped assignments; retry only the failed step |

### Data movement and residency

User prompts, authorized catalogue text and analytic questions can contain business
data even though stored tool-result bodies are filtered. Review all of these surfaces:

- Teams/M365 transcript and channel retention.
- Foundry/model processing, filtered state and operational logs.
- Search's metadata projection and generated embeddings.
- Fabric Data Agent processing and any enabled cross-geography settings.
- Copilot Studio conversation/knowledge processing and environment policies.
- Local debugger output, package artifacts, CI logs and developer-assistant context.

Use synthetic or approved masked data by default. Do not put raw production
transcripts, bearer tokens or finance extracts into diagnostic tickets or developer
AI tools. Set retention/deletion responsibilities independently for these systems;
`reset` is not a cross-service data-erasure request.

## Promotion gates

1. Scoped infrastructure/configuration review, Preview/API/package exception approval
   and reproducible artifact/SBOM.
2. Tests for actual framework/Activity boundaries, secret-free packaging and guarded
   deployment to only the selected service.
3. Fresh user sign-in, typed/clicked clarification, reset, follow-ups, long-tool
   delivery and per-user isolation.
4. Finance reconciliation of source facts and displayed statements; independent
   review of exploratory output.
5. State concurrency/expiry/privacy tests and, for Cosmos, tested backup/restore.
6. Verified telemetry and alerts without tokens, OAuth state or permissioned result
   bodies; runbook for interrupted turns.

Follow the [deployment runbook](deployment.md), then update the
[operations record](operations.md) with observed evidence, not assumptions.

## Customer development acceptance and handover

The application team and relevant administrator should sign off the following
evidence for the **customer's deployed version**, not inherit the demo's results:

| Test | Expected result / evidence | Owner |
| --- | --- | --- |
| Fresh checkout | Selected variant restores/builds and offline tests pass on an approved developer machine or runner | Development |
| Deployment target | Subscription/RG/Foundry project, selected service, runtime version and artifact hash recorded; other variant unchanged | Platform/development |
| Package and routing | Approved app is installed/assigned; Bot, endpoint, app version, OAuth client and token-exchange resource read back | M365/identity |
| Fresh sign-in | Test after an approved fresh-auth procedure, not merely with cached Bot tokens; enforce applicable CA/MFA/device policy | Identity |
| Three finance tools | Approved KPI definition, deterministic statement and exploratory request all return with source attribution | Finance/development |
| Clarification/context | Missing KPI/scope/date, typed and clicked choices, follow-up period and `reset` behave consistently | Development |
| Restricted users | Two real users with different permissions; no unauthorized metadata/facts, shared state or foreign card execution | Finance/security |
| Revocation/expiry | Expired/revoked consent/token and removed item access produce controlled failures; no fallback to another identity | Identity/security |
| State lifetime | Store isolation, stable salt across redeploy, expiry/reset and incompatible-state behavior checked | Development/operations |
| Network enforcement | Test workstation/provisioning, real Bot ingress, hosted outbound dependencies/DNS and delayed reply separately; prove prohibited alternatives are denied | Network/platform |
| Financial reconciliation | Independently compare selected scopes/periods/formulas/totals to Fabric; review analysis claims and driver signs separately | Finance |
| Operations/privacy | Safe failure/correlation logs visible to operator; no tokens, encoded OAuth state or permissioned result bodies captured | Operations/security |
| Recovery/change | Demonstrate controlled redeployment/rollback and retry of interrupted work; preserve state compatibility/salt | Development/operations |

Define latency/concurrency targets with the customer and measure them with the
approved workload. Functional answer delivery is not financial correctness, fresh
SSO proof, a load test, an SLA, or proof of multi-user isolation.

The handover pack should contain the approved input record, architecture/network
decisions, access and consent evidence, package provenance, source/artifact hashes,
deployment and installed-app versions, active catalogue binding, signed test results,
known limitations, cost owner and operational contacts. Store secret **references**
and rotation owners, never secret values.

## Triage and escalation

| Symptom | First checks | Escalate to |
| --- | --- | --- |
| App absent or custom upload disabled | Correct tenant/account, app assignment and organizational app policy; import is not installation | M365 administrator |
| Bot works but repeated sign-in/401 | Named `mcs` connection, client/audience/scope/token-exchange mapping, credential expiry, consent and CA sign-in logs | Identity + application |
| Statement denied but analysis/definitions work | Correct SQL audience, SQL endpoint network path, resolver-table visibility and user's fact permissions | Fabric/identity/network |
| Search/model 403 | Actual hosted principal, scoped **data-plane** roles, role propagation and service network mode | Azure AI/Search platform |
| Works locally with mocks, fails in Azure | Hosted configuration/native dependencies and actual runtime network/identity, not the developer's cached login | Application/platform |
| Readiness succeeds but first user turn fails | State creation/read/write, OBO and downstream connections are not fully covered by readiness | Application/Foundry |
| Fabric Data Agent unavailable/429 | Published item/runtime, active capacity, tenant settings, permissions and service throttling | Fabric administrator |
| KPIpedia unavailable | Published schema/environment, delegated invocation rights, DLP and usage capacity | Power Platform administrator |
| Wrong figures or misleading analysis | Active catalogue binding, calculator/source contract, source types, reviewed narrative; don't tune infrastructure to mask financial errors | Finance/data owner |
| Old behavior after code update | Selected deployed version and the conversation's native session; start a fresh conversation in the correct agent | Application/M365 |
| Duplicate/missing reply after restart | Process-owned work is not durable replay; correlate the original turn before an approved retry | Application operations |

Collect UTC timestamp, selected variant/version, sanitized correlation/activity ID,
dependency/status/error code and affected user group. Never attach full Activities,
OAuth state, access tokens, secret-bearing environment dumps or financial result bodies.

## Staging and production recommendations

Keep independent environments and identities; do not promote the development
subscription's credentials/data or silently repoint its Bots. In addition to the
planned **GA Cosmos DB for NoSQL** state service:

1. Implement and test its version-aware adapter, scoped MI data access, ETags,
   TTL, backup/restore and authorization after restore. Cosmos provisioning alone
   does not switch the application away from Foundry-managed state.
2. Reassess the Activity API and transport support gates against production policy.
   A GA database does not make the complete application path GA.
3. Use approved CI/CD with immutable artifacts, a controlled deployment identity,
   environment approvals, supply-chain scanning and tested rollback. Repository
   scripts are not by themselves an implemented enterprise pipeline.
4. Wire and verify Key Vault/secret delivery and Monitor/OpenTelemetry privacy,
   alerts, dashboards, retention and incident ownership.
5. Validate private connectivity/residency for the exact hosted and SaaS paths;
   do not describe all traffic as private based on one network diagram.
6. Load/failure-test concurrency, long-running replies, throttling, downstream
   timeouts and recovery. Process-local queues/gates and unconditional state writes
   are not a distributed durable-execution/concurrency guarantee.
7. Establish finance release governance, restricted-user regression tests, data
   reconciliation, analytical quality evaluation, model version/retirement review
   and a defined acceptable support/RTO/RPO target.

## Official policy and networking references

Public documentation reviewed **21 September 2026**. These references support
planning, not proof that the customer's configuration has been tested. Product
documentation dates are not release dates; recheck scope and limitations at approval.

| Control / decision | Microsoft reference |
| --- | --- |
| Hosted Agents GA versus protocol-specific support | [Hosted-agent migration](https://learn.microsoft.com/azure/foundry/agents/how-to/migrate-hosted-agent-preview) |
| Native code packaging and provisioning network | [Deploy from source code](https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent-code) |
| Native Activity and BotServiceRbac configuration | [Agent YAML reference](https://learn.microsoft.com/azure/foundry/agents/concepts/azure-yaml-reference#additional-runtime-protocols-and-activity-endpoints) |
| Teams/M365 publication and scoped public ingress | [REST publishing guide](https://learn.microsoft.com/azure/foundry/agents/how-to/publish-copilot-virtual-network) |
| Inbound versus outbound isolation; connected developer clients | [Networking options](https://learn.microsoft.com/azure/foundry/agents/concepts/networking-options), [private client connections](https://learn.microsoft.com/azure/foundry/how-to/configure-private-link#choose-a-secure-connection-method-to-foundry) |
| Managed outbound scope and rule limitations | [Managed virtual network](https://learn.microsoft.com/azure/foundry/how-to/managed-virtual-network) |
| Search private connectivity | [Search private endpoints](https://learn.microsoft.com/azure/search/service-create-private-endpoint) |
| Managed-state Preview, expiry and isolation | [Durable state store](https://learn.microsoft.com/azure/foundry/agents/concepts/agent-state-store) |
| Fabric capacity, runtime and source limitations | [Data Agent concepts](https://learn.microsoft.com/fabric/data-science/concept-data-agent), [creation prerequisites](https://learn.microsoft.com/fabric/data-science/how-to-create-data-agent) |
| MCP protocol and identity | [Data Agent MCP](https://learn.microsoft.com/fabric/data-science/data-agent-mcp-server) |
| Fabric tenant switches and geography | [Data Agent settings](https://learn.microsoft.com/fabric/data-science/data-agent-tenant-settings), [developer settings](https://learn.microsoft.com/fabric/admin/service-admin-portal-developer) |
| SQL network protocol and private source access | [SQL connectivity](https://learn.microsoft.com/fabric/data-warehouse/connectivity), [Private Link Data Agent scope](https://learn.microsoft.com/fabric/security/security-private-links-overview#data-agent) |
| OBO, MFA and Conditional Access recovery | [OBO protocol](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow), [claims challenges](https://learn.microsoft.com/entra/identity-platform/claims-challenge) |
| App registration and consent administration | [Delegated app administration](https://learn.microsoft.com/entra/identity/role-based-access-control/delegate-app-roles), [admin consent](https://learn.microsoft.com/entra/identity/enterprise-apps/grant-admin-consent) |
| Federated credentials; separate Bot OAuth path | [Workload federation](https://learn.microsoft.com/entra/workload-id/workload-identity-federation), [Bot OAuth federation](https://learn.microsoft.com/microsoft-365/agents-sdk/azure-bot-user-authorization-federated-credentials) |
| Teams upload, approval and user availability | [Custom-app policies](https://learn.microsoft.com/microsoftteams/teams-custom-app-policies-and-settings), [app-centric management](https://learn.microsoft.com/microsoftteams/app-centric-management) |
| KPIpedia DLP and endpoint-filtering limitations | [Copilot Studio DLP](https://learn.microsoft.com/microsoft-copilot-studio/admin-data-loss-prevention), [connector endpoint filtering](https://learn.microsoft.com/power-platform/admin/connector-endpoint-filtering) |

The specific REST publishing guide, updated **28 August 2026**, documents a public
publish API; an older Agent Applications migration article says none is available.
Use the newer, specific publication contract, but still verify the actual extension/
runtime used here. Neither document removes the preview-versioned Activity route or
certifies the exact private-network combination.
