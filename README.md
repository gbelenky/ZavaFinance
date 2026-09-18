# ZavaFinance

A finance agent for **Microsoft Teams and Microsoft 365 Copilot**, with independent
**.NET** and **Python** implementations. Both run in Microsoft Foundry using native
Activity hosting and Microsoft Agent Framework.

The agent selects at most one finance tool per turn and returns its answer verbatim:

| Tool | Purpose |
| --- | --- |
| `get_kpi_info` | Sourced KPI definitions from Copilot Studio KPIpedia |
| `get_statement` | Authorized metadata resolution, fixed parameterized Fabric SQL and application-owned decimal calculations |
| `explore_finance` | Exploratory analysis through the published Fabric Data Agent MCP endpoint |

Finance requests use the signed-in user's delegated permissions. Models, embeddings
and Azure AI Search use the runtime managed identity.

## Choose an implementation

| Implementation | Setup, tests and deployment | Foundry service name |
| --- | --- | --- |
| .NET 10 | [src/dotnet/README.md](src/dotnet/README.md) | `zavafinance-one` |
| Python 3.13 | [src/python/README.md](src/python/README.md) | `zavafinance-one-python` |

Each has a distinct Bot registration, installable app package and caller-isolated
state. An authorized finance OAuth client may be shared; the Bots are not shared.
Choose an explicit service when deploying.

## Shared documentation

- [Architecture and environment diagrams](docs/architecture-diagram.md)
- [Activity, authentication and tool execution flows](docs/swimlane-diagram.md)
- [Finance ownership and financial controls](docs/finance-controller-architecture.md)
- [Services explained for controllers](docs/azure-services-for-controllers.md)
- [IT administration, identity and environment requirements](docs/it-admin-catalogue.md)
- [Deployment runbook](docs/deployment.md)
- [Resolver catalogue publication](docs/resolver-data.md)
- [Availability and support boundaries](docs/availability-and-support.md)
- [Operations and acceptance evidence](docs/operations.md)

**Operating boundary:** accepted work and OAuth continuations are process-owned;
a restart can interrupt a turn and require a retry. Development uses Foundry-managed
durable state (**Public Preview, no SLA**) for filtered conversation memory, not
in-flight recovery. Staging and production are separately planned with **GA Cosmos
DB for NoSQL**; their state adapter and deployments are not implemented.

Foundry Hosted Agents is **GA**. The selected Activity API is **Preview**; package
support is documented separately in each language guide.
