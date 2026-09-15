// Copyright (c) Microsoft Corporation.

using System.ComponentModel;
using System.Net;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;

namespace ZavaFinance.Core.Tools;

/// <summary>
/// Open-ended finance analysis, answered by the published Fabric data agent.
/// <para>
/// This is the counterpart to <see cref="StatementTool"/>: a structured signature cannot express
/// "why did APAC margin fall in Q3, and which departments drove it?". Being a natural-language
/// endpoint it is slow, so it belongs on the durable acknowledge-then-answer path alongside the
/// Copilot Studio subagent.
/// </para>
/// <para>
/// Its response is text derived from data that users can write to. It is returned to the caller
/// without entering the model's history, just like the Copilot Studio answer.
/// </para>
/// </summary>
public sealed class ExploreFinanceTool
{
    private readonly FabricDataAgentClient _client;
    private readonly FabricOptions _options;
    private readonly ILogger _logger;

    public ExploreFinanceTool(
        FabricDataAgentClient client,
        FabricOptions options,
        ILogger logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    [OrchestratorTool(FinanceToolNames.ExploreFinanceTool)]
    [Description(
        "Answer an open-ended analytical question about Zava finance data that a single "
        + "KPI-organization-period lookup cannot express: explaining why something moved, "
        + "ranking or comparing many organizations or periods at once, finding drivers, "
        + "trends or outliers. Use this when the question needs analysis rather than one "
        + "figure. Do not infer a request for trends or comparisons from a single KPI's status. "
        + "Do NOT use this for a single specific figure, and do NOT use it to explain "
        + "what a KPI means.")]
    public async Task<string> ExploreFinanceAsync(
        [Description(
            "The user's analytical question, in full, as a natural-language sentence. "
            + "Preserve its scope; do not add comparisons or subquestions.")]
        string question,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return "What would you like me to analyse?";
        }

        if (!_options.IsDataAgentConfigured)
        {
            return "Open-ended finance analysis is not configured in this environment.";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DataAgentTimeout);

        try
        {
            string answer = await _client.AskAsync(question, timeout.Token);

            _logger.LogInformation(
                "explore_finance replied. Length={Length}", answer.Length);

            // Returned verbatim: the data agent's answer carries figures and its own framing,
            // and a model paraphrase of a figure is a wrong figure. The attribution is appended
            // after it rather than woven in, so the answer itself is still untouched.
            return string.IsNullOrWhiteSpace(answer)
                ? "The finance data agent did not return an answer for that question."
                : SourceFooter.Append(answer, SourceFooter.DataAgent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "explore_finance timed out after {Timeout}.", _options.DataAgentTimeout);

            // A timeout must never surface as silence in Teams.
            return "The finance data agent did not respond in time. Please try again, or ask for "
                + "a specific KPI, organization and period instead.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Fabric returns 429 CapacityLimitExceeded when the workspace's capacity is
            // throttled. That is a different problem from an unreachable agent: the request was
            // delivered and rejected, waiting resolves it, and the operator can fix it by
            // scaling the capacity. Reporting it as "could not reach" sends the user looking
            // for a connectivity fault that does not exist.
            _logger.LogWarning(ex, "explore_finance rejected: Fabric capacity limit exceeded.");

            return "The finance data agent is busy — the Fabric capacity backing it has hit its "
                + "compute limit. Please try again in a few minutes. For a specific figure, ask "
                + "for a KPI, organization and period instead, which uses a lighter query.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "explore_finance failed.");

            return "I could not reach the finance data agent for that analysis.";
        }
    }

}
