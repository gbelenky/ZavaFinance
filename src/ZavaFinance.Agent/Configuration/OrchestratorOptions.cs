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
    /// Name of the OAuth/OBO handler configured under
    /// <c>AgentApplication:UserAuthorization:Handlers</c>.
    /// </summary>
    public string UserAuthorizationHandler { get; set; } = "mcs";

    /// <summary>
    /// Conversation history retention. Each interaction resets the timer.
    /// </summary>
    public TimeSpan SessionTimeToLive { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Tool-neutral message shown while the durable turn runs.
    /// </summary>
    public string AcknowledgementText { get; set; } = "Working on that…";

    /// <summary>
    /// Ceiling for a single Copilot Studio call. Measured p50 is ~51 s.
    /// </summary>
    public TimeSpan SubagentTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// History cap for the agent's conversation. Unbounded history costs tokens and latency
    /// on every turn.
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 20;

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
}
