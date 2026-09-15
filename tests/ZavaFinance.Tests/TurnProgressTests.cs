using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZavaFinance.Channel;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class TurnProgressTests
{
    [Fact]
    public async Task FastCompletionHasNoExtraProgress()
    {
        var turn = new ChannelTestTurnContext();
        var progress = TurnProgress.Start(turn, NullLogger.Instance, new FakeTimeProvider());
        await progress.DisposeAsync();
        Assert.Empty(turn.Messages);
    }

    [Fact]
    public async Task TimedProgressUsesOrdinaryMessagesAndNeverNamesTools()
    {
        var time = new FakeTimeProvider();
        var turn = new ChannelTestTurnContext();
        await using var progress = TurnProgress.Start(turn, NullLogger.Instance, time);
        time.Advance(TimeSpan.FromSeconds(15));
        await ChannelTestTurnContext.WaitForAsync(() => turn.Messages.Count == 1);
        time.Advance(TimeSpan.FromSeconds(75));
        await ChannelTestTurnContext.WaitForAsync(() => turn.Messages.Count == 2);
        Assert.All(turn.Activities, activity =>
        {
            Assert.Equal(ActivityTypes.Message, activity.Type);
            Assert.True(activity.Entities is null || activity.Entities.Count == 0);
            Assert.DoesNotContain("KPIpedia", activity.Text);
            Assert.DoesNotContain("get_", activity.Text);
        });
    }

    [Fact]
    public async Task FailedProgressDoesNotPreventLaterUpdates()
    {
        var time = new FakeTimeProvider();
        var turn = new ChannelTestTurnContext { FailText = "Still working — this can take a minute." };
        await using var progress = TurnProgress.Start(turn, NullLogger.Instance, time);
        time.Advance(TimeSpan.FromSeconds(15));
        await ChannelTestTurnContext.WaitForAsync(() => turn.SendAttempts == 1);
        time.Advance(TimeSpan.FromSeconds(75));
        await ChannelTestTurnContext.WaitForAsync(() => turn.Messages.Count == 1);
        Assert.StartsWith("Still going.", turn.Messages[0]);
    }

    [Fact]
    public async Task CallerCancellationStopsProgress()
    {
        var time = new FakeTimeProvider();
        var turn = new ChannelTestTurnContext();
        using var cancellation = new CancellationTokenSource();
        await using var progress = TurnProgress.Start(turn, NullLogger.Instance, time, cancellation.Token);
        cancellation.Cancel();
        time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(turn.Messages);
    }

    [Fact]
    public async Task DisposalJoinsAnInFlightProgressSend()
    {
        var time = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = new ChannelTestTurnContext
        {
            BeforeSend = async (_, _) =>
            {
                started.SetResult();
                // An accepted send cannot be recalled by cancellation.
                await release.Task;
            }
        };
        var progress = TurnProgress.Start(turn, NullLogger.Instance, time);
        time.Advance(TimeSpan.FromSeconds(15));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal = progress.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        release.SetResult();
        await disposal;
        time.Advance(TimeSpan.FromHours(1));
        Assert.Single(turn.Messages);
    }

    [Fact]
    public async Task DisposeIsIdempotentAndStopsAllFutureProgress()
    {
        var time = new FakeTimeProvider();
        var turn = new ChannelTestTurnContext();
        var progress = TurnProgress.Start(turn, NullLogger.Instance, time);
        await Task.WhenAll(progress.DisposeAsync().AsTask(), progress.DisposeAsync().AsTask());
        time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(turn.Messages);
    }
}
