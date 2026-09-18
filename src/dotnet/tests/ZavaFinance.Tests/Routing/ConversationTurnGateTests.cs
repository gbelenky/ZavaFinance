using ZavaFinance.Core.Agent;
using Xunit;

namespace ZavaFinance.Tests.Routing;

public sealed class ConversationTurnGateTests
{
    [Fact]
    public async Task SamePartitionSerializesWhileOtherCallersRemainIndependent()
    {
        var gates = new ConversationTurnGate();
        IDisposable first = await gates.EnterAsync("caller-a", CancellationToken.None);
        Task<IDisposable> next = gates.EnterAsync("caller-a", CancellationToken.None);
        using IDisposable independent = await gates.EnterAsync("caller-b", CancellationToken.None);
        Assert.False(next.IsCompleted);

        first.Dispose();
        using IDisposable acquired = await next.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CancelledWaiterDoesNotLeakOrUnlockTheActivePartition()
    {
        var gates = new ConversationTurnGate();
        IDisposable first = await gates.EnterAsync("caller", CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        Task<IDisposable> waiting = gates.EnterAsync("caller", cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Task<IDisposable> next = gates.EnterAsync("caller", CancellationToken.None);
        Assert.False(next.IsCompleted);

        first.Dispose();
        using IDisposable acquired = await next.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
