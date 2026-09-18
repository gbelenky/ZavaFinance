// Copyright (c) Microsoft Corporation.

namespace ZavaFinance.Core.Tools;

/// <summary>
/// Marks a method as a native function tool and fixes its public tool name.
/// <para>
/// The agent discovers annotated public instance methods on its registered tool classes
/// and binds executable typed AIFunctions. MAF invokes the selected function with the caller's
/// delegated identity; invocation middleware terminates before any answer-synthesis model pass.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OrchestratorToolAttribute : Attribute
{
    public OrchestratorToolAttribute(string name) => Name = name;

    /// <summary>The tool name the model returns, for example <c>get_kpi_info</c>.</summary>
    public string Name { get; }
}
