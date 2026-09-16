using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZavaFinance.Channel;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Identity;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class ChannelDeliveryTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task InboundRetriesPreserveEveryDeliveryState()
    {
        var storage = new ChannelTestStorage();
        var store = new ChannelSessionStore(storage);
        Assert.True(await store.CreateTurnAsync("s", "t", "question", None));
        Assert.False(await store.CreateTurnAsync("s", "t", "changed", None));
        var pending = (await store.ReadTurnAsync("s", "t", None))!;
        Assert.Equal("question", pending.Question);
        await store.SaveAnswerAsync("s", "t", pending, "cached answer", None);
        Assert.False(await store.CreateTurnAsync("s", "t", "changed again", None));
        var ready = (await store.ReadTurnAsync("s", "t", None))!;
        Assert.Equal(TurnDeliveryStatus.AnswerReady, ready.Status);
        Assert.Equal("cached answer", ready.Answer);
        Assert.Null(ready.Question);
        await store.MarkDeliveredAsync("s", "t", ready, None);
        Assert.False(await store.CreateTurnAsync("s", "t", "revive", None));
        var delivered = (await store.ReadTurnAsync("s", "t", None))!;
        Assert.Equal(TurnDeliveryStatus.Delivered, delivered.Status);
        Assert.Null(delivered.Answer);
        Assert.Null(delivered.Question);
        Assert.Single(storage.Items);
    }

    [Fact]
    public async Task FailedDeliveryReusesCachedAnswerWithoutTokenOrHostedCalls()
    {
        var (store, storage, hosted, processor) = Create();
        await store.CreateTurnAsync("s", "t", "private question", None);
        var failing = new ChannelTestTurnContext { FailText = "private answer" };
        await Assert.ThrowsAsync<IOException>(() =>
            processor.ProcessAsync(Request(), failing, _ => Task.FromResult("assertion"), None));
        Assert.DoesNotContain("assertion", JsonSerializer.Serialize(storage.Items));
        Assert.Equal(TurnDeliveryStatus.AnswerReady, (await store.ReadTurnAsync("s", "t", None))!.Status);
        Assert.False(await store.CreateTurnAsync("s", "t", "duplicate", None));
        var retry = new ChannelTestTurnContext();
        await processor.ProcessAsync(Request(), retry, _ => throw new Exception("Must not acquire token"), None);
        await processor.ProcessAsync(Request(), retry, _ => throw new Exception("Must not acquire token"), None);
        Assert.Equal(["private answer"], retry.Messages);
        Assert.Equal(1, hosted.Calls);
        Assert.Equal(TurnDeliveryStatus.Delivered, (await store.ReadTurnAsync("s", "t", None))!.Status);
    }

    [Fact]
    public async Task MissingMessageResourceIdLeavesTheAnswerReadyForRetry()
    {
        var (store, _, hosted, processor) = Create();
        await store.CreateTurnAsync("s", "t", "question", None);
        var unconfirmed = new ChannelTestTurnContext { MissingResourceId = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(Request(), unconfirmed, _ => Task.FromResult("assertion"), None));
        Assert.Equal(1, unconfirmed.SendAttempts);
        Assert.Equal(TurnDeliveryStatus.AnswerReady, (await store.ReadTurnAsync("s", "t", None))!.Status);

        var retry = new ChannelTestTurnContext();
        await processor.ProcessAsync(Request(), retry, _ => throw new Exception("Do not reacquire tokens"), None);
        Assert.Equal(["private answer"], retry.Messages);
        Assert.Equal(1, hosted.Calls);
    }

    [Fact]
    public async Task FinalOrdinaryMessageWaitsForInFlightProgressAndNoUpdatesFollowIt()
    {
        var (store, storage, hosted, _) = Create();
        var time = new FakeTimeProvider();
        var processor = new ChannelTurnProcessor(store, hosted, time, NullLogger<ChannelTurnProcessor>.Instance);
        await store.CreateTurnAsync("s", "t", "question", None);
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hosted.Ask = ct => reply.Task.WaitAsync(ct);
        storage.AfterAnswerWrite = () => cached.SetResult();
        var turn = new ChannelTestTurnContext
        {
            BeforeSend = async (activity, _) =>
            {
                if (activity.Text.StartsWith("Still working", StringComparison.Ordinal))
                {
                    started.SetResult();
                    await release.Task;
                }
            }
        };
        Task processing = processor.ProcessAsync(Request(), turn, _ => Task.FromResult("assertion"), None);
        time.Advance(TimeSpan.FromSeconds(15));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reply.SetResult("private answer");
        await cached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(processing.IsCompleted);
        Assert.Empty(turn.Messages);
        release.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, turn.Messages.Count);
        Assert.Equal("private answer", turn.Messages[^1]);
        Assert.All(turn.Activities, activity =>
        {
            Assert.Equal(ActivityTypes.Message, activity.Type);
            Assert.True(activity.Entities is null || activity.Entities.Count == 0);
        });
    }

    [Fact]
    public async Task FailureAfterSendBeforeCommitCanDuplicateDeliveryButNeverRecomputesAnswer()
    {
        var (store, storage, hosted, processor) = Create();
        await store.CreateTurnAsync("s", "t", "question", None);
        storage.FailDeliveredWrite = true;
        var first = new ChannelTestTurnContext();
        await Assert.ThrowsAsync<IOException>(() =>
            processor.ProcessAsync(Request(), first, _ => Task.FromResult("assertion"), None));
        Assert.Equal(["private answer"], first.Messages);
        Assert.Equal(TurnDeliveryStatus.AnswerReady, (await store.ReadTurnAsync("s", "t", None))!.Status);
        storage.FailDeliveredWrite = false;
        var retry = new ChannelTestTurnContext();
        await processor.ProcessAsync(Request(), retry, _ => throw new Exception(), None);
        Assert.Equal(["private answer"], retry.Messages);
        Assert.Equal(1, hosted.Calls);
        // This is intentionally not described as exactly-once external delivery.
    }

    [Fact]
    public async Task FailedAnswerPersistenceDoesNotDeliverAndAlwaysStopsProgress()
    {
        var (store, storage, hosted, _) = Create();
        var time = new FakeTimeProvider();
        var processor = new ChannelTurnProcessor(store, hosted, time, NullLogger<ChannelTurnProcessor>.Instance);
        await store.CreateTurnAsync("s", "t", "question", None);
        storage.FailAnswerWrite = true;
        var turn = new ChannelTestTurnContext();
        await Assert.ThrowsAsync<IOException>(() =>
            processor.ProcessAsync(Request(), turn, _ => Task.FromResult("assertion"), None));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(turn.Messages);
        Assert.Equal(TurnDeliveryStatus.Pending, (await store.ReadTurnAsync("s", "t", None))!.Status);
        storage.FailAnswerWrite = false;
        await processor.ProcessAsync(Request(), turn, _ => Task.FromResult("assertion"), None);
        Assert.Equal(["private answer"], turn.Messages);
        Assert.Equal(2, hosted.Calls); // No durable cached answer existed in this retry window.
    }

    [Fact]
    public async Task CancellationDoesNotCacheOrDeliverAFailure()
    {
        var (store, _, hosted, processor) = Create();
        await store.CreateTurnAsync("s", "t", "question", None);
        using var cancellation = new CancellationTokenSource();
        hosted.Ask = ct =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("unreachable");
        };
        var turn = new ChannelTestTurnContext();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(Request(), turn, _ => Task.FromResult("assertion"), cancellation.Token));
        Assert.Empty(turn.Messages);
        Assert.Equal(TurnDeliveryStatus.Pending, (await store.ReadTurnAsync("s", "t", None))!.Status);
    }

    [Fact]
    public async Task CancellationAfterCachingPreservesAnswerForRetry()
    {
        var (store, storage, _, processor) = Create();
        await store.CreateTurnAsync("s", "t", "question", None);
        using var cancellation = new CancellationTokenSource();
        storage.AfterAnswerWrite = cancellation.Cancel;
        var turn = new ChannelTestTurnContext();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(Request(), turn, _ => Task.FromResult("assertion"), cancellation.Token));
        Assert.Empty(turn.Messages);
        Assert.Equal(TurnDeliveryStatus.AnswerReady, (await store.ReadTurnAsync("s", "t", None))!.Status);
        storage.AfterAnswerWrite = null;
        await processor.ProcessAsync(Request(), turn, _ => throw new Exception(), None);
        Assert.Equal(["private answer"], turn.Messages);
    }

    [Theory]
    [InlineData("timeout", "That took too long to answer. Please try again.")]
    [InlineData("failure", "Something went wrong while answering that. Please try again.")]
    [InlineData("empty", "Something went wrong while answering that. Please try again.")]
    public async Task UserReceivesExplicitFailureOrTimeoutOnce(string kind, string expected)
    {
        var (store, _, hosted, processor) = Create();
        await store.CreateTurnAsync("s", "t", "question", None);
        hosted.Ask = _ => kind switch
        {
            "timeout" => throw new TaskCanceledException("HTTP timeout"),
            "failure" => throw new HttpRequestException("downstream failure"),
            _ => Task.FromResult("")
        };
        var turn = new ChannelTestTurnContext();
        await processor.ProcessAsync(Request(), turn, _ => Task.FromResult("assertion"), None);
        await processor.ProcessAsync(Request(), turn, _ => throw new Exception(), None);
        Assert.Equal([expected], turn.Messages);
        Assert.Equal(1, hosted.Calls);
    }

    [Theory]
    [InlineData("legacy cached answer")]
    [InlineData("1. North region\n2. South region\nReply with the number.")]
    public async Task LegacyRecordsLoadWithoutTheirClrTypesAndKeepCachedAnswers(string answer)
    {
        var (store, storage, hosted, processor) = Create();
        storage.Seed("orchestrator/s", """
            {"$type":"ZavaFinance.Core.Agent.OrchestratorSessionState","$typeAssembly":"ZavaFinance.Agent",
             "hostedAgentConversationId":"conv_legacy","lastKpiName":"discard","agentSessionJson":"discard"}
            """);
        storage.Seed("pending/s/t", $$"""
            {"$type":"ZavaFinance.Core.Agent.PendingTurn","$typeAssembly":"ZavaFinance.Agent",
             "question":"private question","answer":{{JsonSerializer.Serialize(answer)}}}
            """);
        Assert.Equal("conv_legacy", (await store.LoadAsync("s", None)).HostedAgentConversationId);
        var turn = new ChannelTestTurnContext();
        await processor.ProcessAsync(Request(), turn, _ => throw new Exception(), None);
        Assert.Equal([answer], turn.Messages);
        Assert.Equal(0, hosted.Calls);
        Assert.Equal(TurnDeliveryStatus.Delivered, (await store.ReadTurnAsync("s", "t", None))!.Status);
    }

    [Fact]
    public async Task TypedCachedFinalFinanceAnswerPreservesAllFormattingIncludingNumberedFacts()
    {
        var (store, _, hosted, processor) = Create();
        const string answer = "1. Revenue: **€1,234.50**\n2. Expenses: **€456.78**\n\n[Source](https://example.invalid)";
        await store.CreateTurnAsync("s", "t", "question", None);
        await store.SaveAnswerAsync("s", "t", (await store.ReadTurnAsync("s", "t", None))!, answer, None);
        var turn = new ChannelTestTurnContext();
        await processor.ProcessAsync(Request(), turn, _ => throw new Exception("Must not acquire token"), None);
        Assert.Equal(answer, Assert.Single(turn.Activities).Text);
        Assert.True(turn.Activities[0].Attachments is null || turn.Activities[0].Attachments.Count == 0);
        Assert.Equal(0, hosted.Calls);
    }

    [Fact]
    public async Task LegacyPendingQuestionStillRunsAndReusesPlatformConversation()
    {
        var (store, storage, hosted, processor) = Create();
        storage.Seed("orchestrator/s", """{"HostedAgentConversationId":"conv_legacy"}""");
        storage.Seed("pending/s/t", """{"Question":"question","Answer":null}""");
        await processor.ProcessAsync(Request(), new ChannelTestTurnContext(), _ => Task.FromResult("assertion"), None);
        Assert.Equal("conv_legacy", hosted.Conversations.Single());
        Assert.Equal(0, hosted.Creates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCompletionBecomesDeliveredEvenIfOldInboundRetryRecreatedPending(bool withPending)
    {
        var (store, storage, hosted, processor) = Create();
        if (withPending) { storage.Seed("pending/s/t", """{"Question":"duplicate","Answer":"obsolete"}"""); }
        string completionKey = "turn-complete-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("s:t")));
        storage.Seed(completionKey, """
            {"$type":"ZavaFinance.Channel.OrchestratorActivities+TurnCompletionRecord","$typeAssembly":"ZavaFinance.Channel"}
            """);
        var turn = new ChannelTestTurnContext();
        await processor.ProcessAsync(Request(), turn, _ => throw new Exception(), None);
        Assert.Empty(turn.Messages);
        Assert.Equal(0, hosted.Calls);
        Assert.False(storage.Items.ContainsKey(completionKey));
        Assert.Equal(TurnDeliveryStatus.Delivered, (await store.ReadTurnAsync("s", "t", None))!.Status);
    }

    [Fact]
    public async Task LegacyCompletionSurvivesAMigrationWriteFailure()
    {
        var (store, storage, _, _) = Create();
        string completionKey = "turn-complete-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("s:t")));
        storage.Seed(completionKey, "{}");
        storage.FailDeliveredWrite = true;
        await Assert.ThrowsAsync<IOException>(() => store.ReadTurnAsync("s", "t", None));
        Assert.True(storage.Items.ContainsKey(completionKey));
        Assert.False(storage.Items.ContainsKey("pending/s/t"));
        storage.FailDeliveredWrite = false;
        Assert.Equal(TurnDeliveryStatus.Delivered, (await store.ReadTurnAsync("s", "t", None))!.Status);
        Assert.False(storage.Items.ContainsKey(completionKey));
    }

    [Fact]
    public async Task MissingResourceConfirmationRetainsCachedAnswer()
    {
        var (store, _, hosted, processor) = Create();
        await store.CreateTurnAsync("s", "t", "question", None);
        var turn = new ChannelTestTurnContext { MissingResourceId = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(Request(), turn, _ => Task.FromResult("assertion"), None));
        Assert.Equal(1, turn.SendAttempts);
        Assert.Equal(1, hosted.Calls);
        Assert.Equal(TurnDeliveryStatus.AnswerReady, (await store.ReadTurnAsync("s", "t", None))!.Status);
    }

    [Fact]
    public void DeterministicIdsRemainCompatibleWithPreviouslyScheduledTurns()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var instanceId = typeof(DurableTaskOrchestratorTurnScheduler).GetMethod("InstanceIdFor", flags)!;
        Assert.Equal("turn-session-turn", instanceId.Invoke(null, ["session", "turn"]));
        var requestFactory = typeof(DurableTaskOrchestratorTurnScheduler).GetMethod("CreateRequest", flags)!;
        var request = (OrchestratorTurnRequest)requestFactory.Invoke(
            null, ["session", "turn", "record", "msteams"])!;
        Assert.Equal("session:turn", request.IdempotencyKey);
        var turnId = typeof(OrchestratorChannel).GetMethod("TurnIdFor", flags)!;
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("activity-id")))[..32];
        Assert.Equal(expected, turnId.Invoke(null, ["activity-id"]));
    }

    [Fact]
    public async Task MissingOrMalformedRecordsAreNotMisreportedAsDelivered()
    {
        var (store, storage, _, processor) = Create();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            processor.ProcessAsync(Request(), new ChannelTestTurnContext(), _ => Task.FromResult("assertion"), None));
        storage.Seed("pending/s/t", """{"$typeAssembly":"Removed.Assembly","unrecognized":"answer"}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadTurnAsync("s", "t", None));
    }

    [Fact]
    public async Task StaleWritesCannotReplaceACachedOrDeliveredAnswer()
    {
        var (store, _, _, _) = Create();
        await store.CreateTurnAsync("s", "t", "question", None);
        var stale = (await store.ReadTurnAsync("s", "t", None))!;
        await store.SaveAnswerAsync("s", "t", stale, "answer", None);
        await Assert.ThrowsAsync<EtagException>(() => store.SaveAnswerAsync("s", "t", stale, "overwrite", None));
        var ready = (await store.ReadTurnAsync("s", "t", None))!;
        await store.MarkDeliveredAsync("s", "t", ready, None);
        await Assert.ThrowsAsync<EtagException>(() => store.SaveAnswerAsync("s", "t", stale, "revive", None));
    }

    [Fact]
    public async Task ResetAndDeliveryAreIsolatedByTenantUserAndConversation()
    {
        var (store, storage, _, _) = Create();
        var keys = new SessionKeyProvider("offline-test-salt");
        string a = keys.GetSessionKey(CallerIdentity.Create("tenant-a", "user-a"), "conversation");
        string b = keys.GetSessionKey(CallerIdentity.Create("tenant-a", "user-b"), "conversation");
        string c = keys.GetSessionKey(CallerIdentity.Create("tenant-b", "user-a"), "conversation");
        string d = keys.GetSessionKey(CallerIdentity.Create("tenant-a", "user-a"), "other-conversation");
        foreach (string key in new[] { a, b, c, d })
        {
            await store.SaveAsync(key, new ChannelSessionState { HostedAgentConversationId = "conv_" + key }, None);
            await store.CreateTurnAsync(key, "same-turn", key, None);
        }
        await store.ResetAsync(a, None);
        Assert.Null((await store.LoadAsync(a, None)).HostedAgentConversationId);
        foreach (string key in new[] { b, c, d })
        {
            Assert.Equal("conv_" + key, (await store.LoadAsync(key, None)).HostedAgentConversationId);
            Assert.Equal(key, (await store.ReadTurnAsync(key, "same-turn", None))!.Question);
        }
        Assert.DoesNotContain(storage.Items.Keys, key => key.Contains("user-a") || key.Contains("tenant-a"));
    }

    [Fact]
    public void DurableRequestAndSessionSchemaContainOnlyTheirOwnIdentifiers()
    {
        Assert.Equal(
            ["ChannelId", "ConversationRecordId", "IdempotencyKey", "SessionKey", "TurnId"],
            typeof(OrchestratorTurnRequest).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["ETag", "HostedAgentConversationId"],
            typeof(ChannelSessionState).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain("assertion", JsonSerializer.Serialize(Request()), StringComparison.OrdinalIgnoreCase);
    }

    private static OrchestratorTurnRequest Request() => new()
    {
        SessionKey = "s", TurnId = "t", IdempotencyKey = "s:t",
        ConversationRecordId = "conversation-record", ChannelId = "msteams"
    };

    private static (ChannelSessionStore, ChannelTestStorage, TestHostedAgent, ChannelTurnProcessor) Create()
    {
        var storage = new ChannelTestStorage();
        var store = new ChannelSessionStore(storage);
        var hosted = new TestHostedAgent();
        return (store, storage, hosted,
            new ChannelTurnProcessor(store, hosted, new FakeTimeProvider(), NullLogger<ChannelTurnProcessor>.Instance));
    }

    private sealed class TestHostedAgent : IHostedAgentClient
    {
        public int Calls { get; private set; }
        public int Creates { get; private set; }
        public List<string?> Conversations { get; } = [];
        public Func<CancellationToken, Task<string>> Ask { get; set; } = _ => Task.FromResult("private answer");
        public Task<string> CreateConversationAsync(CancellationToken cancellationToken)
            => Task.FromResult("conv_test_" + ++Creates);
        public async Task<FinanceReply> AskAsync(string question, string userAssertion, string? conversationId,
            CancellationToken ct, ClarificationSubmission? submission = null)
        {
            Calls++;
            Conversations.Add(conversationId);
            return new FinanceReply(await Ask(ct));
        }
    }
}

internal sealed class ChannelTestStorage : IStorage
{
    public Dictionary<string, JsonObject> Items { get; } = [];
    private int _etag;
    public bool FailAnswerWrite { get; set; }
    public bool FailDeliveredWrite { get; set; }
    public Action? AfterAnswerWrite { get; set; }

    public void Seed(string key, string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document["ETag"] = (++_etag).ToString();
        Items[key] = document;
    }

    public Task<IDictionary<string, object>> ReadAsync(string[] keys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDictionary<string, object> result = keys.Where(Items.ContainsKey)
            .ToDictionary(key => key, key => (object)Items[key].DeepClone());
        return Task.FromResult(result);
    }

    public Task WriteAsync(IDictionary<string, object> changes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (key, value) in changes)
        {
            if (value is TurnDeliveryRecord { Status: TurnDeliveryStatus.AnswerReady } && FailAnswerWrite)
                throw new IOException("Answer persistence failed.");
            if (value is TurnDeliveryRecord { Status: TurnDeliveryStatus.Delivered } && FailDeliveredWrite)
                throw new IOException("Delivered persistence failed.");
            string? etag = ((IStoreItem)value).ETag;
            if (Items.TryGetValue(key, out var old)
                ? etag != old["ETag"]!.GetValue<string>() : etag is not null)
                throw new EtagException("Concurrent write.");
            Seed(key, JsonSerializer.Serialize(value));
            if (value is TurnDeliveryRecord { Status: TurnDeliveryStatus.AnswerReady })
                AfterAnswerWrite?.Invoke();
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string[] keys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string key in keys) Items.Remove(key);
        return Task.CompletedTask;
    }

    public async Task<IDictionary<string, T>> ReadAsync<T>(string[] keys, CancellationToken cancellationToken = default)
        where T : class
        => (await ReadAsync(keys, cancellationToken)).ToDictionary(p => p.Key, p => ((JsonObject)p.Value).Deserialize<T>()!);
    public Task WriteAsync<T>(IDictionary<string, T> changes, CancellationToken cancellationToken = default) where T : class
        => WriteAsync(changes.ToDictionary(p => p.Key, p => (object)p.Value), cancellationToken);
}
