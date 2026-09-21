# Availability and support

Review date: **18 September 2026**. Service availability, feature/API status,
software package status and executed acceptance are separate questions.
This register describes the selected architecture, not a support guarantee.

Activity API and customer-networking clarification: **21 September 2026**.

## Service and feature register

| Component | Status | Application implication |
| --- | --- | --- |
| Microsoft Foundry Hosted Agents | **GA service** | Hosts both native Activity implementations |
| Microsoft Agent Framework core | **GA framework** | Actual dependency pins and integrations still require their own review |
| Foundry Activity messaging endpoint | **Preview-versioned API** (`2025-05-15-preview`) | Needs explicit approval; the separate Microsoft 365 publish operation uses `v1`, which does not make this Activity route GA |
| Foundry-managed durable state | **Public Preview; no SLA** | Development finance memory inside the existing project |
| Cosmos DB for NoSQL | **GA service** | Planned independently for staging and production; version-aware adapter not implemented |
| Azure Bot Service, Entra ID, Azure AI Search, Azure Monitor, Key Vault and networking services | **GA services** | Validate the selected API versions, features, regions and network configurations separately |
| Fabric lakehouse, ordinary SQL analytics endpoint queries and capacity | **GA core capabilities** | Fixed statement queries do not require generative reasoning |
| Fabric Data Agent core and Standard runtime | **GA** | The runtime of the currently published endpoint remains **unverified** |
| Fabric Data Agent Preview runtime, advanced NL2SQL/DAX and named Foundry integrations | **Preview capabilities where documented** | Do not infer support from Standard runtime's GA status |
| Copilot Studio | **GA service** | KPIpedia's publication, connector route and delegated access still require validation |

Changing state storage to Cosmos does not make the Activity API GA, change SDK
support, or add durable task execution. Persistent conversation memory cannot
resume a process-owned tool invocation or OAuth continuation.

For customer deployment, also resolve the
[networking and public Activity-route gates](it-admin-catalogue.md#foundry-private-networking-and-the-microsoft-365-public-route).
The current publishing design can expose only the Activity route publicly while
other Foundry routes remain private. General hosted-agent network documentation
does not certify this repository's exact native Activity/direct-code/private-egress/
managed-state combination; validate that combination before deployment sign-off.
The reviewed Fabric MCP documentation likewise does not establish a separate MCP
GA declaration merely because the Data Agent service is GA.

### Implementation package boundaries

SDK versions are intentionally documented beside their code:

- [.NET dependency/support contract](../src/dotnet/README.md#dependencies-and-support):
  the Activity transport is an **unsigned, unofficial, source-pinned public-source
  compatibility build**, not a supported GA transport.
- [Python dependency/support contract](../src/python/README.md#dependencies-and-support):
  a published **beta Activity adapter** and stable Core package with an
  **experimental state API**.

Neither a successful build nor a GA managed hosting service establishes support
for those selected APIs/packages. Capture direct and transitive package versions
in each release's SBOM; an installed build tool or extension version also needs
its own release record.

## Model lifecycle

| Selected use | Deployment/model | Review requirement |
| --- | --- | --- |
| Routing and constrained candidate reranking | `gpt-5.4-mini` — **GA model** | Read actual deployment version/SKU and recheck retirement schedule before release |
| Terminology embeddings | `text-embedding-3-large` — **GA model** | Match the active release's deployment and vector dimensions |

The development catalogue requests **1536 dimensions**. A deployment name is not
proof of its version, region or residency boundary. No comparative latency, cost
or accuracy advantage is assumed. KPIpedia and Fabric manage their own model
choices separately from ZavaFinance's outer routing model.

## Fabric publication gate

Verify the actual published Data Agent's runtime, endpoint and authentication
contract. Standard being the default for newly created agents does not establish
the runtime of an already published item. Likewise, an MCP response or a sourced
answer proves connectivity/delivery, not every analytical figure.

Use the current [MCP documentation](https://learn.microsoft.com/fabric/data-science/data-agent-mcp-server)
and test the configured route with the signed-in user. Do not silently replace
an endpoint, republish an agent, broaden consent or enable a Preview feature to
make a test pass.

## Approval evidence

For each implementation and environment, record:

1. Deployed agent version, runtime and exact artifact/hash.
2. Bot-to-Activity endpoint mapping, routing identity, OAuth resource and installed
   app package version.
3. Selected model version/SKU, package graph and API/feature exceptions.
4. Published Fabric runtime and successful delegated SQL, MCP and KPIpedia checks.
5. Fresh SSO, typed/clicked clarification, follow-ups, reset, two-user isolation
   and independently reconciled statement values.
6. State expiry/concurrency/privacy behavior, observed telemetry and operational
   handling of interrupted work.

See [operations](operations.md) for evidence already recorded. Local source,
local tests and cloud deployment are different acceptance boundaries.

## Official references

- [Agent Framework Foundry hosting](https://learn.microsoft.com/agent-framework/hosting/foundry-hosted-agent)
- [Hosted-agent runtime contract](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agent-contract)
- [Hosted Agents GA migration statement](https://learn.microsoft.com/azure/foundry/agents/how-to/migrate-hosted-agent-preview)
- [Direct source-code hosting](https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent-code)
- [Publish to Teams and Microsoft 365](https://learn.microsoft.com/azure/foundry/agents/how-to/publish-copilot-virtual-network)
- [Durable state store and preview limits](https://learn.microsoft.com/azure/foundry/agents/concepts/agent-state-store)
- [Cosmos DB for NoSQL overview](https://learn.microsoft.com/azure/cosmos-db/nosql/overview)
- [Azure OpenAI models](https://learn.microsoft.com/azure/foundry/openai/concepts/models)
- [Model lifecycle](https://learn.microsoft.com/azure/foundry/openai/concepts/model-retirements)
- [Fabric Data Agent concepts](https://learn.microsoft.com/fabric/data-science/concept-data-agent)
- [Fabric runtime selection and publication](https://learn.microsoft.com/fabric/data-science/data-agent-runtime)
- [Fabric Data Agent MCP](https://learn.microsoft.com/fabric/data-science/data-agent-mcp-server)
- [Fabric SQL source options](https://learn.microsoft.com/fabric/data-science/data-agent-sql-sources)
- [Fabric Foundry integration](https://learn.microsoft.com/fabric/data-science/data-agent-foundry)
