# ZavaFinance Swimlane Diagram

This swimlane shows the Teams and Microsoft 365 Copilot request path. It highlights the immediate acknowledgement, durable background processing, hosted-agent routing, delegated tool execution, and proactive delivery.

## Teams and Microsoft 365 Copilot Turn

<!-- mermaid-checked: no \n, no em-dash/en-dash, no {} in labels, subgraphs are id["label"], arrows are -->|"label"|, all subgraphs closed by end, ids unique -->
```mermaid
flowchart LR
    subgraph SwimUser["User"]
        sUserAsk["1. Ask finance question"]
        sUserAck["7. See working message"]
        sUserAnswer["22. Receive final answer"]
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
        sCache["19. Cache answer as answer-ready"]
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
        sTool["17. Query selected finance service"]
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
    sTool -->|"verbatim result"| sPersist
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
delivery can still duplicate an external message.
