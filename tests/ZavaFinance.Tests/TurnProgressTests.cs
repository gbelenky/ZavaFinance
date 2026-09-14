// Copyright (c) Microsoft Corporation.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZavaFinance.Channel;
using Xunit;

namespace ZavaFinance.Tests;

/// <summary>
/// Progress shown while a slow turn runs, and how the answer is delivered.
/// <para>
/// The two natural-language tools take tens of seconds to minutes. Silence for that long reads as
/// a failure, so these tests pin when the user hears something, that the answer always arrives,
/// and that progress can never become the reason a turn fails.
/// </para>
/// </summary>
public sealed class TurnProgressTests
{
    /// <summary>
    /// Streaming channels get an informative update immediately, and the answer replaces it as
    /// the stream's final message rather than appearing beneath a status that never resolves.
    /// </summary>
    [Fact]
    public async Task StreamingChannelUsesInformativeUpdatesAndEndsTheStream()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await WaitForAsync(() => turn.Stream.Updates.Count >= 1);

            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        Assert.NotEmpty(turn.Stream.Updates);
        Assert.True(turn.Stream.Ended);
        Assert.Equal("the answer", turn.Stream.FinalMessage?.Text);

        // The answer must not also be sent as a separate message.
        Assert.Empty(turn.Messages);
    }

    /// <summary>
    /// Microsoft 365 Copilot accepts typing activities and renders nothing, so a non-streaming
    /// channel has to be told in words. Measured on a real turn: 21 typing activities sent, 0
    /// failed, nothing visible to the user.
    /// </summary>
    [Fact]
    public async Task NonStreamingChannelFallsBackToMessagesAndTyping()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = false };

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await WaitForAsync(() => turn.Messages.Count >= 1 && turn.TypingCount >= 1);

            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        Assert.Contains("the answer", turn.Messages);
        Assert.False(turn.Stream.Ended);
    }

    /// <summary>
    /// Whatever the channel supports, the answer is delivered exactly once.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnswerIsAlwaysDeliveredExactlyOnce(bool streaming)
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = streaming };

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        int delivered =
            turn.Messages.Count(m => m == "the answer")
            + (turn.Stream.FinalMessage?.Text == "the answer" ? 1 : 0);

        Assert.Equal(1, delivered);
    }

    /// <summary>
    /// Nothing queued after the stream ends: the SDK treats that as an error, not a no-op.
    /// </summary>
    [Fact]
    public async Task NoUpdatesAreQueuedAfterCompletion()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await progress.CompleteAsync("the answer", CancellationToken.None);

            int afterCompletion = turn.Stream.Updates.Count;

            time.Advance(TimeSpan.FromMinutes(3));
            await Task.Delay(50);

            Assert.Equal(afterCompletion, turn.Stream.Updates.Count);
        }
    }

    /// <summary>
    /// Progress never names a tool: routing happens inside the hosted agent, so this host does
    /// not know which one is running, and a confidently wrong message is worse than a vague one.
    /// </summary>
    [Fact]
    public async Task ProgressNeverNamesATool()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };

        await using (TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            for (int i = 0; i < 30; i++)
            {
                time.Advance(TimeSpan.FromSeconds(5));
                await Task.Delay(5);
            }
        }

        foreach (string text in turn.Stream.Updates.Concat(turn.Messages))
        {
            Assert.DoesNotContain("KPIpedia", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("get_", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A channel that rejects progress must still deliver the answer. Presentation must never
    /// lose something the user has already waited a minute for.
    /// </summary>
    [Fact]
    public async Task DeliverySurvivesAFailingStream()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };
        turn.Stream.ThrowOnEnd = true;

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        // Fell back to a plain message rather than losing it.
        Assert.Contains("the answer", turn.Messages);
    }

    /// <summary>
    /// The regression this class exists for, reproducing the sequence measured on Microsoft 365
    /// Copilot: the acknowledgement and the first informative update are created (201), and then
    /// every later streamed frame — including the answer — is accepted and discarded (202). The
    /// user is left looking at a progress line forever.
    /// <para>
    /// Note that the stream <i>does</i> have an id here, because the first frame was created. That
    /// is precisely why the id is not a usable signal, and why the check is on the final message.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnswerIsResentWhenTheChannelDiscardsTheStreamedFinalMessage()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };

        // First streamed frame is created; everything after it is dropped.
        turn.StreamedActivitiesCreatedBeforeDropping = 1;

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await WaitForAsync(() => turn.Stream.Updates.Count >= 1);

            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        // The stream had an id, so the old check would have concluded "delivered".
        Assert.False(string.IsNullOrEmpty(turn.Stream.StreamId));

        // What matters: the answer reached the user as an ordinary message.
        Assert.Contains("the answer", turn.Messages);
    }

    /// <summary>
    /// The same fallback when the channel creates nothing for the stream at all.
    /// </summary>
    [Fact]
    public async Task AnswerIsResentWhenTheChannelRendersNothingForTheStream()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };
        turn.StreamedActivitiesCreateResources = false;

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await WaitForAsync(() => turn.Stream.Updates.Count >= 1);

            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        Assert.Contains("the answer", turn.Messages);
    }

    /// <summary>
    /// The converse: where the channel does render the streamed answer, it must not also be sent
    /// as an ordinary message. A duplicated answer is its own defect.
    /// </summary>
    [Fact]
    public async Task AnswerIsNotResentWhenTheChannelRendersTheStreamedFinalMessage()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await WaitForAsync(() => turn.Stream.Updates.Count >= 1);

            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        Assert.True(turn.Stream.Ended);
        Assert.DoesNotContain("the answer", turn.Messages);
    }

    /// <summary>
    /// A fast tool can answer before the first update is flushed. Ending a stream that never
    /// started sends nothing, so the answer has to go out as an ordinary message.
    /// </summary>
    [Fact]
    public async Task AnswerIsDeliveredWhenTheStreamNeverStarted()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };
        turn.Stream.StreamStarts = false;

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        Assert.Contains("the answer", turn.Messages);
    }

    /// <summary>
    /// Once the channel is seen discarding streamed frames, later progress updates must switch to
    /// ordinary messages. On Microsoft 365 Copilot only the first streamed frame is created, so
    /// without this a one-to-three-minute turn goes visibly silent after the first update.
    /// </summary>
    [Fact]
    public async Task ProgressSwitchesToMessagesOnceStreamedFramesAreDiscarded()
    {
        var time = new FakeTimeProvider();
        var turn = new RecordingTurnContext { Streaming = true };

        // Matches the measured Copilot behaviour: first frame created, rest discarded.
        turn.StreamedActivitiesCreatedBeforeDropping = 1;

        await using (TurnProgress progress = TurnProgress.Start(turn, NullLogger.Instance, time))
        {
            await WaitForAsync(() => turn.Stream.Updates.Count >= 1);

            // Drive the clock past the later nudges.
            time.Advance(TimeSpan.FromSeconds(20));
            await WaitForAsync(() => turn.Stream.Updates.Count >= 2);

            time.Advance(TimeSpan.FromSeconds(120));
            await WaitForAsync(() => turn.Messages.Count >= 1);

            await progress.CompleteAsync("the answer", CancellationToken.None);
        }

        // A later nudge was delivered as an ordinary message rather than vanishing.
        Assert.Contains(turn.Messages, m => m.Contains("Still going", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    private sealed class RecordingStream : IStreamingResponse
    {
        private readonly Lock _gate = new();
        private readonly List<string> _updates = [];

        public bool ThrowOnEnd { get; set; }

        /// <summary>
        /// Whether the channel creates a message resource for the stream's activities.
        /// <para>
        /// Teams does, and the id it returns is where <see cref="StreamId"/> comes from.
        /// Microsoft 365 Copilot does not: it accepts the stream's typing-class activities with
        /// <c>202 Accepted</c> and creates nothing, so the id is never assigned and every frame —
        /// including the final answer — is discarded.
        /// </para>
        /// </summary>
        public bool ChannelCreatesMessages { get; set; } = true;

        public bool Ended { get; private set; }

        public IReadOnlyList<string> Updates
        {
            get { lock (_gate) { return [.. _updates]; } }
        }

        public bool IsStreamingChannel { get; set; }

        public IActivity FinalMessage { get; set; } = null!;

        /// <summary>The context this stream sends through, as the real SDK does.</summary>
        public RecordingTurnContext Context { get; set; } = null!;

        public Task QueueInformativeUpdateAsync(
            string text, CancellationToken cancellationToken = default)
        {
            if (Ended)
            {
                throw new InvalidOperationException("The stream has already ended.");
            }

            lock (_gate)
            {
                _updates.Add(text);
            }

            return SendThroughContextAsync(StreamFrame(ActivityTypes.Typing, text), cancellationToken);
        }

        public async Task<StreamingResponseResult> EndStreamAsync(
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnEnd)
            {
                throw new InvalidOperationException("Stream could not be ended.");
            }

            Ended = true;

            if (!IsStreamStarted())
            {
                // The SDK returns without sending when no stream was ever started.
                return StreamingResponseResult.NotStarted;
            }

            // The final message is a message activity that still carries stream info, which is
            // exactly how the channel tells it apart from an ordinary message.
            await SendThroughContextAsync(
                StreamFrame(ActivityTypes.Message, FinalMessage?.Text ?? string.Empty),
                cancellationToken);

            return StreamingResponseResult.Success;
        }

        /// <summary>
        /// A streamed activity. Real streamed activities carry a StreamInfo entity; ordinary
        /// messages do not, and that is what lets the channel treat the two differently.
        /// </summary>
        private static IActivity StreamFrame(string type, string text) => new Activity
        {
            Type = type,
            Text = text,
            Entities = [new Entity { Type = StreamInfoEntityType }]
        };

        internal const string StreamInfoEntityType = "streaminfo";

        private async Task SendThroughContextAsync(
            IActivity activity, CancellationToken cancellationToken)
        {
            ResourceResponse response = await Context.SendActivityAsync(activity, cancellationToken);

            // The SDK adopts the id of the first resource the channel actually creates.
            if (string.IsNullOrEmpty(StreamId))
            {
                StreamId = response.Id;
            }
        }

        public bool IsStreamStarted() => StreamStarts && Updates.Count > 0;

        /// <summary>
        /// Whether queueing an update actually starts the stream. Set false to pin the case where
        /// the answer arrives before the stream is running, which the SDK reports as
        /// <see cref="StreamingResponseResult.NotStarted"/> and sends nothing for.
        /// </summary>
        public bool StreamStarts { get; set; } = true;

        public int UpdatesSent() => Updates.Count;

        public Task<bool> SendStreamTimedOutNotification(
            string message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        // The interface declares these non-nullable, so the fake matches it rather than warning.
        public int Interval { get; set; }
        public int InitialDelay { get; set; }

        /// <summary>
        /// The id the channel returns for the first message it creates for the stream. Null until
        /// the channel actually creates one, which is the whole point of
        /// <see cref="ChannelCreatesMessages"/>.
        /// </summary>
        public string StreamId { get; set; } = null!;

        // Not exercised by TurnProgress.
        public int EndStreamTimeout { get; set; }
        public string Message => null!;
        public bool FeedbackLoopEnabled { get; set; }
        public string FeedbackLoopType { get; set; } = null!;
        public bool? EnableGeneratedByAILabel { get; set; }
        public SensitivityUsageInfo? SensitivityLabel { get; set; }
        public List<ClientCitation>? Citations { get; set; }
        public string StreamingTakingTooLongMessage { get; set; } = null!;

        public void AddAttachment(Attachment attachment) => throw new NotSupportedException();
        public void AddCitation(ClientCitation citation) => throw new NotSupportedException();
        public void AddCitation(Citation citation, int position) => throw new NotSupportedException();
        public void AddCitations(IList<Citation> citations) => throw new NotSupportedException();
        public void AddCitations(IList<ClientCitation> citations) => throw new NotSupportedException();
        public void QueueTextChunk(string text) => throw new NotSupportedException();
        public Task ResetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingTurnContext : ITurnContext
    {
        private readonly Lock _gate = new();
        private readonly List<string> _messages = [];
        private readonly List<SendActivitiesHandler> _handlers = [];

        public RecordingTurnContext() => Stream.Context = this;

        public RecordingStream Stream { get; } = new();

        public bool Streaming
        {
            get => Stream.IsStreamingChannel;
            init => Stream.IsStreamingChannel = value;
        }

        public int TypingCount { get; private set; }

        /// <summary>
        /// Text the channel rendered as an <b>ordinary</b> message. Streamed frames are excluded,
        /// so an assertion on this is an assertion that the user actually saw the text.
        /// </summary>
        public IReadOnlyList<string> Messages
        {
            get { lock (_gate) { return [.. _messages]; } }
        }

        /// <summary>
        /// Whether the channel creates a resource for streamed activities. Teams does. Microsoft
        /// 365 Copilot accepts them with 202 and creates nothing, which is the defect under test.
        /// </summary>
        public bool StreamedActivitiesCreateResources { get; set; } = true;

        /// <summary>
        /// Lets a test reproduce the measured Copilot sequence, where the first streamed frame is
        /// created and every later one is discarded.
        /// </summary>
        public int StreamedActivitiesCreatedBeforeDropping { get; set; } = int.MaxValue;

        private int _streamedSeen;

        public IStreamingResponse StreamingResponse => Stream;

        public Task<ResourceResponse> SendActivityAsync(
            IActivity activity, CancellationToken cancellationToken = default)
        {
            Func<Task<ResourceResponse[]>> next = () => DeliverAsync([activity]);

            // Handlers wrap the delivery, outermost first, exactly as the SDK composes them.
            for (int i = _handlers.Count - 1; i >= 0; i--)
            {
                SendActivitiesHandler handler = _handlers[i];
                Func<Task<ResourceResponse[]>> inner = next;

                next = () => handler(this, [activity], inner);
            }

            return Continue(next);

            static async Task<ResourceResponse> Continue(Func<Task<ResourceResponse[]>> pipeline)
            {
                ResourceResponse[] responses = await pipeline();

                return responses.Length > 0 ? responses[0] : new ResourceResponse();
            }
        }

        private Task<ResourceResponse[]> DeliverAsync(IReadOnlyList<IActivity> activities)
        {
            var responses = new ResourceResponse[activities.Count];

            lock (_gate)
            {
                for (int i = 0; i < activities.Count; i++)
                {
                    IActivity activity = activities[i];

                    bool streamed = activity.Entities?.Any(e =>
                        string.Equals(
                            e.Type,
                            RecordingStream.StreamInfoEntityType,
                            StringComparison.OrdinalIgnoreCase)) == true;

                    bool created = streamed
                        ? StreamedActivitiesCreateResources
                          && _streamedSeen++ < StreamedActivitiesCreatedBeforeDropping
                        : true;

                    if (activity.IsType(ActivityTypes.Typing))
                    {
                        TypingCount++;
                    }
                    else if (!streamed && !string.IsNullOrEmpty(activity.Text))
                    {
                        _messages.Add(activity.Text);
                    }

                    // A created resource comes back with an id; a discarded one does not.
                    responses[i] = created
                        ? new ResourceResponse { Id = $"resource-{Guid.NewGuid():N}" }
                        : new ResourceResponse();
                }
            }

            return Task.FromResult(responses);
        }

        public Task<ResourceResponse> SendActivityAsync(
            string textReplyToSend,
            string? speak = null,
            string inputHint = "acceptingInput",
            CancellationToken cancellationToken = default)
            => SendActivityAsync(MessageFactory.Text(textReplyToSend), cancellationToken);

        // TurnProgress reads Activity to address the typing indicator.
        public IActivity Activity { get; } = new Activity
        {
            Type = ActivityTypes.Event,
            ChannelId = "msteams",
            ServiceUrl = "https://example.invalid",
            Conversation = new ConversationAccount { Id = "19:conversation@thread.tacv2" },
            From = new ChannelAccount { Id = "user-1" },
            Recipient = new ChannelAccount { Id = "bot-1" }
        };

        public System.Security.Claims.ClaimsIdentity Identity => throw new NotSupportedException();
        public IChannelAdapter Adapter => throw new NotSupportedException();
        public bool Responded => throw new NotSupportedException();
        public TurnContextStateCollection StackState => throw new NotSupportedException();
        public TurnContextStateCollection Services => throw new NotSupportedException();

        public Task<ResourceResponse[]> SendActivitiesAsync(
            IActivity[] activities, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ResourceResponse> UpdateActivityAsync(
            IActivity activity, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteActivityAsync(
            string activityId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteActivityAsync(
            ConversationReference conversationReference,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ITurnContext OnSendActivities(SendActivitiesHandler handler)
        {
            _handlers.Add(handler);

            return this;
        }

        public ITurnContext OnUpdateActivity(UpdateActivityHandler handler)
            => throw new NotSupportedException();

        public ITurnContext OnDeleteActivity(DeleteActivityHandler handler)
            => throw new NotSupportedException();

        public Task<ResourceResponse> TraceActivityAsync(
            string name,
            object? value = null,
            string? valueType = null,
            string? label = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
