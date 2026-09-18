using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace ZavaFinance.Core.Agent;

internal static class ModelRequestOptions
{
    internal static ChatOptions Create(string modelDeployment, bool reasoningEnabled = false) =>
        new()
        {
            ModelId = modelDeployment,
            // Keep classification deterministic where supported; reasoning models reject temperature.
            Temperature = reasoningEnabled ? null : 0,
            // Permissioned results must never become stored model output or server-side history.
            RawRepresentationFactory = _ => new CreateResponseOptions
            {
                StoredOutputEnabled = false,
                ReasoningOptions = reasoningEnabled
                    ? new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Low }
                    : null
            }
        };
}
