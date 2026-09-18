# ZavaFinance Swimlane Diagram

The first paths describe the **original two-host deployment**, including its durable
Channel workflow. The [One native path](#zava-finance-one-native-activity-turn) is
separate and has no durable execution or persisted delivery record.

## Zava Finance One native Activity turn

Messages enter the SDK's background path. Supported clarification invokes validate
the user and payload before enqueueing a normalized message, then return a protocol
acknowledgement. The worker reacquires/validates the human token before finance access.
Only the authenticated success path is shown; pending sign-in does not execute finance.

<!-- mermaid-checked: no \n, no em-dash/en-dash, no {} in labels, subgraphs are id["label"], arrows are -->|"label"|, all subgraphs closed by end, ids unique -->
```mermaid
flowchart LR
    subgraph nsUser["User"]
        nsAsk["Ask or click a clarification row"]
        nsAnswer["Receive text or clickable card"]
    end
    subgraph nsIngress["Bot and Foundry gateway"]
        nsRoute["Route to One Activity endpoint"]
        nsAck["HTTP or invoke acceptance"]
        nsDeliver["Deliver connector reply"]
    end
    subgraph nsNative["One native Activity boundary"]
        nsAdapter["M365 adapter"]
        nsInvoke["Validate invoke identity and selection"]
        nsQueue["In-memory queue"]
        nsAuth["Fresh worker: acquire and validate assertion"]
        nsRender["Render one text or card activity"]
        nsConfirm["Require nonempty delivery ID"]
    end
    subgraph nsFinance["Finance code inside the same One host"]
        nsState["Derive caller key and load One finance state"]
        nsRun["Reuse router, resolver and one selected tool"]
        nsSave["Save finance state, not a work checkpoint"]
    end
    subgraph nsData["Delegated finance services"]
        nsTool["KPIpedia, Fabric SQL or Fabric Data Agent"]
    end
    nsAsk --> nsRoute --> nsAdapter
    nsAdapter -->|"ordinary message"| nsQueue
    nsAdapter -->|"clarification invoke"| nsInvoke
    nsInvoke -->|"normalized message"| nsQueue
    nsQueue -->|"accepted, not completed"| nsAck
    nsQueue --> nsAuth --> nsState --> nsRun
    nsRun -->|"application OBO"| nsTool
    nsTool -->|"verbatim answer or typed options"| nsSave
    nsSave --> nsRender --> nsDeliver
    nsDeliver --> nsAnswer
    nsDeliver -->|"connector response"| nsConfirm
```

HTTP acceptance is not a user-visible progress message and not a delivery guarantee.
One has no Channel progress loop, AnswerReady/Delivered ledger or automatic crash replay.
A restart may lose accepted work; persisted finance memory does not resume it.
Native OAuth continuation is process-local too. Initial silent SSO remains unresolved;
interactive sign-in or cached-token reset is not proof of silent SSO.
See the [One guide](../../../../../src/ZavaFinance.One/README.md) for deployment and acceptance.

## Original Teams and Microsoft 365 Copilot turn

<!-- mermaid-checked: no \n, no em-dash/en-dash, no {} in labels, subgraphs are id["label"], arrows are -->|"label"|, all subgraphs closed by end, ids unique -->
```mermaid
flowchart LR
    subgraph SwimUser["User"]
        sUserAsk["1. Ask finance question"]
        sUserAck["7. See working message"]
        sUserAnswer["22. Receive answer or clarification card"]
    end
    subgraph SwimBot["Teams and Bot Service"]
        sBotForward["2. Forward signed activity"]
        sBotAck["6. Deliver acknowledgement"]
        sBotFinal["21. Deliver proactive message"]
    end
    subgraph SwimChannel["ZavaFinance Channel"]
        sAuth["3. Validate Bot Service JWT"]
        sIdentity["4. Resolve signed-in user"]
        sPending["5. Save pending turn"]
        sSchedule["8. Schedule durable turn"]
        sResume["10. Resume conversation"]
        sCallAgent["11. Call Foundry Responses API"]
        sCache["19. Cache typed reply as answer-ready"]
        sSend["20. Send proactive answer"]
        sDelivered["23. Record delivered"]
    end
    subgraph SwimDurable["Durable Task"]
        sStart["9. Start retryable activity"]
        sComplete["24. Complete orchestration"]
    end
    subgraph SwimFoundry["Foundry Hosted Agent"]
        sGateway["12. Authorize channel workload"]
        sValidate["13. Validate user assertion"]
        sLoad["14. Load isolated session"]
        sRoute["15. Native function selection and validation"]
        sPersist["18. Save call history without tool answer"]
    end
    subgraph SwimFinance["Delegated Finance Service"]
        sObo["16. Exchange token on behalf of user"]
        sTool["17. Resolve statement terms or query selected service"]
    end

    sUserAsk -->|"message"| sBotForward
    sBotForward -->|"activity"| sAuth
    sAuth -->|"trusted caller"| sIdentity
    sIdentity -->|"derived session key"| sPending
    sPending -->|"acknowledge first"| sBotAck
    sBotAck -->|"neutral progress"| sUserAck
    sPending -->|"turn reference"| sSchedule
    sSchedule -->|"instance request"| sStart
    sStart -->|"activity callback"| sResume
    sResume -->|"question and user assertion"| sCallAgent
    sCallAgent -->|"managed identity authorization"| sGateway
    sGateway -->|"forward client header"| sValidate
    sValidate -->|"trusted tenant and user"| sLoad
    sLoad -->|"question and history"| sRoute
    sRoute -->|"validated function call"| sObo
    sObo -->|"delegated access token"| sTool
    sTool -->|"verbatim answer or typed clarification"| sPersist
    sPersist -->|"response body"| sCache
    sCache -->|"reuse cached answer on retry"| sSend
    sSend -->|"ordinary message"| sBotFinal
    sBotFinal -->|"final response"| sUserAnswer
    sBotFinal -->|"send confirmed"| sDelivered
    sDelivered -->|"success"| sComplete
```

## Direct Responses API Turn

Direct clients bypass the channel and Durable Task. They call the same Foundry hosted agent, which still validates the forwarded user assertion, derives isolated state, routes one tool, and uses delegated downstream access.

<!-- mermaid-checked: no \n, no em-dash/en-dash, no {} in labels, subgraphs are id["label"], arrows are -->|"label"|, all subgraphs closed by end, ids unique -->
```mermaid
flowchart LR
    subgraph DirectClientLane["Responses API Client"]
        dRequest["1. Send question and user assertion"]
        dReceive["9. Receive final answer"]
    end
    subgraph DirectFoundryLane["Foundry Gateway"]
        dAuthorize["2. Authorize client workload"]
        dForward["3. Forward client header"]
    end
    subgraph DirectAgentLane["ZavaFinance Hosted Agent"]
        dValidate["4. Validate user assertion"]
        dSession["5. Derive and load isolated session"]
        dRoute["6. Native function selection and validation"]
        dSave["8. Save call history without tool answer"]
    end
    subgraph DirectFinanceLane["Delegated Finance Service"]
        dExecute["7. Execute as signed-in user"]
    end

    dRequest -->|"Responses API"| dAuthorize
    dAuthorize -->|"authorized request"| dForward
    dForward -->|"forwarded assertion"| dValidate
    dValidate -->|"validated claims"| dSession
    dSession -->|"question and history"| dRoute
    dRoute -->|"delegated tool call"| dExecute
    dExecute -->|"verbatim result"| dSave
    dSave -->|"response"| dReceive
```

Tool answers go directly to the client without another model generation step. Only content-free
function-result markers enter the model's conversation history, keeping subsequent calls valid
without retaining permissioned finance responses.

Channel acknowledgement, progress and final answers use ordinary messages, not in-place
streaming. Delivered records suppress later retries, but a crash between sending and persisting
delivery can still duplicate an external message. Clarification cards are ordinary final
messages for their turn; a selection starts a new authenticated turn.

## Statement resolution and clarification

```mermaid
sequenceDiagram
    actor User
    participant Channel as Channel Service
    participant Agent as Hosted agent and local resolver
    participant SQL as Fabric delegated SQL
    participant Search as Azure AI Search
    participant Model as Embedding and candidate model
    participant State as Caller conversation state
    User->>Channel: Raw KPI, organization and date question
    Channel->>Agent: Workload auth and forwarded user assertion
    Agent->>Agent: Validate identity and strict period
    Agent->>SQL: Active release and user-visible catalogue/scopes
    SQL-->>Agent: Authoritative metadata
    Agent->>Agent: Exact code/name/alias matching
    opt No safe exact match
        Agent->>Model: Embed raw query terms
        Agent->>Search: Keyword plus vector and semantic retrieval
        Search-->>Agent: Candidate IDs
        Agent->>SQL: Revalidate candidates under user permissions
        opt More than one plausible candidate
            Agent->>Model: Constrained selection from permitted candidates
            Model-->>Agent: Candidate ID or needs clarification
        end
    end
    alt Ambiguity or any fuzzy suggestion, including a single candidate
        Agent->>State: Pending request, field, candidates, expiry and full release binding
        Agent-->>Channel: Numbered text result plus structured clarification options
        Channel->>Channel: Persist AnswerReady before send
        Channel-->>User: One attachment-only card with numbered choices and paths
        Channel->>Channel: Record Delivered after nonempty message ID
        User->>Channel: Submit Adaptive Card or reply with number/ordinal
        Channel->>Agent: Authenticated selection and current conversation
        Agent->>State: Match caller, request, candidate and expiry
        Agent->>SQL: Confirm full active release binding and current visibility
        Agent->>Agent: Reject stale, reset or forged selections
    end
    Agent->>SQL: Fixed parameterized query using leaf-scope EXISTS
    SQL-->>Agent: Additive finance components
    Agent->>Agent: Recompute ratios and render deterministic statement
    Agent-->>Channel: Verbatim finance reply
    Channel->>Channel: Persist AnswerReady and stop progress
    Channel-->>User: Ordinary final message
    Channel->>Channel: Record Delivered after nonempty message ID
```

The final SQL path is entered only after **all** required terms are resolved. If one field
still needs clarification, return another prompt rather than running the statement. Direct
Responses clients receive readable numbered options; negotiated channel clients receive the
typed envelope, and the Channel Service builds the Adaptive Card from its structured options.
Numbered result text remains available. Publication and retrieval never replace delegated
financial authorization. The release binding includes catalogue version, Search index,
embedding deployment and embedding dimensions; changing any field invalidates an old choice.
