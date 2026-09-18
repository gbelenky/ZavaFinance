# Azure and Microsoft services explained for controllers

**Audience:** finance/controller project and code owners, not Azure administrators.
**Purpose:** understand why each service is present, what it handles, and what to discuss with IT.
This describes the documented demo baseline, not a new inspection of live Azure resources.

Read this alongside the [finance architecture guide](finance-controller-architecture.md).
The Python code will be provided separately. Service responsibilities and financial controls
are the focus here, not implementation-language changes.

**Status checked: 17 September 2026.** **GA** means generally available, not that the particular
deployment has passed production acceptance. **Preview** applies to a named feature/API;
**prerelease** applies to a named software package. **Legacy** is a model lifecycle stage,
not Preview or retirement. A GA service can still be accessed through a prerelease library.
**Conditional** and **optional** resources must not be mistaken for resources already deployed.
See the [availability and support register](availability-and-support.md) for current official
sources and the [IT-admin catalogue](it-admin-catalogue.md#availability-and-support-status)
for release evidence, policy exceptions and approval requirements.

## Original deployment versus Zava Finance One

The service descriptions below explain the **original two-host baseline**. One is a
separate experiment on `activity-protocol`; it does not require all those resources.

| Service or capability | Purpose in One |
| --- | --- |
| Azure Bot Service | Separate Bot delivers directly to the agent's native Activity endpoint |
| Foundry hosted agent | Runs both channel handlers and the existing finance engine |
| Functions, App Service plan, Channel Storage and Durable Task Scheduler | **Not used by One**; original resources remain in place for the original app |
| Foundry state store | Separate finance conversation memory, not an executing-task checkpoint |
| Native SDK queue and OAuth state | Process-local work and sign-in continuation; no crash recovery or cross-instance sharing |
| Routing/reranking model | GPT-5.4-mini (**GA**) instead of the original GPT-4.1-mini (**Legacy**) |
| Search, embeddings, Fabric, KPIpedia and user permissions | Same dependencies and finance controls as the original |
| Monitoring | Native host/application telemetry; no original Channel/DTS delivery events to inspect |

One's additional Activity adapter is an **unreleased, unofficial source build**, not a
new GA library. It can process slow requests, but a process restart can lose accepted
work and the user must retry. Initial silent SSO is still unresolved; interactive
sign-in and a later cached-token request working do not prove SSO. See the
[One contract and acceptance status](../src/ZavaFinance.One/README.md).

## 1. Services that run the conversation

### Azure Bot Service

**Used; GA service.** Think of it as the messaging switchboard, not the finance expert.

The template still selects a **preview management API**; that is a separate approval item.
The retired Bot Framework SDK is not the Microsoft 365 Agents SDK used by this Channel.

- **Purpose in ZavaFinance:** connects Teams/Microsoft 365 messages to the Channel and carries
  replies back to the right conversation. Its configured sign-in connection supports user login.
- **Handles:** messages, attachments such as clarification cards, and conversation addresses.
  It does not decide which KPI "margin" means or calculate a statement.
- **If unavailable or misconfigured:** the user may not reach the application or receive its reply,
  even when the finance Agent itself is healthy.
- **Ownership:** finance approves the user experience and application audience; IT manages bot
  registration, messaging endpoint, sign-in configuration and channel enablement.

The template uses a preview Bot **management API version**. That is a policy-review item,
not a claim that the entire Bot Service is Preview.

### Azure Functions

**Used; GA.** This is the place where the **Channel application code** runs.

- **Purpose in ZavaFinance:** receives the bot request, handles sign-in, acknowledges quickly,
  starts background work, calls the Agent and creates clickable cards from structured choices.
- **Handles:** signed-in requests and delivery state, including answers that must be sent later.
  It is not the host of the finance calculator; that code runs in the Foundry-hosted Agent.
- **If unavailable:** the chat entry point fails. A healthy Foundry Agent does not replace it.
- **Ownership:** finance owns Channel behavior and tests; the application/platform team manages
  deployment, configuration, health checks and runtime support.

### App Service plan

**Used; GA.** This is the reserved computing capacity underneath the Function App.

- **Purpose in ZavaFinance:** provides the Linux workers, memory and scaling settings on which
  the Channel runs. The Function App is the application; the plan supplies its compute.
- **Handles:** running processes, not a separate business database.
- **If undersized or unavailable:** the Channel may be slow or unable to process requests.
- **Ownership and cost:** IT sizes it from measured demand; finance approves cost/availability
  tradeoffs. This solution uses a **dedicated plan**, not a per-request Consumption Functions
  plan. Its provisioned capacity can cost money even with no conversations.

### Durable Task Scheduler

**Used; GA.** This is the reliable background-work coordinator.

- **Purpose in ZavaFinance:** lets a slow finance request continue after the chat endpoint has
  acknowledged it, with saved progress and controlled retries. Some downstream calls take minutes.
- **Handles:** orchestration progress and identifiers. The application deliberately keeps user
  tokens and finance answers out of its orchestration payloads.
- **If unavailable:** work can remain pending rather than reaching a completed response.
- **Ownership:** the application team owns workflow/retry behavior; IT manages scheduler access,
  capacity and a separate **task hub** for each environment. A task hub is a named boundary for
  a set of workflows, not a finance reporting unit.

Durable Functions is the programming extension; Durable Task Scheduler is the separate Azure
backend service. It coordinates execution; the Function App still executes Channel code.
Its internal workflow storage does not remove the application's need for Blob Storage.
[Microsoft's scheduler overview][scheduler]

### Azure Storage

**Used; GA.** This is the Channel's persistent storage, not the authoritative finance lakehouse.

- **Purpose in ZavaFinance:** Blob containers hold pending-turn references, cached answers/cards
  and delivery records. The Functions host also needs storage for its own coordination.
- **Handles:** cached replies **can contain financial information**. Apply data retention and
  access controls; do not classify this account as harmless infrastructure-only data.
- **If unavailable:** startup, state persistence or delivery/retry can fail. Deleting the account
  is not a safe way to reset a conversation.
- **Ownership:** finance agrees retention/recovery requirements; IT manages private access,
  storage permissions, resilience settings and operational recovery.

The baseline exposes storage's blob, queue and table endpoints privately for platform needs.
This does **not** mean a separate business messaging queue or Azure Storage durable backend is
in use: Durable Task Scheduler is the orchestration backend. The provisioned deployment container
is also not the finance data store; the current Channel ZIP deployment uses the app filesystem.

## 2. Services that run the finance Agent and find terminology

### Microsoft Foundry account and project

**Used; GA account/project capabilities and GA Hosted Agents service.**
Think of the account/project as the managed home for the AI application.

- **Purpose in ZavaFinance:** groups the Agent, model access, configuration and access boundaries.
  The **account** is the Azure resource; the **project** organizes the AI application's assets
  and exposes its project endpoint.
- **Handles:** AI application assets and configuration. It is not a replacement for Fabric.
- **If misconfigured:** the Channel may be unable to invoke the Agent, or the Agent may be unable
  to reach its models and connected services.
- **Ownership:** finance owns the Agent's intended behavior; the Foundry administrator manages
  project access, approved regions, deployment permissions and model capacity.

Creating an account and project alone does not install a working finance Agent.

### Foundry hosted Agent runtime and state store

**Used; GA hosting service, with prerelease SDK integration in this application.**
This runs **your application code**. In the original app, the selected Responses adapter and its transitive Core
library are beta packages, including the integration used to access platform state. Do not
confuse those libraries with the availability of the managed hosting service.

- **Purpose in ZavaFinance:** accepts requests through the Responses API and executes the
  finance Agent, including its three tools, in-process resolver and deterministic calculator.
- **Handles:** caller-isolated routing state, downstream conversation handles and pending
  clarification state in the platform state store. This is separate from the Channel's
  cached delivery records in Blob Storage.
- **If unavailable:** the Channel may acknowledge a request but cannot obtain its finance answer.
- **Ownership:** finance owns and tests the code and controls; IT supports deployment versions,
  runtime identity, networking, compute allocation and rollback.

The hosted **Agent is not the model**. The Agent is the controlled program; the model is one
service that program calls. Hosted compute, model use and retained state have different operating
and cost considerations. [Hosted-agent concepts][hosted]

### Azure OpenAI model deployments in Foundry

**Used; model lifecycle must be checked separately from the GA Azure OpenAI service.**
A deployment is an approved, callable instance of a model with a deployment name, version and
capacity limits.

| Deployment in the current solution | Current published lifecycle | Purpose | What it does not do |
|---|---|---|---|
| `gpt-4.1-mini` | **Legacy**, version `2025-04-14`; retirement **14 April 2027** | Selects a finance tool and arguments; can rerank already-authorized terminology candidates | Does not write the statement SQL or perform the statement's financial arithmetic |
| `text-embedding-3-large` | **GA**, version `1` | Converts catalogue wording and search text into numeric representations of meaning | Does not calculate revenue or encode permission to access a reporting unit |

Legacy means newer models are available, not that existing calls have stopped working. IT and
finance should plan and evaluate a replacement before retirement, not silently change the
model during a documentation update. Confirm the actual deployed version and current dates
against the [official lifecycle sources](availability-and-support.md#model-lifecycle).

- **Handles:** questions, tool descriptions and permitted catalogue text relevant to its role.
  An **embedding** is a list of numbers for similarity matching, not a financial amount.
- **If unavailable or throttled:** routing or semantic terminology discovery can fail. The
  application must surface that failure rather than fabricate an answer.
- **Ownership and cost:** finance validates behavior and search quality; IT manages approved
  models, versions, regions and quotas. Usage is metered separately from hosted Agent compute.
- **Important dependency:** the current catalogue uses 1536 embedding dimensions. The model,
  dimensions and Search index must stay compatible; changing them requires a reviewed catalogue
  release, not an isolated configuration edit.

These are model deployments in the configured Foundry resource. Do not assume that each model
requires another standalone Azure OpenAI account.

### Azure AI Search

**Used; GA keyword, vector and semantic features in this solution.**
Think of it as a searchable terminology directory.

- **Purpose in ZavaFinance:** proposes KPI and organization identifiers when wording does not
  resolve uniquely through exact Fabric metadata. It helps with paraphrases and similar meanings.
- **Stores:** a versioned copy of catalogue names, aliases, descriptions, paths and embeddings.
  **No financial fact rows are indexed.** The authoritative catalogue remains in Fabric.
- **If unavailable:** search-backed discovery is unavailable; an already-unambiguous exact match
  can still use the Fabric metadata path, provided its other dependencies are healthy.
- **Ownership and cost:** finance curates vocabulary and confirms candidate quality; IT manages
  the dedicated Search service, network access, capacity and publisher/runtime permissions.
  Provisioned search capacity costs are not limited to moments when someone searches.

Search does not inherit Fabric's user permissions. Candidate visibility must be checked against
Fabric before disclosure, and semantic suggestions require user confirmation. The publication
process builds and verifies the index before activating it. It does not automatically reindex
on every transaction or ordinary financial-fact refresh.

## 3. Services that protect access and connectivity

### Microsoft Entra ID

**Used; GA.** This is the corporate identity and sign-in system.

- **Purpose in ZavaFinance:** establishes who the employee is and which application is calling.
  Application registrations, consent and a delegated token exchange let finance requests run
  with the signed-in employee's downstream permissions.
- **Handles:** identities, application registrations and authorization tokens, not finance facts.
  Tokens are confidential credentials, not fields to copy into logs or assistant prompts.
- **If misconfigured:** sign-in, token validation or downstream access fails. A user's name in a
  question does not bypass those checks.
- **Ownership:** finance defines the intended audience and information visibility; Entra/security
  administrators manage registrations, assignments, consent and credential lifecycle.

Entra sign-in is not a universal access grant. Fabric item/SQL permissions and Copilot Studio
sharing still apply separately.

### Managed identities and Azure role assignments

**Used; GA.** A managed identity is an application badge issued and maintained by Azure.

- **Purpose in ZavaFinance:** the Channel uses its identity for Foundry invocation, storage and
  scheduling. The Agent's dedicated runtime identity accesses Search and embeddings. The
  project infrastructure identity has a different job, such as pulling deployment images.
- **Handles:** service identity and narrowly scoped permissions, not employee entitlements.
  **RBAC** means role-based access control: who can do which action on which Azure resource.
- **If misconfigured:** a service receives an access-denied error even if the employee is signed in.
- **Ownership:** finance states which connections are required; IT assigns the smallest necessary
  roles. Runtime identities must not receive catalogue-publisher privileges as a shortcut.

Managed identities avoid service passwords for supported connections; they do not eliminate
every credential in the solution. The configured delegated sign-in exchange still needs a
securely managed application credential. Nor should an application identity replace the
employee's Fabric permissions. Detailed role assignments belong in the
[IT-admin catalogue](it-admin-catalogue.md), not in controllers' everyday code changes.

### Azure Virtual Network and subnets

**Used; GA.** This is the controlled network path between services.

- **Purpose in ZavaFinance:** gives the Channel an outbound path to private storage endpoints.
  Subnets divide that network into approved connection areas.
- **Handles:** network traffic, not a persistent store of finance records.
- **If misconfigured:** the application can appear healthy but fail to reach required storage.
- **Ownership:** IT/networking manages address ranges and routes. Controllers describe required
  service connections; they do not need to select IP ranges or disable firewalls to fix finance code.

Channel network integration does not automatically put the Foundry-hosted Agent on that network.
The current baseline is **not an entirely private end-to-end solution**.

### Azure Private Link and private endpoints

**Used for Channel storage; GA. Additional private service connections are design choices.**
A private endpoint gives a supported service a private network address.

- **Purpose in ZavaFinance:** allows the Channel to access Storage without opening that storage
  account's public network endpoint.
- **Handles:** the connection to a service, not another copy of its data. There are distinct
  private connections for storage's blob, queue and table endpoints.
- **If misconfigured:** storage can be unreachable even when its access roles are correct.
- **Ownership and cost:** IT provisions and approves each endpoint and its network path. Endpoints
  have their own usage/capacity charges; creating one is not equivalent to creating a database.

Private endpoints for Search or other services require a working path from the **actual caller**,
including the hosted Agent. They are not automatically present because storage is private.

### Azure Private DNS

**Used; GA.** This is the private network's address book.

- **Purpose in ZavaFinance:** resolves the usual storage service names to their private endpoint
  addresses. Applications can keep using service names rather than hard-coded private addresses.
- **Handles:** name-to-address records and links to networks, not business records.
- **If misconfigured:** a request may resolve to the blocked public endpoint, or fail to find the
  service at all. A correct private endpoint without correct DNS is not a complete connection.
- **Ownership:** IT manages DNS zones, network links and any corporate DNS forwarding.

## 4. Services that explain failures and performance

### Azure Monitor and Application Insights

**Used; GA core monitoring capabilities.** Azure Monitor is the monitoring platform;
Application Insights is its application-focused diagnostic capability.

- **Purpose in ZavaFinance:** shows request duration, exceptions, failed service calls and
  correlation between Channel and Agent operations. It helps answer "where did this request fail?"
- **Handles:** diagnostic events and metrics. The baseline avoids logging prompt/answer content
  and tokens by default; that policy must also be respected by new code.
- **If unavailable:** the application may still run, but investigating failures becomes harder.
  A deployment without usable diagnostics is not ready for operational handover.
- **Ownership:** finance/code owners instrument meaningful events and interpret business impact;
  operations maintains access, alert rules and incident response.

Telemetry is not the general ledger, a full conversation archive or proof that a KPI is correct.
Quality still requires finance reconciliation and acceptance tests. Creating an Application
Insights resource does not automatically implement every desired alert.

### Log Analytics workspace

**Used; GA.** This is the retained, searchable diagnostic log store.

- **Purpose in ZavaFinance:** stores the detailed telemetry associated with workspace-based
  Application Insights so authorized operators can query it across requests and services.
- **Handles:** log/trace records governed by access and retention settings. It is a different
  kind of workspace from a Fabric workspace.
- **If unavailable or retention is insufficient:** incident evidence may be missing or no longer
  searchable; this is not a reason to query the finance database for application exceptions.
- **Ownership and cost:** finance/security agree information handling and retention; operations
  manages access and log volume. Ingestion and retention influence cost.

In short: **Application Insights helps you understand the application's behavior; Log Analytics
retains and queries its detailed diagnostic records.** They are related, not duplicate finance
databases. [Microsoft's workspace-based Application Insights guidance][app-insights]

## 5. Connected Microsoft platforms

These are essential to the solution, but are not all independently provisioned Azure resources.
Azure RBAC alone does not configure their tenant settings, licensing or user permissions.

### Microsoft Fabric capacity

**Used; GA capacity service.** This is the compute budget that powers the Fabric workloads.

- **Purpose:** supplies capacity for the lakehouse, SQL and data-agent workloads. The demo uses
  the existing **F2**, not an additional finance database created for the resolver.
- **Handles:** execution capacity; it is not itself the table containing your accounts.
- **If paused or exhausted:** Fabric-backed work is unavailable or may be throttled. Pausing
  compute does not delete the underlying finance data.
- **Ownership:** finance agrees operating hours, peak reporting demand and cost; the Fabric
  administrator manages capacity and its pause/resume schedule.

Separate workspaces on one capacity can isolate permissions, but still share compute resources.

### Fabric workspace, lakehouse, OneLake and SQL analytics endpoint

**Used; GA capabilities.** These are different parts of the finance data platform:

| Part | Purpose in ZavaFinance | Finance-owner responsibility |
|---|---|---|
| Workspace | Organizes Fabric items and their access within an environment | Agree who may see/manage which finance items |
| Lakehouse and underlying OneLake storage | Hold finance fact/dimension tables and authoritative resolver metadata | Own data quality, reporting keys, hierarchy meaning and reconciliation |
| SQL analytics endpoint | Provides the table-reading interface used by fixed statement queries and authorized metadata reads | Approve source measures, user visibility and reference calculations |
| Publication notebook | Builds and stages the reviewed catalogue inside Fabric; it is a workload, not a separate Azure service | Review the metadata change and publication evidence |

The SQL endpoint is **not an Azure SQL Database server** to provision separately. Published table
changes must become visible there before a new catalogue release is activated. If the endpoint
is unavailable, Search cannot substitute for authoritative facts or authorization checks.

### Fabric Data Agent and MCP endpoint

**Used; GA Data Agent core, with runtime/integration-specific exceptions.**

The Standard runtime is GA; Preview runtime and Advanced NL2SQL/DAX remain Preview.
The GA Copilot Studio Fabric IQ Data MCP announcement does not establish support for this
application's older custom-client MCP route. Confirm the actual published runtime and endpoint
using the [Fabric feature boundaries](availability-and-support.md#fabric-feature-boundaries);
do not assume an existing published agent was automatically upgraded.

- **Purpose:** answers open-ended finance questions through the `explore_finance` tool, using
  the signed-in user's authorized data access. MCP is the connector protocol, not another database.
- **Handles:** analytical questions and the permitted data needed to answer them.
- **If unavailable:** exploration fails even if the deterministic statement path is healthy.
- **Ownership:** finance curates instructions and checks narrative arithmetic/evidence; the
  Fabric owner manages item sharing, publication, tenant settings and supported regions.

An exploratory answer is not automatically covered by the deterministic calculator's tests.

### Copilot Studio and KPIpedia

**Used; GA platform, with selected features and connections subject to approval.**

- **Purpose:** provides sourced KPI explanations through `get_kpi_info`. KPIpedia is the
  configured knowledge agent, not a fourth Azure compute host.
- **Handles:** definition questions, approved knowledge sources and its conversation context.
- **If unavailable:** KPI explanations fail; this does not necessarily prevent a fixed statement.
- **Ownership:** finance keeps definitions consistent with executable formulas; the Power Platform
  owner manages environment, licensing, authentication, user sharing, connectors and publication.

Changing a definition here does not change the statement calculator's executable formula.

### Microsoft Teams and Microsoft 365 Copilot

**Used as client entry points; GA products.**

- **Purpose:** provide the familiar place where employees ask questions and click choices.
- **Handle:** displayed questions, finance answers and Adaptive Cards, subject to the
  organization's Microsoft 365 information policies.
- **If the app is unpublished, unassigned or blocked:** users cannot find or use it even though
  the Azure services are running.
- **Ownership:** finance approves the experience and user audience; Microsoft 365 administrators
  manage application distribution and licensing. Test each intended client separately; the
  recorded M365 acceptance does not establish completed Teams acceptance.

## 6. Conditional and optional supporting services

### Azure Container Registry

**Conditional; GA.** This stores packaged application images for deployment, not finance facts.

Foundry code/image deployment may use a generated or selected registry and remote build
infrastructure. If that deployment uses a registry, the platform needs permission to pull the
image and the build publisher needs separate write permission. A failure can prevent deployment
or a new runtime from starting, even if an existing instance is still running.

IT must inventory the **actual** registry used by the selected deployment path. The
[registry access module](../infra/registry-access.bicep) grants access to an existing registry;
it does not prove a new registry was provisioned. Controllers own release acceptance, not
routine image-pull permissions.

### Azure Key Vault

**Optional; GA; not provisioned by the baseline templates.**

Key Vault can hold secrets and certificates when customer policy requires a dedicated secret
store. It does not replace managed identity, end-user permissions or finance data storage.
The current configuration still has securely managed application credentials; do not assume
they are already in Key Vault.

If adopted, IT must configure identity access, credential lifecycle and network reachability.
Finance/code owners must keep credentials out of code, logs and coding-assistant prompts.

### Subscription, resource groups and governance

These are management controls, not additional finance-processing services:

| Term | What controllers need to understand |
|---|---|
| Azure subscription | Billing, quota and policy boundary for Azure resources; owning this project does not require subscription administrator rights |
| Resource group | Logical grouping for lifecycle and access; deleting one can delete the resources it contains |
| Azure Policy | Organization rules that may allow, deny or enforce deployment settings; the application must fit the customer's policies |
| Tags | Labels such as owner, environment and purpose for accountability and cost allocation |
| Resource locks | Protection against accidental resource modification/deletion; not a substitute for user-data permissions |
| Cost Management budgets and alerts | Visibility and notifications about spending; a budget alert is not automatically a spending cap |

The catalogue specifies governance requirements; it is not evidence that every policy, lock,
budget or alert has already been configured. Dev, staging and production also need explicit
boundaries; the presence of templates does not mean all three environments exist.

## 7. Common questions

| Question | Answer |
|---|---|
| Why Functions **and** Foundry? | Functions runs the chat/delivery code; Foundry runs the reusable finance Agent code |
| Why Fabric **and** Search? | Fabric supplies authoritative data and visibility; Search proposes matching terminology |
| Why a language model **and** an embedding model? | One chooses tools/reranks candidates; the other represents text for similarity search |
| Why Scheduler **and** Blob Storage? | Scheduler coordinates background execution; Blob Storage preserves application replies and delivery state |
| Why Application Insights **and** Log Analytics? | Application diagnostics and retained log storage are complementary parts of monitoring |
| Does private networking replace sign-in? | No. Network reachability, application identity and user-data authorization are separate checks |
| Does pausing Fabric pause the whole bill? | No. Dedicated Channel compute, Search and other resources have separate meters; model calls and Microsoft 365/Power Platform licensing are separate considerations |
| Do we need Azure SQL, Cosmos DB, Service Bus, Redis, APIM or another vector database? | Not for this baseline. Do not add them merely because the solution has an Agent or needs state/search |
| Are Copilot, Claude, Agent Framework or Adaptive Cards extra Azure services here? | GitHub Copilot/Claude assist development; Agent Framework is a code library; Adaptive Cards is a presentation format. These are not additional Azure compute resources in this design |

For configurations, service dependencies, precise roles and customer policy review, hand IT the
[administration catalogue](it-admin-catalogue.md). For business behavior, calculations and
acceptance, return to the [finance architecture guide](finance-controller-architecture.md).
The resource definitions are in [main.bicep](../infra/main.bicep) and the independent
[Search module](../infra/search.bicep); this guide does not change or deploy them.

[scheduler]: https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler
[hosted]: https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents
[app-insights]: https://learn.microsoft.com/azure/azure-monitor/app/create-workspace-resource
