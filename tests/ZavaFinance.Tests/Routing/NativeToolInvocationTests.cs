using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Tools;
using Xunit;

namespace ZavaFinance.Tests.Routing;

public sealed class NativeToolInvocationTests
{
    private const string ExactAnswer = "**Revenue**: \"298.0\"\r\n\r\n| A | B |\n|---|---|\n"
        + "| 1 | 2 |\n\n[Evidence](https://example.test/a?q=1&b=2)\nBackslash: \\";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnnotationAliasBindsNativeArgumentsAndReturnsUnserializedText(bool jsonArguments)
    {
        var tool = new SampleTool();
        using var cts = new CancellationTokenSource();
        var arguments = new Dictionary<string, object?>
        {
            ["text"] = jsonArguments ? JsonSerializer.SerializeToElement(ExactAnswer) : ExactAnswer,
            ["kpi"] = jsonArguments ? JsonSerializer.SerializeToElement("Revenue") : "Revenue",
            ["suffix"] = jsonArguments ? JsonSerializer.SerializeToElement("provided") : "provided"
        };

        string answer = await OrchestratorAgent.InvokeToolAsync(
            new FunctionCallContent("call-1", "echo_response", arguments), Factories(tool), cts.Token);

        Assert.Equal(ExactAnswer, answer);
        Assert.Equal("Revenue", tool.Kpi);
        Assert.Equal("provided", tool.Suffix);
        Assert.Equal(cts.Token, tool.CancellationToken);
        Assert.Equal(1, tool.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkAppliesMethodDefaultsAndAcceptsExplicitJsonNull(bool explicitNull)
    {
        var tool = new SampleTool();
        var arguments = new Dictionary<string, object?> { ["text"] = ExactAnswer };
        if (explicitNull)
        {
            arguments["kpi"] = JsonSerializer.SerializeToElement<string?>(null);
        }

        string answer = await OrchestratorAgent.InvokeToolAsync(
            new FunctionCallContent("call-1", "echo_response", arguments),
            Factories(tool), CancellationToken.None);

        Assert.Equal(ExactAnswer, answer);
        Assert.Null(tool.Kpi);
        Assert.Equal("default", tool.Suffix);
    }

    [Theory]
    [InlineData(nameof(SampleTool.RespondAsync))]
    [InlineData(nameof(SampleTool.Unannotated))]
    [InlineData("ECHO_RESPONSE")]
    public async Task UnregisteredNamesNeverConstructTools(string name)
    {
        await Assert.ThrowsAsync<ToolSelectionException>(() => OrchestratorAgent.InvokeToolAsync(
            new FunctionCallContent("call-1", name),
            new Dictionary<Type, Func<object>>
            {
                [typeof(SampleTool)] = () => throw new InvalidOperationException("Must not construct.")
            }, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeInvocationNeverConstructsTools()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrchestratorAgent.InvokeToolAsync(
            new FunctionCallContent("call-1", "echo_response"),
            new Dictionary<Type, Func<object>>
            {
                [typeof(SampleTool)] = () => throw new InvalidOperationException("Must not construct.")
            }, cts.Token));
    }

    [Fact]
    public async Task CancellationDuringInvocationReachesTheAnnotatedMethod()
    {
        var tool = new SampleTool();
        using var cts = new CancellationTokenSource();
        Task<string> invocation = OrchestratorAgent.InvokeToolAsync(
            new FunctionCallContent("call-1", "wait_for_cancellation"), Factories(tool), cts.Token);
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(cts.Token, tool.CancellationToken);
    }

    [Fact]
    public async Task ToolExceptionsPropagateWithoutWrappingOrFallbackText()
    {
        var tool = new SampleTool();
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrchestratorAgent.InvokeToolAsync(new FunctionCallContent("call-1", "fail"),
                Factories(tool), CancellationToken.None));

        Assert.Same(tool.Failure, error);
    }

    [Fact]
    public async Task NonTextResultsFailExplicitly()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrchestratorAgent.InvokeToolAsync(new FunctionCallContent("call-1", "number"),
                Factories(new SampleTool()), CancellationToken.None));

        Assert.Contains("did not return a text response", error.Message);
    }

    private static Dictionary<Type, Func<object>> Factories(SampleTool tool) => new()
    {
        [typeof(SampleTool)] = () => tool,
        [typeof(UnselectedTool)] = () => throw new InvalidOperationException("Must not construct.")
    };

    private sealed class SampleTool
    {
        public int Calls { get; private set; }
        public string? Kpi { get; private set; }
        public string? Suffix { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InvalidOperationException Failure { get; } = new("Expected tool failure.");

        [OrchestratorTool("echo_response")]
        [Description("Echo the supplied text.")]
        public Task<string> RespondAsync(
            [Description("Text to return.")] string text,
            [Description("Optional KPI.")] string? kpi = null,
            [Description("Optional suffix.")] string suffix = "default",
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Kpi = kpi;
            Suffix = suffix;
            CancellationToken = cancellationToken;
            return Task.FromResult(text);
        }

        [OrchestratorTool("wait_for_cancellation")]
        [Description("Wait until cancelled.")]
        public async Task<string> WaitAsync(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "Unexpected completion.";
        }

        [OrchestratorTool("fail")]
        [Description("Throw a tool error.")]
        public Task<string> FailAsync() => Task.FromException<string>(Failure);

        [OrchestratorTool("number")]
        [Description("Return an invalid non-text result.")]
        public int Number() => 42;

        public string Unannotated() => throw new InvalidOperationException("Must not invoke.");
    }

    private sealed class UnselectedTool
    {
        [OrchestratorTool("unselected")]
        [Description("An unselected tool.")]
        public string Run() => throw new InvalidOperationException("Must not invoke.");
    }
}
