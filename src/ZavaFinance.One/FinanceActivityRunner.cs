using Microsoft.Agents.AI;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Agent;

namespace ZavaFinance.One;

public interface IFinanceActivityRunner
{
    Task<FinanceReply> RunReplyAsync(
        IDownstreamTokenProvider tokenProvider, string sessionKey, string question,
        ClarificationSubmission? submission, CancellationToken cancellationToken);
}

public sealed class FinanceActivityRunner(OrchestratorAgent orchestrator, AIAgent routingAgent)
    : IFinanceActivityRunner
{
    public Task<FinanceReply> RunReplyAsync(
        IDownstreamTokenProvider tokenProvider, string sessionKey, string question,
        ClarificationSubmission? submission, CancellationToken cancellationToken)
        => orchestrator.RunReplyAsync(
            routingAgent, tokenProvider, sessionKey, question, submission, cancellationToken);
}
