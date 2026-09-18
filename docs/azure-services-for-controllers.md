# Azure and Microsoft services explained for controllers

This is the service inventory for both [.NET and Python](../README.md#choose-an-implementation).
It explains purpose, data and ownership without treating programming libraries as
additional cloud resources. See [architecture](architecture-diagram.md) for the
diagram and [availability](availability-and-support.md) for official sources.

## Services used by development

| Service | Purpose and data handled | If unavailable or misconfigured | Primary administrative owner |
| --- | --- | --- | --- |
| Teams / Microsoft 365 Copilot | Employee chat, sign-in UX, answers and clickable clarification cards | Users cannot reach or use the app | Microsoft 365 administrators |
| Azure Bot Service | Routes messages to the native Activity endpoint and replies to the conversation; each implementation has its own Bot | Requests, OAuth or reply delivery fail even if agent compute is healthy | Application/platform team |
| Microsoft Foundry account/project | Organizes hosted applications, identity, model connections and platform state | Agent operations, inference or state access can fail | Azure AI/platform team |
| Foundry Hosted Agents | Runs Activity handlers, Agent Framework execution and finance tools in the same hosted application | No application processing or finance answers | Application/platform team |
| Foundry-managed durable state | Stores caller-isolated filtered conversation/clarification memory inside the project | Follow-up context cannot be safely loaded or saved | Application owner and Foundry administrator |
| Azure OpenAI model deployments | GPT-5.4-mini selects a finance tool; text-embedding-3-large encodes terminology; constrained reranking may assist resolution | Routing or fuzzy terminology discovery fails; the model never substitutes fabricated statement figures | AI platform owner |
| Azure AI Search | Rebuildable metadata projection for keyword/vector/semantic retrieval, not financial fact storage | Fuzzy discovery fails; authorization and exact catalogue rules must still be respected | Search administrator and data publisher |
| Microsoft Entra ID | Authenticates applications and employees; enables delegated on-behalf-of token exchange | Sign-in or downstream access fails | Identity/security team |
| Managed identities and Azure RBAC | Authorize runtime model/Search access without service passwords for those hops | Model/Search requests return authorization failures | Azure platform/security team |
| Fabric capacity, workspace, lakehouse and SQL endpoint | Authoritative finance facts, catalogue metadata, authorized scope and fixed statement queries | Statements or catalogue checks fail; paused capacity is not missing finance data | Fabric/data engineering team |
| Fabric Data Agent MCP | Published analytical endpoint called with the signed-in user's permissions | Exploratory questions cannot complete; statements use their own SQL path | Fabric Data Agent owner |
| Copilot Studio KPIpedia | Sourced KPI definitions and follow-up conversation | Definitions cannot be delivered; it is not the statement calculator | Power Platform/knowledge owner |

**Availability:** Foundry Hosted Agents and the other listed core services are GA.
Foundry-managed durable state is a **Public Preview feature with no SLA**, and
Activity publishing uses a **Preview API**. Fabric Data Agent **Standard runtime
is GA**, but the runtime selected by the published application endpoint has not
been independently verified. SDK package status is recorded only in the language guides.

## Planned staging and production services

Staging and production are separate, undeployed designs. Each needs its own
identities, configuration, data access and state boundary.

| Service/control | Purpose | Required evidence before use |
| --- | --- | --- |
| Cosmos DB for NoSQL — **GA** | Store filtered finance state outside Foundry in a dedicated account per environment | Implement/version-test the state adapter, MI data-plane RBAC, ETags, TTL, backup and restore |
| Application Insights — **GA** | Application request/dependency timing, failures and traces | Confirm actual ingestion, alerts and privacy filters; do not capture tokens or permissioned result payloads |
| Log Analytics — **GA** | Retain/query diagnostic logs under approved access and retention policies | Agreed retention, region, operator permissions and tested incident queries |
| Key Vault — **GA** | Protect remaining secrets/certificates and stable state-key material | Least-privilege identity access, expiry monitoring and tested rotation procedures |
| Virtual Network, Private Link and Private DNS — **GA services** | Constrain supported network paths and resolve private endpoints | End-to-end validation for Activity ingress, model/Search/state access and Fabric/Copilot Studio egress |

Application Insights is the application telemetry experience; Log Analytics is
the workspace used to retain and query logs. Key Vault controls secret storage,
not a user's finance authorization. A drawn private boundary does not prove that
all connected SaaS paths support private connectivity.

## Concepts that are not extra Azure resources

- **Microsoft Agent Framework** is the execution library. It owns executable tools,
  sessions and middleware; the hosting service runs the process.
- **MCP** is the integration protocol used for Fabric exploration.
- **Adaptive Cards** is the presentation format for application-issued choices.
- **Managed identity** is a service identity, not a delegated employee identity.
- **Native work queue and OAuth continuation** live in the host process. They are
  not durable in-flight storage and cannot recover accepted work after a restart.
- **Development durable state** is a feature inside the existing Foundry project.
  It does not require a customer Storage account or Cosmos account.

GA describes the availability of a specific service or feature, not production
approval of this application. Review the [administration requirements](it-admin-catalogue.md)
and [acceptance evidence](operations.md) before promoting it.
