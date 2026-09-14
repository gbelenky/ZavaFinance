# ZavaFinance

A finance agent for **Microsoft Teams and Microsoft 365 Copilot**, split across two hosts: a
Foundry **hosted agent** that owns routing and tools, and a thin **channel** that owns identity,
acknowledgement, and delivery.

It answers three kinds of question, each as the signed-in user:

| Tool | Backed by | Shape |
|---|---|---|
| `get_kpi_info` | Copilot Studio agent | Natural language, slow (~51 s) |
| `get_statement` | Fabric lakehouse SQL | Structured arguments, sub-second |
| `explore_finance` | Fabric data agent over MCP | Open-ended, 25 s to 3 min |

> The finance dataset describes **Zava**, a fictional company. No real financial data is in this
> repository.

## Why it is split

The same finance agent is reachable two ways:

```
Teams / M365 Copilot ─► Bot Service ─► ZavaFinance.Channel ─┐
                                                            ├─► ZavaFinance.Agent ─► tools
Any Responses API client ───────────────────────────────────┘      (Foundry hosted)
```

The channel is **not** the only front door. Anything that can call the Responses API — a web app,
another agent — reaches the same agent with the same per-user isolation, without Teams in the
path. That is the reason for the split, and it is what makes the agent testable from the Foundry
playground rather than only through a real Teams turn.

The split is not free. There is an extra hop, so a dead turn is now the channel, the gateway or
the container; the user assertion crosses the wire and must be kept out of logs at three points;
this solution owns the OBO lifecycle rather than delegating refresh to the SDK; and nothing is
removed operationally — the bot app, OAuth connection, Function App, Durable Task and blob storage
all remain, plus a container, a registry and an agent deployment.

## The header that makes it work

Foundry forwards only headers prefixed **`x-client-`** to a hosted-agent container, and
deliberately does **not** forward `Authorization`. The adapter documents this on the property
itself:

> `ResponseContext.ClientHeaders` — *Gets the forwarded client headers (those prefixed with
> `x-client-`) from the original HTTP request.*

A separate header is required because one bearer token cannot carry two audiences:

| Hop | `Authorization` | `x-client-user-token` |
|---|---|---|
| Teams → channel | Bot Service JWT — proves the caller is Bot Service | — |
| Channel → Foundry | Channel's managed identity — authorizes the call | the user's assertion, forwarded unchanged |
| Agent → Copilot Studio / Fabric | the OBO result — acts **as the user** | not sent |

Foundry neither authenticates nor interprets the header. **This solution owns its validation**,
and that ownership is the security-critical part of the design.

### The assertion is validated before its claims are used

The session key — the isolation boundary for all per-user state — is derived from the `tid` and
`oid` claims of the forwarded assertion. [`UserAssertionValidator`](src/ZavaFinance.Agent/UserAssertionValidator.cs)
verifies signature, issuer, audience and lifetime against the tenant's OpenID Connect metadata
**first**, and a token that fails ends the turn.

It is not sufficient to argue that a forged assertion would fail the later OBO exchange. That
check happens *after* the session key has already been used to load and later overwrite state, so
a forgery that never obtains a downstream token could still read and poison another user's
session. `UserAssertionValidatorTests` pins this.

## Layout

```
src/ZavaFinance.Agent/     Foundry hosted agent, and the shared core it carries:
                             Agent/          routing agent and session state
                             Tools/          get_kpi_info, get_statement, explore_finance
                             Finance/        KPI catalog, period parsing, Fabric clients
                             Identity/       session-key derivation
                             Abstractions/   IDownstreamTokenProvider
src/ZavaFinance.Channel/   Function App: Teams + M365 Copilot, ack, proactive delivery
tests/ZavaFinance.Tests/   isolation, finance maths, tool failures, routing golden set
appPackage/                Teams + Microsoft 365 Copilot manifest and icons
```

The routing agent and tools live **inside** the agent project rather than in a separate library,
because the hosted-agent code deploy uploads a single project directory: a project reference is
dropped and the remote build fails on every shared type. The namespaces stay `ZavaFinance.Core.*`
so the separation is still visible in the source tree, and the channel references this project to
reuse exactly the same routing agent and tools. One copy of the code, one golden set, two hosts.

The seam that lets one set of tools serve both hosts is one interface:

```csharp
public interface IDownstreamTokenProvider
{
    Task<string> GetTokenAsync(string[] scopes, CancellationToken cancellationToken);
}
```

The channel implementation delegates to the Agents SDK; the hosted agent implementation performs
the MSAL On-Behalf-Of exchange. Everything else — routing, tools, aggregation rules, verbatim
passthrough — is shared, so the golden set measures the code that actually runs.

## Package status

Everything is GA except one package, and it is confined to one project:

| Package | Status | Where |
|---|---|---|
| `Microsoft.Agents.AI` 1.21.0 | GA | Agent |
| `Microsoft.Agents.CopilotStudio.Client` 1.8.77 | GA | Agent |
| `Microsoft.Agents.Storage` 1.8.77 | GA | Agent |
| `Microsoft.Data.SqlClient` 6.1.4 | GA | Agent |
| `Microsoft.Agents.Hosting.AspNetCore` 1.8.77 | GA | Channel |
| `Microsoft.Azure.Functions.Worker.Extensions.DurableTask` 1.16.4 | GA | Channel |
| `Microsoft.Identity.Client` 4.89.0 | GA | Agent |
| **`Azure.AI.AgentServer.Responses` 1.0.0-beta.8** | **preview** | Agent only |

Exactly one preview package, confined to one project. The routing conversation is serialized into
this solution's own store rather than a durable entity, which is what keeps
`Microsoft.Agents.AI.DurableTask` and `Microsoft.Agents.AI.Hosting.AzureFunctions` — neither of
which has a GA release — out of the dependency set entirely.

### On the one preview package

`Azure.AI.AgentServer.Responses` had breaking changes in beta.2, beta.6 and beta.8, and beta.8
removed public types outright. All of that churn is in **resilient background execution, steerable
conversations and stream providers** — none of which this solution uses, because responses are
returned whole and conversation state is owned here. The surface actually consumed
(`ResponseHandler.CreateAsync`, `ResponseContext.ClientHeaders`, `GetInputTextAsync`) has been
stable since beta.1.

Pinned deliberately. Do not adopt the resilience or steering APIs without re-reading that
changelog.

## Known limitation: progress cannot name the tool

Routing happens inside the hosted agent, so the channel learns which tool ran only when the answer
is already back. The acknowledgement is therefore tool-neutral — the user sees *"Working on that…"*
for the whole wait, which can exceed a minute — and it cannot say *"Analysing that in the finance
data agent…"* however much that would help.

This is pinned by `AcknowledgementCannotNameTheToolInThisTopology` so it cannot be quietly
reintroduced as a bug. Fixing it properly means having the agent stream its routing decision so the
channel can relay it, which trades the "responses are returned whole" rule for partial output.

The latency budget is unrelated to any of this: Bot Service times out at 10–15 s and the slow tools
take far longer, so acknowledge-then-answer-proactively is mandatory regardless.

## Every answer names its source

All three tools append a one-line attribution, from a single definition in `SourceFooter`:

| Tool | Footer |
|---|---|
| `get_statement` | `Source: Zava finance lakehouse. <KPI> = <formula>.` |
| `explore_finance` | `Source: Zava finance data agent (Microsoft Fabric).` |
| `get_kpi_info` | `Source: KPIpedia (Copilot Studio).` |

This was originally true of `get_statement` only, because that answer is composed here while the
other two are passed through verbatim from a subagent. The asymmetry was not deliberate, and it was
backwards: the two natural-language tools are precisely the ones whose text is generated elsewhere,
so they are the ones where naming the system matters most.

The footer is **appended after** the answer, never woven into it. The subagent answers are returned
verbatim on purpose — a paraphrased figure is a wrong figure, and a dropped citation is an unsourced
claim — so attribution has to be additive or it would defeat the passthrough. `SourceFooter.Append`
is a no-op when the answer already carries a footer, so a tool that composes its own source text
cannot end up with two.

## Verification

```powershell
dotnet build
dotnet test
```

**147 tests, 0 warnings.** The routing eval self-skips without Foundry configuration, so a
credential-less run still gets the deterministic suite. To run the live golden set:

```powershell
$env:Foundry__ProjectEndpoint = "https://<resource>.services.ai.azure.com/api/projects/<project>"
$env:Foundry__ModelDeployment = "gpt-4.1-mini"
az login
dotnet test --filter "FullyQualifiedName~Routing"
```

Verified at **54/54** against the live model after the port, including the prompt change — which
is the evidence that routing behaviour survived the refactor rather than merely compiling.

## Publishing to Teams and Microsoft 365 Copilot

One package covers both surfaces. This is a **custom engine agent**.

```powershell
./scripts/build-app-package.ps1 -BotId <botAppId> -AppHostName <app>.azurewebsites.net
```

Then **Apps → Manage your apps → Upload an app**.

Three rules, each learned the hard way:

1. **`copilotAgents.customEngineAgents` requires the referenced bot to have `personal` scope**,
   and the app short name and short description must be defined.
2. **Declare `copilot` in `bots[].scopes` explicitly.** Schema 1.21 added it as the Copilot-surface
   scope. An agent whose installed package advertised only `personal` and `team` was visible in
   Copilot but returned *"Sorry, I wasn't able to respond to that"* — with no request ever reaching
   the app, because the failure was upstream.
3. **Bump `version` on every manifest edit**, or Teams silently ignores the update. Copilot's agent
   list also caches separately from Teams, so the two surfaces can disagree for minutes to hours.

## Prerequisites

- .NET 10 SDK
- A Foundry project with a chat model deployment
- A published Copilot Studio agent with user authentication
- A Fabric **F2 or higher** capacity, with a lakehouse and a published data agent
- An Entra app registration with delegated permissions and **tenant-wide admin consent**:
  `CopilotStudio.Copilots.Invoke`, Graph `User.Read`, Azure SQL `user_impersonation`,
  Power BI `Item.Execute.All`

Without admin consent, `.default` returns a token that silently lacks the scope and the downstream
call fails with an opaque 401.

### Credentials

The OBO confidential client prefers a **managed-identity federated credential**
(`api://AzureADTokenExchange`) over a client secret, which keeps the "managed identity, no keys"
rule intact and avoids a private endpoint for a single value. Set `Obo:ClientSecret` only where a
federated credential is unavailable.

## Operational notes

### A deployed agent does not reach existing conversations

Hosted-agent containers are **warm per session**, and a session is bound to its conversation. A
conversation that keeps being used keeps its container — and therefore keeps running the build
that was live when that container started. Deploying a new version does not migrate it.

This is worth knowing before it wastes an afternoon. A fix deployed correctly, verified active,
and confirmed by the startup marker still reproduced the original exception for four consecutive
test cycles, because the conversation under test was pinned to a container from an hour earlier.
New conversations were already running the fix.

Two things make it diagnosable:

- **`ThisAssembly.BuildMarker`** is logged at startup and participates in the package content, so
  it both forces a new version and answers "which build is actually running?" in one query.
- **`reset`** deletes the caller's session state, including the stored conversation id, so the
  next turn creates a fresh conversation and a fresh container. After deploying the agent, reset
  before testing.

Stack-trace line numbers are the other tell, and they are more reliable than they look: an async
frame reports the real await site, so a line that no longer matches the source means the running
assembly is older than the source — not that the fix was wrong.

### A streamed answer is lost on Microsoft 365 Copilot unless delivery is verified

The proactive answer is delivered as the stream's final message, so it replaces the progress line
instead of appearing beneath a status that never resolves. On **Microsoft 365 Copilot the stream is
not rendered**, and the failure is silent: the user gets the acknowledgement and nothing else, no
exception is thrown, and the turn is recorded as completed.

Measured on a real Copilot turn, by status code on the Bot Connector:

| Activity | Result | Rendered |
|---|---|---|
| Acknowledgement (ordinary message) | `201` + resource id | yes |
| First streamed frame | `201` + resource id | yes |
| Second streamed frame | `202`, no id | **no** |
| **Streamed final answer** | `202`, no id | **no** |
| Answer re-sent as ordinary message | `201` + resource id | yes |

Copilot creates a resource for the *first* streamed frame and accepts-and-discards every later one.
Teams creates all of them. Ordinary message activities are created on both surfaces, which is why
the acknowledgement always arrives and only the streamed answer disappears.

So the channel checks whether the **final message specifically** produced a resource, reading the
`ResourceResponse` the channel returned for it via `ITurnContext.OnSendActivities`. If it did not,
the answer is re-sent as an ordinary message. The same fallback covers `NotStarted`, which is what
ending a stream returns when a fast tool answers before the first update is flushed.

> ⚠ **Do not simplify this to "does the stream have an id".** That was the first attempt and it is
> wrong: the id is assigned from the first frame, which Copilot *does* create, so the stream has an
> id while the answer is still discarded. The streaming API reports that it ended the stream, not
> whether the channel kept anything, and it swallows the response of its own final send — so
> `OnSendActivities` is the only place the truth is available.

The same signal drives progress. Once a streamed frame is seen to be discarded, later progress
nudges switch to ordinary messages, so a one-to-three-minute turn does not go visibly silent after
the first update.

`DeliveredInStream=False` in the `Turn progress finished` log line means the answer fallback was
used. Pinned by `AnswerIsResentWhenTheChannelDiscardsTheStreamedFinalMessage`,
`AnswerIsNotResentWhenTheChannelRendersTheStreamedFinalMessage` and
`ProgressSwitchesToMessagesOnceStreamedFramesAreDiscarded`.

### `azd deploy` is content-addressed

An unchanged package is skipped and the previous version keeps serving, reported as a successful
deploy in about ten seconds. Bump the build marker to force a version.

### Storage is private-only in this tenant

Policy forces `publicNetworkAccess: Disabled` on storage accounts, and setting it back to
`Enabled` is silently reverted. The hosted agent therefore uses the platform's own
`FoundryStateStore` rather than a customer storage account, which removes the storage account,
the VNet, the private endpoints and the private DNS zones from the agent tier entirely. The
channel still needs all of them, because Durable Functions does.

### One bot app id, one Azure Bot Service

`Failed to store new bot. MsaAppId is already in use` is the error when two Bot Services share an
app registration. The app can be reused for OBO, but not for a second bot.

### Reserved environment variables

`FOUNDRY_*` and `AGENT_*` are reserved for the platform. Declaring `Foundry__ProjectEndpoint` is
rejected at deploy time with a `ValidationError`; the endpoint is injected, so bind it from
`FOUNDRY_PROJECT_ENDPOINT` and carry anything else under a neutral name.

### Conversation ids are issued by the platform

The Responses endpoint validates the identifier and rejects anything it did not issue —
`Invalid conversation id '…', Malformed identifier`. Create a conversation through the
conversations endpoint and store its `conv_…` id per session; reusing it is what keeps the
agent's state sticky across turns.

## License

[MIT](./LICENSE)
