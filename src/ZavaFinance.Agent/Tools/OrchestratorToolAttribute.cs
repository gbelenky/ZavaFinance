// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Tools;

/// <summary>
/// Marks a method as an orchestrator tool and fixes the name the model uses to select it.
/// <para>
/// The model does not invoke these methods. The durable routing agent returns a
/// <see cref="Agent.OrchestratorRoute"/> naming one of them, and the channel host executes it
/// afterwards inside the M365 continuation, where the caller's turn context can perform OBO.
/// The attribute exists so the routing prompt and the executable tool cannot drift apart.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OrchestratorToolAttribute : Attribute
{
    public OrchestratorToolAttribute(string name) => Name = name;

    /// <summary>The tool name the model returns, for example <c>get_kpi_info</c>.</summary>
    public string Name { get; }
}
