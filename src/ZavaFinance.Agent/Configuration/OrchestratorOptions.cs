// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Configuration;

public sealed class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>
    /// Salt for the session key hash. Sourced from Key Vault; never checked in.
    /// </summary>
    public string SessionKeySalt { get; set; } = string.Empty;

    /// <summary>
    /// Conversation history retention. Each interaction resets the timer.
    /// </summary>
    public TimeSpan SessionTimeToLive { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Ceiling for a single Copilot Studio call. Measured p50 is ~51 s.
    /// </summary>
    public TimeSpan SubagentTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// History cap for the agent's conversation. Unbounded history costs tokens and latency
    /// on every turn.
    /// </summary>
    public const int DefaultMaxHistoryMessages = 20;
    private int _maxHistoryMessages = DefaultMaxHistoryMessages;

    public int MaxHistoryMessages
    {
        get => _maxHistoryMessages;
        set
        {
            // A native turn consists of a user message, a call and its result marker.
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 3);
            _maxHistoryMessages = value;
        }
    }

    /// <summary>
    /// Logs the subagent's answer text. <b>Off by default.</b> Subagent answers are
    /// permissioned user content, so this is a deliberate per-environment diagnostic and not
    /// something to leave enabled.
    /// </summary>
    public bool LogSubagentText { get; set; }
}

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    public string ProjectEndpoint { get; set; } = string.Empty;

    public string ModelDeployment { get; set; } = "gpt-4.1-mini";

    /// <summary>
    /// Opts into low-effort reasoning and omits temperature for both routing and reranking.
    /// Set from the deployment's known capabilities, not its arbitrary deployment name.
    /// </summary>
    public bool ReasoningEnabled { get; set; }
}
