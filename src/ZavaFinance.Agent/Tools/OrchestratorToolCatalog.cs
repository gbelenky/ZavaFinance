// Copyright (c) Microsoft Corporation.

using System.ComponentModel;
using System.Reflection;
using System.Text;

namespace ZavaFinance.Core.Tools;

/// <summary>One routed argument, described where it is declared.</summary>
public sealed record OrchestratorToolParameter(string Name, string Description);

/// <summary>One tool the router can select.</summary>
public sealed record OrchestratorToolDescriptor(
    string Name,
    string Description,
    IReadOnlyList<OrchestratorToolParameter> Parameters);

/// <summary>
/// Builds the routing prompt's tool section from the <c>[OrchestratorTool]</c> and
/// <c>[Description]</c> annotations on the tool methods themselves.
/// <para>
/// Before this existed, a tool's description lived in two places: a carefully worded
/// <c>[Description]</c> that nothing read, and a terser restatement inside the system prompt that
/// the model actually saw. Editing the annotation appeared to change routing and did not. Tool
/// descriptions set this Orchestrator's quality ceiling, so they get exactly one home.
/// </para>
/// <para>
/// This is deliberately not <c>ChatOptions.Tools</c>. Registering real functions would let the
/// framework invoke them inside the durable entity, which would write permissioned subagent text
/// into the task hub and route tool output back through the model. Both are prohibited: tool
/// results are returned verbatim so citations survive. The model chooses; it never calls.
/// </para>
/// </summary>
public static class OrchestratorToolCatalog
{
    /// <summary>Every annotated tool, ordered by name so the prompt prefix stays stable.</summary>
    public static IReadOnlyList<OrchestratorToolDescriptor> Tools { get; } = Discover();

    /// <summary>
    /// The tool section of the routing prompt. Placed in the constant prefix of the system
    /// prompt, ahead of the volatile per-turn values, so it stays cacheable.
    /// </summary>
    public static string BuildPromptSection()
    {
        var builder = new StringBuilder();

        foreach (OrchestratorToolDescriptor tool in Tools)
        {
            builder.Append("- ").AppendLine(tool.Name);
            builder.Append("  ").AppendLine(tool.Description);

            if (tool.Parameters.Count == 0)
            {
                continue;
            }

            builder.AppendLine("  Arguments:");

            foreach (OrchestratorToolParameter parameter in tool.Parameters)
            {
                builder.Append("    ").Append(parameter.Name).Append(" — ")
                    .AppendLine(parameter.Description);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static OrchestratorToolDescriptor[] Discover()
    {
        return [.. SafeGetTypes()
            .SelectMany(SafeGetMethods)
            .Select(method => (method, attribute: method.GetCustomAttribute<OrchestratorToolAttribute>()))
            .Where(candidate => candidate.attribute is not null)
            .Select(candidate => Describe(candidate.method, candidate.attribute!))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The catalog is built in a static initializer, so anything thrown here surfaces as a
    /// <see cref="TypeInitializationException"/> on every turn rather than as a diagnosable
    /// error. A single dependency whose types cannot be loaded — routine with preview packages —
    /// must not take the Orchestrator down, so partial reflection results are used instead.
    /// </summary>
    private static IEnumerable<Type> SafeGetTypes()
    {
        try
        {
            return typeof(OrchestratorToolCatalog).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }

    private static IEnumerable<MethodInfo> SafeGetMethods(Type type)
    {
        try
        {
            return type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException)
        {
            return [];
        }
    }

    private static OrchestratorToolDescriptor Describe(MethodInfo method, OrchestratorToolAttribute attribute)
    {
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? throw new InvalidOperationException(
                $"Tool '{attribute.Name}' ({method.DeclaringType?.Name}.{method.Name}) has no "
                + "[Description]. The routing prompt is generated from it, so an undescribed "
                + "tool would be invisible to the model.");

        OrchestratorToolParameter[] parameters =
        [
            .. method.GetParameters()
                .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                .Select(parameter => new OrchestratorToolParameter(
                    parameter.Name ?? string.Empty,
                    parameter.GetCustomAttribute<DescriptionAttribute>()?.Description
                        ?? throw new InvalidOperationException(
                            $"Parameter '{parameter.Name}' of tool '{attribute.Name}' has no "
                            + "[Description]. The model cannot extract an argument it was "
                            + "never told about.")))
        ];

        return new OrchestratorToolDescriptor(attribute.Name, description, parameters);
    }
}
