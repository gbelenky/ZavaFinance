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
- [Customer IT readiness and installation guide (PDF, draft)](docs/customer-it-installation-guide.pdf) - edition 1.3, 5 October 2026: outstanding delivery dependencies and closure owners, installation map, settings/RBAC, customer-admin Entra handback checklist, nine portal screenshots with source identifiers and personal data masked, Entra/Bot setup, data seeding versus estate migration, and environment architecture; [editable source](docs/customer-it-installation-guide.md), [Entra screenshot walkthrough](docs/customer-it-installation-guide.md#22-entra-administrator-screenshot-reference). For IT readiness review, not deployment sign-off until the delivery gaps and clean-workstation acceptance are closed. Resource/channel setup is portal-first; resolver seeding uses the reviewed [script pack](docs/zavafinance-fabric-seeding.zip).
- [Customer IT catalogue: services, ownership, identity and network policies](docs/it-admin-catalogue.md)
- [Price and consumption calculator (offline HTML)](docs/price-consumption-calculator.html) - download and open in a modern browser. Starts in **Controller view**: choose build-and-try, test-before-release and daily-use areas; enter team size, working days, conversations and questions to see monthly/annual budgets, per-person averages and plain-language cost drivers. **Detailed IT view** exposes the same scenario's Development/Staging/Production assumptions and rates; switching views never resets them. Export/import JSON to share the scenario, download a CSV for the current view, or print it. Indicative USD estimates, not a quote: actual prices depend on your Microsoft agreement. Staging/Production sizing is assumed; Copilot Studio/KPIpedia pricing is excluded. No server or credentials required. Formula tests: `node --test tools\test-consumption-calculator.cjs`.
- [Customer onboarding and local development](docs/development.md)
- [Deployment runbook](docs/deployment.md)
- [Resolver catalogue publication](docs/resolver-data.md)
- [Recipient guide: reproduce Fabric configuration and data](docs/fabric-import-guide.md) - handover contents, target tenant/capacity/settings/access, clean-workstation prerequisites, complete reference mapping, scripted import and reconciliation, and manual Data Agent/Search/application release gates. The approved data export is delivered separately, outside Git.
- [Fabric estate migration: export, isolated import and full readback](docs/fabric-estate-migration.md) - preserves actual Lakehouse data/retained Delta history, empty directories and API-returned definitions via public `fab api`, not native CLI import/export or demo reseeding. DataAgent definition transfer is Preview opt-in; unsupported items, workspace folders and schedules block full export. Workspace roles/SQL metadata are not a complete security backup. Validate restored data before Search/release activation; real rehearsal, finance/security acceptance and manual cutover remain required.
- [Availability and support boundaries](docs/availability-and-support.md)
- [Operations and acceptance evidence](docs/operations.md)

**Operating boundary:** accepted work and OAuth continuations are process-owned;
a restart can interrupt a turn and require a retry. Development uses Foundry-managed
durable state (**Public Preview, no SLA**) for filtered conversation memory, not
in-flight recovery. Staging and production are separately planned with **GA Cosmos
DB for NoSQL**; their state adapter and deployments are not implemented.

Foundry Hosted Agents is **GA**. The selected Activity API is **Preview**; package
support is documented separately in each language guide.
