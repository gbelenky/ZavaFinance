// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Tools;

/// <summary>
/// Marks a method as a native function tool and fixes its public tool name.
/// <para>
/// The agent discovers annotated public instance methods on its registered tool classes
/// and generates native function declarations from them. Execution remains in
/// the hosted agent, using the caller's delegated identity, with no answer-synthesis model pass.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OrchestratorToolAttribute : Attribute
{
    public OrchestratorToolAttribute(string name) => Name = name;

    /// <summary>The tool name the model returns, for example <c>get_kpi_info</c>.</summary>
    public string Name { get; }
}
