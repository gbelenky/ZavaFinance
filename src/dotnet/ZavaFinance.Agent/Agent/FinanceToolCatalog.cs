using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ZavaFinance.Core.Tools;

namespace ZavaFinance.Core.Agent;

internal static class FinanceToolCatalog
{
    internal static IReadOnlyList<AIFunctionDeclaration> Declarations { get; } =
        Discover(typeof(FinanceTools));

    internal static IReadOnlyList<AIFunctionDeclaration> Discover(params Type[] types) =>
        Methods(types).Select(method =>
        {
            AIFunctionFactoryOptions options = Options(method);
            return AIFunctionFactory.CreateDeclaration(
                options.Name!, options.Description!, AIJsonUtilities.CreateFunctionJsonSchema(method));
        }).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<AIFunction> Bind(object target, DateOnly? today = null) =>
        Methods(target.GetType()).Select(method =>
        {
            AIFunctionFactoryOptions options = Options(method);
            if (options.Name == FinanceToolNames.StatementTool && today is not null)
                options.Description += $"\n\nUTC reference date: {today:yyyy-MM-dd}.\nCurrent calendar year: {today.Value.Year}.";
            return AIFunctionFactory.Create(method, target, options);
        }).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();

    private static IEnumerable<MethodInfo> Methods(params Type[] types) =>
        types.SelectMany(type => type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.IsDefined(typeof(OrchestratorToolAttribute), false));

    private static AIFunctionFactoryOptions Options(MethodInfo method)
    {
        string name = method.GetCustomAttribute<OrchestratorToolAttribute>()!.Name;
        string? description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (string.IsNullOrWhiteSpace(description) || method.GetParameters()
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Any(parameter => string.IsNullOrWhiteSpace(
                parameter.GetCustomAttribute<DescriptionAttribute>()?.Description)))
            throw new InvalidOperationException($"Tool '{name}' must describe its function and arguments.");
        return new() { Name = name, Description = description,
            MarshalResult = (result, _, _) => ValueTask.FromResult(result) };
    }

    internal static FunctionCallContent? Validate(IEnumerable<ChatMessage> messages)
    {
        ChatMessage[] response = messages.ToArray();
        FunctionCallContent[] calls = response.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>().ToArray();
        if (calls.Length == 0)
        {
            if (!response.Any(message => !string.IsNullOrWhiteSpace(message.Text)))
                throw new ToolSelectionException("The model returned neither a function call nor a response.");
            return null;
        }
        if (calls.Length != 1)
            throw new ToolSelectionException("Only one finance function may be called per turn.");
        FunctionCallContent call = calls[0];
        AIFunctionDeclaration tool = Declarations.SingleOrDefault(tool => tool.Name == call.Name)
            ?? throw new ToolSelectionException("The model selected an unknown function.");
        if (call.InformationalOnly || call.Exception is not null || string.IsNullOrWhiteSpace(call.CallId))
            throw new ToolSelectionException("The model returned a malformed function call.");
        JsonElement properties = tool.JsonSchema.GetProperty("properties");
        foreach ((string name, object? value) in call.Arguments ?? new Dictionary<string, object?>())
        {
            if (!properties.TryGetProperty(name, out JsonElement schema))
                throw new ToolSelectionException("The function call contains an unknown argument.");
            bool isNull = value is null || value is JsonElement { ValueKind: JsonValueKind.Null };
            JsonElement type = schema.GetProperty("type");
            bool nullable = type.ValueKind == JsonValueKind.Array
                && type.EnumerateArray().Any(item => item.GetString() == "null");
            if (isNull ? !nullable : value is not string
                && value is not JsonElement { ValueKind: JsonValueKind.String })
                throw new ToolSelectionException("A function argument has an invalid type.");
        }
        if (tool.JsonSchema.TryGetProperty("required", out JsonElement required))
            foreach (JsonElement parameter in required.EnumerateArray())
                if (call.Arguments is null || !call.Arguments.ContainsKey(parameter.GetString()!))
                    throw new ToolSelectionException("The function call is missing a required argument.");
        return call;
    }
}

internal sealed class ToolSelectionException(string message) : Exception(message);
