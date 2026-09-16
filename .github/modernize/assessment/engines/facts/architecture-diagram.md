# Architecture Diagram

ZavaFinance separates channel delivery from finance-agent execution. The channel provides the Teams and Microsoft 365 Copilot entry point, while a Foundry hosted agent provides reusable routing, identity isolation, and delegated access to finance systems.

## Application Architecture

<!-- mermaid-checked: no \n, no em-dash/en-dash, no {} in labels, subgraphs are id["label"], arrows are -->|"label"|, all subgraphs closed by end, ids unique -->
```mermaid
flowchart TD
    subgraph ClientLayer["Client Layer"]
        TeamsClient["Microsoft Teams"]
        M365Client["Microsoft 365 Copilot"]
        ApiClient["Responses API Client"]
    end
    subgraph ChannelLayer["Channel Layer - Azure Functions"]
        BotService["Azure Bot Service"]
        ChannelHost["ZavaFinance Channel"]
        DurableFlow["Durable Task Orchestration"]
        ChannelState[("Private Blob Storage")]
    end
    subgraph FoundryLayer["Agent Layer - Microsoft Foundry"]
        FoundryGateway["Foundry Responses Gateway"]
        AgentHost["ZavaFinance Hosted Agent"]
        AgentModel["Agent Model - Native Function Calling"]
        AgentState[("Foundry State Store")]
        Resolver["In-process terminology resolver"]
        Search["Azure AI Search - hybrid and semantic"]
        ResolverModel["Embedding and constrained candidate selection"]
    end
    subgraph FinanceLayer["Finance Services"]
        KpiTool["KPI Information Tool"]
        StatementTool["Statement Tool"]
        ExploreTool["Finance Exploration Tool"]
        CopilotStudio["Copilot Studio Agent"]
        FabricSql[("Fabric Lakehouse SQL")]
        FabricAgent["Fabric Data Agent MCP"]
        Catalog[("Fabric versioned catalogue and leaf scopes")]
        Publisher["Metadata publication notebook and script"]
    end
    subgraph IdentityLayer["Identity and Observability"]
        Entra["Microsoft Entra ID"]
        Monitor["Application Insights"]
    end

    TeamsClient -->|"user message"| BotService
    M365Client -->|"user message"| BotService
    BotService -->|"Activity Protocol"| ChannelHost
    ChannelHost -->|"acknowledge and schedule"| DurableFlow
    ChannelHost -->|"pending turn and reference"| ChannelState
    DurableFlow -->|"resume conversation"| ChannelHost
    ChannelHost -->|"Responses API and user assertion"| FoundryGateway
    ApiClient -->|"Responses API and user assertion"| FoundryGateway
    FoundryGateway -->|"forward client headers"| AgentHost
    AgentHost -->|"validate assertion"| Entra
    AgentHost -->|"load and save session"| AgentState
    AgentHost -->|"question and native tool schemas"| AgentModel
    AgentModel -->|"function name and arguments"| AgentHost
    AgentHost -->|"execute definition call"| KpiTool
    AgentHost -->|"execute statement call"| StatementTool
    AgentHost -->|"execute analysis call"| ExploreTool
    KpiTool -->|"delegated request"| CopilotStudio
    StatementTool -->|"raw KPI, organization and period"| Resolver
    Resolver -->|"delegated visible metadata and active release"| Catalog
    Resolver -->|"MI-authenticated candidate retrieval"| Search
    Resolver -->|"query embedding and allowed candidates only"| ResolverModel
    Resolver -->|"validated canonical IDs and fixed SQL"| FabricSql
    Resolver -->|"pending clarification bound to caller"| AgentState
    Resolver -->|"typed options or deterministic answer"| AgentHost
    Publisher -->|"stage immutable metadata"| Catalog
    Publisher -->|"publisher identity for catalogue embeddings"| ResolverModel
    Publisher -->|"publish and verify versioned index"| Search
    ExploreTool -->|"delegated MCP"| FabricAgent
    ChannelHost -.->|"telemetry"| Monitor
    AgentHost -.->|"telemetry"| Monitor
    ChannelHost -->|"proactive answer or clarification card"| BotService
    BotService -->|"delivered response"| TeamsClient
    BotService -->|"delivered response"| M365Client
```

### Technology Stack Summary

| Layer | Technology | Version | Purpose |
|---|---|---:|---|
| Clients | Microsoft Teams, Microsoft 365 Copilot, Responses API | Current service | User-facing and direct API entry points |
| Channel | .NET, Azure Functions isolated worker | .NET 10, Functions v4 | Bot authentication, identity acquisition, acknowledgement, scheduling, and proactive delivery |
| Channel workflow | Durable Task Scheduler | 1.25.0 client and worker | Executes slow turns outside the Bot Service response timeout with retries |
| Channel state | Azure Blob Storage | Microsoft.Agents.Storage.Blobs 1.8.77 | Stores pending turns, conversation references, and idempotency records |
| Hosted agent | Microsoft Foundry hosted agent | Azure.AI.AgentServer.Responses 1.0.0-beta.8 - Preview | Exposes the Responses API handler and runs the agent container |
| Native function calling | Microsoft Agent Framework | Microsoft.Agents.AI 1.21.0 - GA | Model selects a tool; host validates and executes it without model answer synthesis |
| Agent state | Foundry State Store | Platform managed | Stores per-user routing state and downstream conversation handles |
| Identity | Microsoft Entra ID and MSAL OBO | Microsoft.Identity.Client 4.89.0 | Validates user assertions and obtains delegated downstream tokens |
| KPI knowledge | Copilot Studio agent | Microsoft.Agents.CopilotStudio.Client 1.8.77 - GA | Returns sourced KPI definitions and calculations |
| Structured finance | Fabric lakehouse SQL analytics endpoint | Microsoft.Data.SqlClient 6.1.4 - GA | Executes parameterized KPI statement queries under user permissions |
| Exploratory finance | Fabric data agent over MCP | MCP 2025-06-18 | Answers open-ended finance questions under user permissions |
| Terminology | In-process C# resolver | Same hosted deployment | Exact aliases, ambiguity handling, strict dates and candidate validation before fixed SQL |
| Retrieval | Azure AI Search | GA hybrid/vector and semantic APIs | Dedicated, versioned metadata index; runtime read MI separate from publisher write identity |
| Catalogue publication | Fabric Spark notebook and PowerShell | Fabric REST v1 | Builds additive hierarchy/scopes, verifies unchanged facts, activates only after index verification |
| Clarification UX | Adaptive Cards and shared reply contract | Channel presentation | Agent returns numbered text plus typed options; Channel sends one attachment-only card with numbered choices, fallback and hierarchy paths; no SQL from card input |
| Observability | OpenTelemetry and Azure Monitor | Microsoft.OpenTelemetry 1.0.7 | Emits channel and hosted-agent telemetry |

### Data Storage & External Services

The channel uses private Blob Storage for pending turns, proactive conversation references, and idempotency records required by Durable Task. The hosted agent keeps routing history and downstream conversation handles in the platform-managed Foundry State Store under a key derived from validated tenant, user, and conversation identity. Finance data is read from Fabric through delegated SQL or a stateless MCP data-agent call, while KPI definitions come from a stateful Copilot Studio conversation. Microsoft Entra ID supplies signing metadata and On-Behalf-Of tokens; user assertions and access tokens are not stored in orchestration state.

### Key Architectural Decisions

- The channel and hosted agent are separate hosts so the same finance agent can serve Teams, Microsoft 365 Copilot, and direct Responses API callers.
- The channel acknowledges quickly and completes work through Durable Task because downstream calls can exceed the Bot Service timeout.
- Identity is fail-closed: the hosted agent validates the forwarded assertion before deriving a per-user session key or requesting delegated tokens.
- Native function calls are executed by the host. Tool answers are returned verbatim and never fed back to the model; history records content-free result markers instead.
- `get_statement` resolves terminology locally; Search retrieves candidates but never supplies SQL or financial figures.
- Fabric is authoritative for metadata and fact visibility. Search MI access does not inherit user SQL security; revalidate candidates with delegated SQL before exposing them or model-selecting.
- Branches cover distinct region/department pairs. Ratios are recomputed from components; global scopes and regional branches must not double count.
- Pending clarification is bound to caller, conversation, request and the full release binding (catalogue version, Search index, embedding deployment/dimensions). A stale/reset/forged selection cannot execute a financial query.
- This diagram describes the resolver update. Deployment and live acceptance evidence are recorded separately, not inferred from the diagram.

## Component Relationships

<!-- mermaid-checked: no \n, no em-dash/en-dash, no {} in labels, subgraphs are id["label"], arrows are -->|"label"|, all subgraphs closed by end, ids unique -->
```mermaid
flowchart LR
    subgraph cPresentation["Channel Presentation"]
        cMessages["MessagesFunction"]
        cChannel["OrchestratorChannel"]
        cHealth["HealthFunction"]
    end
    subgraph cWorkflow["Durable Workflow"]
        cScheduler["Turn Scheduler"]
        cTurn["TurnOrchestrator"]
        cActivities["OrchestratorActivities"]
    end
    subgraph cAgent["Hosted Agent"]
        cHandler["Response Handler"]
        cValidator["User Assertion Validator"]
        cSessionKeys["Session Key Provider"]
        cRouter["OrchestratorAgent"]
    end
    subgraph cTools["Finance Tools"]
        cKpi["KpiInfoTool"]
        cStatement["StatementTool"]
        cResolver["Local terminology resolver"]
        cExplore["ExploreFinanceTool"]
    end
    subgraph cData["Data and Integration"]
        cAgentStore["FoundrySessionStore"]
        cChannelStore["Channel Session and Delivery Store"]
        cHostedClient["Foundry Hosted Agent Client"]
        cCopilotFactory["Copilot Studio Client Factory"]
        cStatementQuery["FabricStatementQuery"]
        cDataAgent["FabricDataAgentClient"]
        cTokenProvider["Downstream Token Provider"]
    end

    cMessages -->|"authenticated activity"| cChannel
    cChannel -->|"pending, answer-ready, delivered"| cChannelStore
    cChannel -->|"schedule"| cScheduler
    cScheduler -->|"start instance"| cTurn
    cTurn -->|"retry activity"| cActivities
    cActivities -->|"resume proactive turn"| cChannel
    cChannel -->|"invoke agent"| cHostedClient
    cHostedClient -->|"Responses request"| cHandler
    cHandler -->|"validate first"| cValidator
    cHandler -->|"derive isolation key"| cSessionKeys
    cHandler -->|"run question"| cRouter
    cRouter -->|"load and save typed history"| cAgentStore
    cRouter -->|"execute definition route"| cKpi
    cRouter -->|"execute statement route"| cStatement
    cRouter -->|"execute exploration route"| cExplore
    cKpi -->|"create client"| cCopilotFactory
    cKpi -->|"request delegated token"| cTokenProvider
    cStatement -->|"resolve or clarify"| cResolver
    cResolver -->|"validated hierarchy scope"| cStatementQuery
    cStatementQuery -->|"request delegated token"| cTokenProvider
    cExplore -->|"ask open question"| cDataAgent
    cDataAgent -->|"request delegated token"| cTokenProvider
    cKpi -->|"return exact answer"| cRouter
    cStatement -->|"return exact answer"| cRouter
    cExplore -->|"return exact answer"| cRouter
```

### Component Inventory

| Component | Layer | Type | Responsibility |
|---|---|---|---|
| `MessagesFunction` | Channel presentation | HTTP-triggered Azure Function | Authenticates and validates inbound Activity Protocol requests |
| `OrchestratorChannel` | Channel presentation | Agent application | Resolves caller identity, acknowledges messages, schedules work, and sends proactive answers |
| `HealthFunction` | Channel presentation | HTTP-triggered Azure Function | Exposes channel health status |
| `DurableTaskOrchestratorTurnScheduler` | Durable workflow | Scheduler | Starts a deterministic orchestration for each pending turn |
| `TurnOrchestrator` | Durable workflow | Durable orchestrator | Runs the processing activity with bounded retries |
| `OrchestratorActivities` | Durable workflow | Durable activity | Resumes the conversation, invokes slow work, sends cached replies, and records confirmed delivery |
| `FoundryHostedAgentClient` | Data and integration | HTTP client | Calls Foundry with workload authorization and a separate forwarded user assertion |
| `ZavaFinanceResponseHandler` | Hosted agent | Responses API handler | Reads input, validates identity, creates OBO context, and runs native function selection and host execution |
| `UserAssertionValidator` | Hosted agent | Security service | Validates assertion signature, issuer, audience, lifetime, tenant, and user claims |
| `SessionKeyProvider` | Hosted agent | Identity service | Derives the per-user, per-conversation state key |
| `OrchestratorAgent` | Hosted agent | Routing service | Uses the model to select one route, executes it, and persists routing state |
| `KpiInfoTool` | Finance tools | Tool | Retrieves KPI definitions from Copilot Studio |
| `StatementTool` | Finance tools | Tool | Resolves scope and period, then calculates structured finance statements |
| Local terminology resolver | Hosted agent | In-process service | Reads authorized metadata, preserves ambiguity, uses Search as needed and validates selections |
| `ExploreFinanceTool` | Finance tools | Tool | Sends open-ended questions to the Fabric data agent |
| `FoundrySessionStore` | Hosted agent | Typed state store | Stores bounded routing history, KPI context and KPIpedia conversation handle |
| Channel session and delivery store | Channel | State store | Stores platform conversation IDs and turn delivery lifecycle with cached answers |
| `CopilotStudioClientFactory` | Data and integration | Client factory | Creates a delegated Copilot Studio client |
| `FabricStatementQuery` | Data and integration | SQL repository | Executes parameterized Fabric SQL and returns additive finance components |
| `FabricDataAgentClient` | Data and integration | MCP client | Discovers and calls the published Fabric data-agent tool |
| `IDownstreamTokenProvider` | Data and integration | Authentication abstraction | Supplies caller-bound delegated tokens inside the hosted agent |
