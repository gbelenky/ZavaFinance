using System.Text.Json;
using Microsoft.Extensions.AI;
using ZavaFinance.Core.Agent;

namespace ZavaFinance.Tests.Routing;

internal static class FinanceTestTools
{
    // Wire/evaluation tests exercise MAF invocation without permissioned downstream services.
    internal static IReadOnlyList<AIFunction> Create()
    {
        var tools = new FinanceTools(() => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            new OrchestratorSessionState(), DateOnly.FromDateTime(DateTime.UtcNow));
        return tools.Functions().Select(function => (AIFunction)new Stub(function)).ToArray();
    }

    private sealed class Stub(AIFunction declaration) : AIFunction
    {
        public override string Name => declaration.Name;
        public override string Description => declaration.Description;
        public override JsonElement JsonSchema => declaration.JsonSchema;
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(FinanceHistory.WithheldResult);
    }
}
