using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Tools;
using Xunit;

namespace ZavaFinance.Tests.Routing;

public sealed class NativeToolInvocationTests
{
    [Fact]
    public async Task ExecutableFunctionBindsJsonArgumentsAndInjectsCancellation()
    {
        var target = new SampleTools();
        AIFunction function = Assert.Single(FinanceToolCatalog.Bind(target));
        using var cancellation = new CancellationTokenSource();

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["value"] = JsonSerializer.SerializeToElement("Revenue")
        }, cancellation.Token);

        Assert.Equal("Revenue|default", result);
        Assert.Equal(cancellation.Token, target.Token);
    }

    [Fact]
    public async Task ExecutableFunctionPreservesExplicitNullForNullableArguments()
    {
        var target = new SampleTools();
        AIFunction function = Assert.Single(FinanceToolCatalog.Bind(target));

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["value"] = JsonSerializer.SerializeToElement<string?>(null),
            ["optional"] = "specified"
        });

        Assert.Equal("|specified", result);
    }

    [Fact]
    public void StatementSchemaCarriesTurnDateAndTimeInterpretationRules()
    {
        var tools = new FinanceTools(() => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            new OrchestratorSessionState(), new DateOnly(2026, 9, 18));
        AIFunction statement = tools.Functions()
            .Single(function => function.Name == FinanceToolNames.StatementTool);

        Assert.Contains("2026-09-18", statement.Description);
        Assert.Contains("current year", statement.Description);
        Assert.Contains("conversation", statement.Description);
        Assert.Contains("last quarter", statement.Description);
    }

    private sealed class SampleTools
    {
        public CancellationToken Token { get; private set; }

        [OrchestratorTool("sample")]
        [Description("Exercise native executable argument binding.")]
        public Task<string> InvokeAsync(
            [Description("Nullable value.")] string? value,
            [Description("Optional value.")] string optional = "default",
            CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return Task.FromResult($"{value}|{optional}");
        }
    }
}
