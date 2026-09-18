# IT administration and environment requirements

For Azure, Entra, Microsoft 365, Fabric, Power Platform, networking and operations
administrators. Applies to both language implementations. Development is an existing
demo environment; staging and production are **planned independently and not deployed**.

Start with [architecture](architecture-diagram.md), [service purposes](azure-services-for-controllers.md)
and [availability](availability-and-support.md). Exact setup commands and package pins
are in the [.NET](../src/dotnet/README.md) and [Python](../src/python/README.md) guides.

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
  token-exchange resource separately: it may be the selected Bot URI, not an
  identifier URI on the shared finance OAuth application. Treat audience/consent
  changes as explicit security changes.
- Keep Bot/custom-engine routing IDs distinct from the finance OAuth client; the
  package builder enforces this application contract. Teams supports that separation,
  so the difference alone is not an SSO fault.
- Prefer managed identity/workload federation where the confidential-client flow
  supports it. An implemented federation path still requires the actual Entra
  credential and grant configuration; do not assume a secret has disappeared.
- Store remaining secrets outside Git; planned staging/production use Key Vault
  with expiry monitoring and rotation tests.
- Use the branded package for the selected implementation. Tenant custom-app
  policy governs installation; Developer Portal import alone is not installation.
- Prove fresh silent SSO separately from interactive login or cached-token replies.

## Least-privilege access

| Principal and scope | Required access |
| --- | --- |
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

Provisioning needs resource-write and, when creating assignments, role-assignment
write permissions at the relevant scopes. These do not grant Entra consent, Fabric
finance permissions or Power Platform administration. Do not use subscription
Owner or broad data access to work around a failed dependency check.

Search does not inherit Fabric row-level security. Revalidate candidate visibility
through the caller's delegated Fabric access **before** exposing labels, paths or
definitions and before any model reranking. Recheck the complete catalogue release
and selected organization scope at execution and after clarification.

## State and environment design

| Requirement | Development | Staging and production, separately |
| --- | --- | --- |
| Compute | Existing Foundry project and explicit language service | Independent approved Foundry resources/project per environment |
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

Approve inbound Bot/Foundry connectivity and outbound Entra/token-service,
model/embedding, Search, Fabric SQL/MCP and Copilot Studio paths. Confirm TLS,
DNS, SQL reachability, SaaS endpoint support and managed identity access from the
actual hosted runtime. A successful test from an administrator's laptop is not
evidence for the host network.

Validate regional availability, quotas and data movement, including model deployment
type and Fabric capacity region. User prompts, permitted catalogue text and analytic
questions can contain business data even when saved tool results are filtered.
Apply residency, retention, DLP and AI-tool policies accordingly.

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
