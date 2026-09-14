// Copyright (c) Microsoft Corporation.

using System.ComponentModel;
using System.Text;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;

namespace ZavaFinance.Core.Tools;

/// <summary>
/// Copilot Studio subagent tool. Slow (~51 s measured), so it only ever runs off the inbound
/// channel turn, which times out at 10–15 seconds.
/// </summary>
public sealed class KpiInfoTool
{
    private readonly ICopilotStudioClientFactory _clientFactory;
    private readonly IDownstreamTokenProvider _tokenProvider;
    private readonly OrchestratorSessionState _state;
    private readonly ToolPassthrough _passthrough;
    private readonly OrchestratorOptions _options;
    private readonly ILogger _logger;

    public KpiInfoTool(
        ICopilotStudioClientFactory clientFactory,
        IDownstreamTokenProvider tokenProvider,
        OrchestratorSessionState state,
        ToolPassthrough passthrough,
        OrchestratorOptions options,
        ILogger logger)
    {
        _clientFactory = clientFactory;
        _tokenProvider = tokenProvider;
        _state = state;
        _passthrough = passthrough;
        _options = options;
        _logger = logger;
    }

    [OrchestratorTool(OrchestratorRoute.KpiInfoTool)]
    [Description(
        "Look up the official definition, meaning, formula or business description of a "
        + "KPI from the KPIpedia knowledge base. Use this only when the user asks what a KPI "
        + "is, what it means, how it is defined or how it is calculated. Do NOT use this to "
        + "retrieve figures or numbers, and do NOT use it merely because the user named a KPI "
        + "without an organization or period.")]
    public async Task<string> GetKpiInfoAsync(
        [Description("The KPI name to explain, e.g. 'Net Revenues'.")] string kpi,
        CancellationToken cancellationToken)
    {
        string kpiName = kpi;

        if (string.IsNullOrWhiteSpace(kpiName))
        {
            return Passthrough("I need a KPI name to look up.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.SubagentTimeout);

        try
        {
            CopilotClient client = _clientFactory.Create(_tokenProvider);

            // Copilot Studio is a natural-language endpoint. A bare noun such as "EBIT"
            // does not match a topic trigger or run a knowledge search, so it falls through
            // to the fallback topic and answers with nothing useful. Ask a real question.
            string question = BuildQuestion(kpiName);

            string answer = await AskAsync(client, question, timeout.Token);

            if (!string.IsNullOrWhiteSpace(answer))
            {
                // Recorded per user, per conversation — never in a static or singleton.
                _state.LastKpiName = kpiName;
                _state.LastSubagent = "get_kpi_info";
            }

            // The subagent's answer is sourced and carries citations. It is delivered to the
            // user exactly as received, never re-emitted through the model. The attribution is
            // appended after it, naming the system the citations come from.
            return Passthrough(
                string.IsNullOrWhiteSpace(answer)
                    ? $"KPIpedia returned no description for '{kpiName}'."
                    : SourceFooter.Append(answer, SourceFooter.KnowledgeBase));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("get_kpi_info timed out after {Timeout}.", _options.SubagentTimeout);

            // A timeout must never surface as silence in Teams.
            return Passthrough(
                $"KPIpedia did not respond in time for '{kpiName}'. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "get_kpi_info failed.");

            return Passthrough($"I could not reach KPIpedia to look up '{kpiName}'.");
        }
    }

    private string Passthrough(string answer)
    {
        _passthrough.Capture(answer);

        return answer;
    }

    private static string BuildQuestion(string kpiName)
    {
        string trimmed = kpiName.Trim();

        // Already a question or a sentence: pass it through untouched.
        return trimmed.Contains(' ') && trimmed.EndsWith('?')
            ? trimmed
            : $"What is {trimmed}? Please explain how it is defined and calculated.";
    }

    private async Task<string> AskAsync(
        CopilotClient client, string question, CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();

        if (string.IsNullOrEmpty(_state.CopilotStudioConversationId))
        {
            await foreach (IActivity activity in client.StartConversationAsync(
                emitStartConversationEvent: true,
                cancellationToken: cancellationToken))
            {
                // The conversation id can arrive on any activity, not only a message.
                _state.CopilotStudioConversationId ??= activity.Conversation?.Id;
            }

            _logger.LogInformation(
                "Started KPIpedia conversation. Acquired={Acquired}",
                !string.IsNullOrEmpty(_state.CopilotStudioConversationId));
        }

        if (string.IsNullOrEmpty(_state.CopilotStudioConversationId))
        {
            throw new InvalidOperationException(
                "Copilot Studio did not return a conversation id.");
        }

        var seenTypes = new List<string>();
        int messageCount = 0;

        // Only the current question plus the conversation id. Our transcript is never
        // forwarded — Copilot Studio maintains its own conversation state.
        await foreach (IActivity activity in client.AskQuestionAsync(
            question,
            _state.CopilotStudioConversationId,
            cancellationToken))
        {
            seenTypes.Add(activity.Type?.ToString() ?? "null");

            if (activity.IsType(ActivityTypes.Message) && !string.IsNullOrEmpty(activity.Text))
            {
                messageCount++;
                answer.AppendLine(activity.Text);
            }
        }

        string result = answer.ToString().Trim();

        // Shape only, never content: enough to tell "the subagent said nothing" apart from
        // "the subagent answered and we dropped it".
        _logger.LogInformation(
            "KPIpedia replied. Activities={Activities} Messages={Messages} Length={Length} "
            + "Types={Types}",
            seenTypes.Count,
            messageCount,
            result.Length,
            string.Join(",", seenTypes.Distinct()));

        if (_options.LogSubagentText && result.Length > 0)
        {
            // Off by default. Subagent answers are permissioned user content, so this is
            // enabled per environment only as a deliberate diagnostic.
            _logger.LogInformation("KPIpedia answer text: {Answer}", result);
        }

        return result;
    }
}
