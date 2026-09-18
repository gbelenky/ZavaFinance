// Copyright (c) Microsoft Corporation.

using Azure.AI.AgentServer.Responses;
using ZavaFinance.Agent;

// The hosted-agent runtime contract - port 8088, GET /readiness, POST /responses, SSE and
// graceful shutdown - is implemented by the adapter. Only the handler below is ours.
ResponsesServer.Run<ZavaFinanceResponseHandler>(configure: builder =>
{
    // Emitted once at startup so a log always identifies which build is actually running.
    // Diagnosing a stale deployment from behaviour alone cost real time: a fixed defect kept
    // reproducing, and the only evidence that the running code was older than the source was a
    // three-line discrepancy in a stack trace.
    Console.WriteLine(
        $"ZavaFinance.Agent build {ThisAssembly.BuildMarker} starting.");

    builder.Services.AddFinanceServices(builder.Configuration);
});
