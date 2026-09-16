using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Xunit;

namespace ZavaFinance.Tests;

internal sealed class ChannelTestTurnContext : ITurnContext
{
    private readonly Lock _gate = new();
    private readonly List<IActivity> _activities = [];
    private int _sendAttempts;
    public int SendAttempts => Volatile.Read(ref _sendAttempts);
    public string? FailText { get; set; }
    public bool MissingResourceId { get; set; }
    public Func<IActivity, CancellationToken, Task>? BeforeSend { get; init; }
    public IReadOnlyList<IActivity> Activities { get { lock (_gate) { return [.. _activities]; } } }
    public IReadOnlyList<string> Messages => Activities.Select(a => a.Text).ToArray();

    public async Task<ResourceResponse> SendActivityAsync(
        IActivity activity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _sendAttempts);
        if (BeforeSend is not null) { await BeforeSend(activity, cancellationToken); }
        if (FailText is not null && activity.Text == FailText) { throw new IOException("Simulated delivery failure."); }
        lock (_gate) { _activities.Add(activity); }
        return new ResourceResponse { Id = MissingResourceId ? null! : $"message-{SendAttempts}" };
    }

    public Task<ResourceResponse> SendActivityAsync(
        string textReplyToSend, string? speak = null, string inputHint = "acceptingInput",
        CancellationToken cancellationToken = default)
        => SendActivityAsync(MessageFactory.Text(textReplyToSend), cancellationToken);

    public static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); }
        Assert.True(condition(), "Expected asynchronous event did not occur.");
    }

    public IActivity Activity { get; } = new Activity
    {
        Id = "activity-1",
        Type = ActivityTypes.Event,
        ChannelId = "msteams",
        ServiceUrl = "https://example.invalid",
        Conversation = new ConversationAccount { Id = "conversation-1" },
        From = new ChannelAccount { Id = "user-1" },
        Recipient = new ChannelAccount { Id = "bot-1" }
    };
    // Throwing here verifies that the ordinary-message implementation never inspects streams
    // or installs interception hooks, even when the actual channel supports streaming.
    public IStreamingResponse StreamingResponse => throw new NotSupportedException();
    public System.Security.Claims.ClaimsIdentity Identity { get; init; } = new(
        [new System.Security.Claims.Claim("aud", "bot-1")], "test-channel");
    public IChannelAdapter Adapter => throw new NotSupportedException();
    public bool Responded => Activities.Count > 0;
    public TurnContextStateCollection StackState { get; } = new();
    public TurnContextStateCollection Services { get; } = new();
    public Task<ResourceResponse[]> SendActivitiesAsync(IActivity[] activities, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<ResourceResponse> UpdateActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task DeleteActivityAsync(string activityId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task DeleteActivityAsync(ConversationReference conversationReference, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public ITurnContext OnSendActivities(SendActivitiesHandler handler) => throw new NotSupportedException();
    public ITurnContext OnUpdateActivity(UpdateActivityHandler handler) => throw new NotSupportedException();
    public ITurnContext OnDeleteActivity(DeleteActivityHandler handler) => throw new NotSupportedException();
    public Task<ResourceResponse> TraceActivityAsync(string name, object? value = null, string? valueType = null,
        string? label = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
