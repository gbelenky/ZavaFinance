using System.Net;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZavaFinance.Channel;
using ZavaFinance.Contracts;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class ChannelMcsDeliveryTests
{
    private static readonly ClarificationPrompt Prompt = new(
        "request-margin", "kpi", "Which margin do you mean?",
        [new("kpi:gross-margin", "Gross Margin", "Gross profit as a share of revenue"),
         new("kpi:operating-margin", "Operating Margin", "Operating income as a share of revenue")],
        "catalog-1");

    private static readonly OrchestratorTurnRequest Request = new()
    {
        SessionKey = "s", TurnId = "t", IdempotencyKey = "s:t",
        ConversationRecordId = "conversation", ChannelId = "mcs"
    };

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task InstalledMcsConnectorPropagatesEmptyAcknowledgmentsWithoutInventingIds(HttpStatusCode status)
    {
        var transport = new McsTransport { Status = status, ReturnEmptyBody = true };
        using var connector = Connector(transport);
        using var context = Context(connector);
        IActivity mixed = MessageFactory.Text("1. Gross Margin\n2. Operating Margin");
        mixed.Attachments =
        [
            new Attachment
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = JsonSerializer.Deserialize<JsonElement>(ClarificationCard.CreateJson(Prompt))
            }
        ];

        ResourceResponse response = await context.SendActivityAsync(mixed);

        Assert.True(string.IsNullOrEmpty(response.Id));
        JsonElement posted = Assert.Single(transport.Activities);
        Assert.Equal(mixed.Text, posted.GetProperty("text").GetString());
        Assert.Single(posted.GetProperty("attachments").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleCardAcknowledgmentCommitsDeliveryIncludingPreviouslyCachedCards(bool cached)
    {
        var transport = new McsTransport();
        using var connector = Connector(transport);
        using var context = Context(connector);
        var store = new ChannelSessionStore(new ChannelTestStorage());
        var hosted = new HostedAgent();
        var processor = Processor(store, hosted);
        await store.CreateTurnAsync("s", "t", "margin", CancellationToken.None);
        if (cached)
        {
            var reply = new FinanceReply("Choose a margin.", Prompt);
            await store.SaveAnswerAsync("s", "t", (await store.ReadTurnAsync("s", "t", CancellationToken.None))!,
                ClarificationCard.CreateFallbackText(reply), CancellationToken.None, ClarificationCard.CreateJson(Prompt));
        }

        await processor.ProcessAsync(Request, context,
            _ => cached ? throw new Exception("Do not reacquire a token") : Task.FromResult("assertion"),
            CancellationToken.None);
        await processor.ProcessAsync(Request, context, _ => throw new Exception("Already delivered"), CancellationToken.None);

        AssertSingleCard(Assert.Single(transport.Activities));
        Assert.Equal(cached ? 0 : 1, hosted.Calls);
        Assert.Equal(TurnDeliveryStatus.Delivered, (await store.ReadTurnAsync("s", "t", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task EmptySingleCardAcknowledgmentStillRequiresRetryOfTheSameCachedPayload()
    {
        var transport = new McsTransport { ReturnEmptyBody = true };
        using var connector = Connector(transport);
        using var context = Context(connector);
        var store = new ChannelSessionStore(new ChannelTestStorage());
        var hosted = new HostedAgent();
        var processor = Processor(store, hosted);
        await store.CreateTurnAsync("s", "t", "margin", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(
            Request, context, _ => Task.FromResult("assertion"), CancellationToken.None));

        AssertSingleCard(Assert.Single(transport.Activities));
        var ready = (await store.ReadTurnAsync("s", "t", CancellationToken.None))!;
        Assert.Equal(TurnDeliveryStatus.AnswerReady, ready.Status);
        Assert.Contains("1. Gross Margin", ready.Answer);
        transport.ReturnEmptyBody = false;
        await processor.ProcessAsync(Request, context,
            _ => throw new Exception("Do not reacquire a token"), CancellationToken.None);
        await processor.ProcessAsync(Request, context,
            _ => throw new Exception("Already delivered"), CancellationToken.None);

        Assert.Equal(2, transport.Activities.Count);
        Assert.All(transport.Activities, AssertSingleCard);
        Assert.Equal(transport.Activities[0].GetProperty("attachments").GetRawText(),
            transport.Activities[1].GetProperty("attachments").GetRawText());
        Assert.Equal(1, hosted.Calls);
        Assert.Equal(TurnDeliveryStatus.Delivered, (await store.ReadTurnAsync("s", "t", CancellationToken.None))!.Status);
    }

    private static void AssertSingleCard(JsonElement activity)
    {
        Assert.True(!activity.TryGetProperty("text", out JsonElement text) || text.ValueKind == JsonValueKind.Null);
        JsonElement card = Assert.Single(activity.GetProperty("attachments").EnumerateArray()).GetProperty("content");
        Assert.Contains("1. Gross Margin", card.GetProperty("fallbackText").GetString());
        JsonElement choice = card.GetProperty("body").EnumerateArray()
            .First(e => e.GetProperty("type").GetString() == "Container").GetProperty("selectAction");
        Assert.StartsWith("1. Gross Margin", choice.GetProperty("title").GetString());
        Assert.Equal("kpi:gross-margin", choice.GetProperty("data").GetProperty("optionId").GetString());
    }

    private static ChannelTurnProcessor Processor(ChannelSessionStore store, HostedAgent hosted)
        => new(store, hosted, new FakeTimeProvider(), NullLogger<ChannelTurnProcessor>.Instance);

    private static MCSConnectorClient Connector(McsTransport transport)
        => new(new Uri("https://connector.invalid/callback"), new ClientFactory(transport));

    private static TurnContext Context(IConnectorClient connector)
        => new(new McsAdapter(connector), new Activity
        {
            Type = ActivityTypes.Event, Id = "inbound-1", ChannelId = "mcs",
            ServiceUrl = "https://connector.invalid/callback",
            Conversation = new ConversationAccount { Id = "conversation-1" },
            From = new ChannelAccount { Id = "user-1" }, Recipient = new ChannelAccount { Id = "bot-1" }
        });

    private sealed class HostedAgent : IHostedAgentClient
    {
        public int Calls { get; private set; }
        public Task<string> CreateConversationAsync(CancellationToken cancellationToken) => Task.FromResult("conv_test");
        public Task<FinanceReply> AskAsync(string question, string userAssertion, string? conversationId,
            CancellationToken cancellationToken, ClarificationSubmission? submission = null)
        {
            Calls++;
            return Task.FromResult(new FinanceReply("1. Gross Margin\n2. Operating Margin", Prompt));
        }
    }

    private sealed class McsAdapter(IConnectorClient connector) : ChannelAdapter
    {
        public override async Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext, IActivity[] activities, CancellationToken cancellationToken)
        {
            var responses = new List<ResourceResponse>();
            foreach (IActivity activity in activities)
            {
                responses.Add(await connector.Conversations.SendToConversationAsync(activity, cancellationToken));
            }
            return responses.ToArray();
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class McsTransport : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool ReturnEmptyBody { get; set; }
        public List<JsonElement> Activities { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonElement activity = document.RootElement.Clone();
            Activities.Add(activity);
            // Reproduce the reported connector response for a mixed text/card activity.
            bool mixed = activity.TryGetProperty("text", out JsonElement text) && !string.IsNullOrEmpty(text.GetString())
                && activity.TryGetProperty("attachments", out JsonElement attachments) && attachments.GetArrayLength() > 0;
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(ReturnEmptyBody || mixed ? "" : """{"id":"connector-message-1"}""")
            };
        }
    }
}
