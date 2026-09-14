// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Configuration;

/// <summary>
/// Fabric lakehouse and data agent settings.
/// <para>
/// Both are reached with a **delegated user token**, never an app-only token: row-level
/// security only filters per user when the end user's identity reaches Fabric.
/// </para>
/// </summary>
public sealed class FabricOptions
{
    public const string SectionName = "Fabric";

    /// <summary>
    /// SQL analytics endpoint host of the lakehouse, e.g.
    /// <c>xxxx-yyyy.datawarehouse.fabric.microsoft.com</c>.
    /// </summary>
    public string SqlEndpoint { get; set; } = string.Empty;

    /// <summary>Database name, which is the lakehouse display name.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>Workspace that holds the lakehouse and the data agent.</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>Published Fabric data agent backing <c>explore_finance</c>.</summary>
    public string DataAgentId { get; set; } = string.Empty;

    /// <summary>
    /// The data agent's OpenAI-compatible surface requires an explicit api-version; without it
    /// every call fails with "Query parameter 'api-version' is required".
    /// </summary>
    public string DataAgentApiVersion { get; set; } = "2024-05-01-preview";

    /// <summary>
    /// get_statement must stay fast and synchronous, so its SQL is bounded well inside the
    /// channel turn budget rather than the subagent budget.
    /// </summary>
    public TimeSpan SqlTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// explore_finance is a natural-language endpoint and behaves like the Copilot Studio
    /// subagent: slow, and therefore only ever on the durable path.
    /// <para>
    /// Measured in production between roughly 25 s and over 3 minutes for the same agent,
    /// depending on how many queries it plans. The user already has an acknowledgement by this
    /// point, so a longer ceiling costs them nothing and avoids abandoning an answer that was
    /// nearly ready. The MCP endpoint also supports the tasks extension for requests that
    /// outlive a connection, which is the more robust fix if this ceiling is ever hit.
    /// </para>
    /// </summary>
    public TimeSpan DataAgentTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The SQL analytics endpoint audience. Not the Fabric REST audience.</summary>
    public static string[] SqlScopes => ["https://database.windows.net/.default"];

    /// <summary>The Fabric REST audience used by the data agent.</summary>
    public static string[] DataAgentScopes => ["https://api.fabric.microsoft.com/.default"];

    public bool IsSqlConfigured =>
        !string.IsNullOrWhiteSpace(SqlEndpoint) && !string.IsNullOrWhiteSpace(Database);

    public bool IsDataAgentConfigured =>
        !string.IsNullOrWhiteSpace(WorkspaceId) && !string.IsNullOrWhiteSpace(DataAgentId);
}
