using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZavaFinance.Channel;
using ZavaFinance.Contracts;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class ChannelClarificationTests
{
    private static readonly ClarificationPrompt Prompt = new(
        "request-1", "org", "Which organization do you mean?",
        [new("org:region:north", "North region", "Region within Corporate"),
         new("org:branch:north", "North branch", "Branch within West / Retail")],
        "catalog-2026-09");

    [Fact]
    public void SharedReplyAndSubmissionRoundTripWithoutTokenFields()
    {
        string json = FinanceReplyProtocol.SerializeReply(new FinanceReply("Choose an organization.", Prompt));
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(FinanceReplyProtocol.Schema, document.RootElement.GetProperty("schema").GetString());
        FinanceReply decoded = FinanceReplyProtocol.DeserializeReply(json);
        Assert.Equal(Prompt.Options, decoded.Clarification!.Options);
        Assert.Equal(Prompt.RequestId, decoded.Clarification.RequestId);
        var submission = new ClarificationSubmission(Prompt.RequestId, Prompt.Options[1].Id, Prompt.CatalogVersion);
        Assert.Equal(submission, FinanceReplyProtocol.DecodeSubmission(FinanceReplyProtocol.EncodeSubmission(submission)));
        Assert.Equal(["CatalogVersion", "OptionId", "RequestId"],
            typeof(ClarificationSubmission).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("""{"schema":"other","text":"x","clarification":null}""")]
    [InlineData("""{"schema":"zava-finance.v1","text":"x","clarification":null,"token":"secret"}""")]
    [InlineData("""{"schema":"zava-finance.v1","text":"x","text":"y","clarification":null}""")]
    [InlineData("""{"schema":"zava-finance.v1","Text":"x","clarification":null}""")]
    [InlineData("""{"schema":"zava-finance.v1","text":null,"clarification":null}""")]
    [InlineData("""{"schema":"zava-finance.v1","text":"x","clarification":{}}""")]
    [InlineData("""{"schema":"zava-finance.v1","text":"x"}""")]
    [InlineData("{")]
    public void MalformedTypedRepliesFailExplicitly(string json)
        => Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.DeserializeReply(json));

    [Fact]
    public void ContractLimitsAndUniquenessAreEnforced()
    {
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.Validate(Prompt with { Options = [] }));
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.Validate(Prompt with { Field = "hierarchyId" }));
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.Validate(Prompt with
        {
            Options = [Prompt.Options[0], Prompt.Options[0]]
        }));
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.Validate(Prompt with
        {
            Options = Enumerable.Range(0, 26).Select(i => new ClarificationOption($"id-{i}", "Label")).ToArray()
        }));
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.Validate(
            new ClarificationSubmission("request", "org\r\nInjected: true", "v1")));
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.Validate(
            new FinanceReply(new string('x', FinanceReplyProtocol.MaxTextLength + 1))));
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.DecodeSubmission("not-base64"));
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.DecodeSubmission(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"requestId":"r","optionId":"o","catalogVersion":"v","hierarchyId":"123"}"""))));
        Assert.Throws<InvalidDataException>(() => FinanceReplyProtocol.DecodeSubmission(
            new string('x', FinanceReplyProtocol.MaxSubmissionBytes * 2)));
    }

    [Theory]
    [InlineData("org", "Choose an organization")]
    [InlineData("kpi", "Choose a KPI")]
    public void ClickableItemsSubmitCanonicalIdsWithDescriptiveContext(string field, string label)
    {
        var reply = new FinanceReply("Please choose.", Prompt with { Field = field });
        using JsonDocument card = JsonDocument.Parse(ClarificationCard.CreateJson(reply.Clarification!));
        Assert.Equal("AdaptiveCard", card.RootElement.GetProperty("type").GetString());
        Assert.Equal("1.3", card.RootElement.GetProperty("version").GetString());
        Assert.False(card.RootElement.TryGetProperty("actions", out _));
        JsonElement[] body = card.RootElement.GetProperty("body").EnumerateArray().ToArray();
        Assert.Contains(body, e => e.TryGetProperty("text", out JsonElement text) && text.GetString() == label);
        Assert.DoesNotContain("Input.", card.RootElement.GetRawText());
        JsonElement[] choices = body.Where(e => e.GetProperty("type").GetString() == "Container").ToArray();
        Assert.Equal(Prompt.Options.Count, choices.Length);
        for (int i = 0; i < choices.Length; i++)
        {
            JsonElement action = choices[i].GetProperty("selectAction");
            Assert.Equal("Action.Submit", action.GetProperty("type").GetString());
            Assert.Equal("none", action.GetProperty("associatedInputs").GetString());
            Assert.StartsWith($"{i + 1}. {Prompt.Options[i].Label}", action.GetProperty("title").GetString());
            Assert.Contains(Prompt.Options[i].Description!, action.GetProperty("title").GetString());
            JsonElement items = choices[i].GetProperty("items");
            Assert.Equal($"{i + 1}. {Prompt.Options[i].Label}", items[0].GetProperty("text").GetString());
            Assert.Equal(Prompt.Options[i].Description, items[1].GetProperty("text").GetString());
            Assert.All(items.EnumerateArray(), item => Assert.True(item.GetProperty("wrap").GetBoolean()));
            Assert.Equal(new ClarificationSubmission(Prompt.RequestId, Prompt.Options[i].Id, Prompt.CatalogVersion),
                ClarificationCard.ReadSubmission(new Activity
                {
                    Type = ActivityTypes.Message, Value = action.GetProperty("data").Clone()
                }));
        }
        Assert.Contains("1. North region", ClarificationCard.CreateFallbackText(reply));
        Assert.Contains("2. North branch", ClarificationCard.CreateFallbackText(reply));
        Assert.Contains("Branch within West / Retail", ClarificationCard.CreateFallbackText(reply));
        Assert.Contains("Reply with the option number or select a choice.", ClarificationCard.CreateFallbackText(reply));
        Assert.DoesNotContain("number or label", ClarificationCard.CreateFallbackText(reply));
        Assert.Contains("1. North region", card.RootElement.GetProperty("fallbackText").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(25)]
    public void EveryOptionHasItsOwnClickableRowWithoutATopLevelActionLimit(int count)
    {
        var prompt = Prompt with
        {
            Options = Enumerable.Range(0, count).Select(i => new ClarificationOption($"id-{i}", "Same label")).ToArray()
        };
        string json = ClarificationCard.CreateJson(prompt);
        using JsonDocument card = JsonDocument.Parse(json);
        JsonElement[] rows = card.RootElement.GetProperty("body").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "Container").ToArray();
        Assert.Equal(count, rows.Length);
        Assert.Equal(count, rows.Select(row => row.GetProperty("selectAction").GetProperty("title").GetString()).Distinct().Count());
        Assert.All(rows, row => Assert.Single(row.GetProperty("items").EnumerateArray()));
        Assert.True(Encoding.UTF8.GetByteCount(json) <= 28_000);
    }

    [Fact]
    public void NumberedResultRemainsIntactAlongsideTheChannelRenderedCard()
    {
        const string text = "1. North region\n2. North branch\nReply with the option number.";
        FinanceReply reply = FinanceReplyProtocol.DeserializeReply(
            FinanceReplyProtocol.SerializeReply(new FinanceReply(text, Prompt)));
        Assert.Equal(text, reply.Text);
        IActivity activity = ClarificationCard.CreateMessage(reply.Text, ClarificationCard.CreateJson(reply.Clarification!));
        Assert.Null(activity.Text);
        Attachment attachment = Assert.Single(activity.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
        JsonElement row = ((JsonElement)attachment.Content).GetProperty("body").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "Container").ElementAt(1);
        Assert.Equal(Prompt.Options[1].Id, row.GetProperty("selectAction").GetProperty("data")
            .GetProperty("optionId").GetString());
    }

    [Theory]
    [InlineData("message")]
    [InlineData("messageBack")]
    [InlineData("adaptiveCard/action")]
    [InlineData("typedAdaptiveCard/action")]
    [InlineData("typedAdaptiveCard/submit")]
    [InlineData("task/submit")]
    public void SupportedSubmitShapesProduceOnlyUntrustedCanonicalSelection(string format)
    {
        var data = new
        {
            schema = FinanceReplyProtocol.Schema, action = ClarificationCard.SubmitAction,
            requestId = Prompt.RequestId, optionId = Prompt.Options[1].Id, catalogVersion = Prompt.CatalogVersion
        };
        var activity = new Activity
        {
            Type = format.StartsWith("message", StringComparison.Ordinal) ? ActivityTypes.Message : ActivityTypes.Invoke,
            Name = format.StartsWith("typedAdaptiveCard", StringComparison.Ordinal) ? "adaptiveCard/action" : format,
            Text = "/reset",
            Value = format switch
            {
                "adaptiveCard/action" => JsonSerializer.SerializeToElement(new
                {
                    action = new { type = "Action.Execute", verb = ClarificationCard.SubmitAction, data }
                }),
                "task/submit" => JsonSerializer.SerializeToElement(new { data }),
                "typedAdaptiveCard/action" => new AdaptiveCardInvokeValue
                {
                    Action = new AdaptiveCardInvokeAction
                    {
                        Type = "Action.Execute", Verb = ClarificationCard.SubmitAction, Data = data
                    }
                },
                "typedAdaptiveCard/submit" => new AdaptiveCardInvokeValue
                {
                    Action = new AdaptiveCardInvokeAction { Type = "Action.Submit", Data = data }
                },
                "messageBack" => JsonSerializer.Serialize(data),
                _ => data
            }
        };
        Assert.Equal(new ClarificationSubmission(Prompt.RequestId, Prompt.Options[1].Id, Prompt.CatalogVersion),
            ClarificationCard.ReadSubmission(activity));
    }

    [Theory]
    [InlineData("""{"hierarchyId":"org:123"}""")]
    [InlineData("""{"schema":"zava-finance.v1","action":"zavaFinanceClarification","requestId":"r","optionId":"o","catalogVersion":"v","hierarchyId":"123"}""")]
    [InlineData("""{"schema":"zava-finance.v1","action":"zavaFinanceClarification","requestId":"r","optionId":["o"],"catalogVersion":"v"}""")]
    [InlineData("""{"schema":"zava-finance.v1","action":"zavaFinanceClarification","requestId":"r","optionId":"o","optionId":"p","catalogVersion":"v"}""")]
    [InlineData("{")]
    public void InvalidSubmitsCannotBecomeTextOrResolvedHierarchyInputs(string json)
        => Assert.Throws<InvalidDataException>(() => ClarificationCard.ReadSubmission(
            new Activity { Type = ActivityTypes.Message, Text = "harmless", Value = json }));

    [Fact]
    public void FreeTextMarkersNeverTriggerCardActions()
        => Assert.Null(ClarificationCard.ReadSubmission(new Activity
        {
            Type = ActivityTypes.Message,
            Text = """{"action":"zavaFinanceClarification","optionId":"org:123"}"""
        }));

    [Fact]
    public async Task CachedCardDeliveryRetriesUseIdenticalPayloadWithoutRecomputation()
    {
        var storage = new ChannelTestStorage();
        var store = new ChannelSessionStore(storage);
        var hosted = new CardHostedAgent(new FinanceReply("Please choose.", Prompt));
        var processor = new ChannelTurnProcessor(store, hosted, new FakeTimeProvider(),
            NullLogger<ChannelTurnProcessor>.Instance);
        await store.CreateTurnAsync("s", "t", "question", CancellationToken.None);
        var failed = new ChannelTestTurnContext { MissingResourceId = true };
        var request = new OrchestratorTurnRequest
        {
            SessionKey = "s", TurnId = "t", IdempotencyKey = "s:t",
            ConversationRecordId = "conversation", ChannelId = "msteams"
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(request, failed, _ => Task.FromResult("assertion-not-for-storage"), CancellationToken.None));
        TurnDeliveryRecord ready = (await store.ReadTurnAsync("s", "t", CancellationToken.None))!;
        Assert.Equal(TurnDeliveryStatus.AnswerReady, ready.Status);
        Assert.NotNull(ready.CardJson);
        Assert.Contains("1. North region", ready.Answer);
        Assert.Null(failed.Activities[0].Text);
        Assert.Equal(ready.Answer, ((JsonElement)Assert.Single(failed.Activities[0].Attachments).Content)
            .GetProperty("fallbackText").GetString());
        Assert.Single(failed.Activities[0].Attachments);
        Assert.DoesNotContain("assertion-not-for-storage", JsonSerializer.Serialize(storage.Items));
        var retry = new ChannelTestTurnContext();
        await processor.ProcessAsync(request, retry, _ => throw new Exception("Must not acquire token"), CancellationToken.None);
        await processor.ProcessAsync(request, retry, _ => throw new Exception("Must not acquire token"), CancellationToken.None);
        Assert.Equal(1, hosted.Calls);
        Assert.Equal(JsonSerializer.Serialize(failed.Activities[0].Attachments),
            JsonSerializer.Serialize(Assert.Single(retry.Activities).Attachments));
        Assert.Null(retry.Activities[0].Text);
        TurnDeliveryRecord delivered = (await store.ReadTurnAsync("s", "t", CancellationToken.None))!;
        Assert.Equal(TurnDeliveryStatus.Delivered, delivered.Status);
        Assert.Null(delivered.CardJson);
        Assert.Null(delivered.Submission);
    }

    [Fact]
    public async Task PendingSubmissionsSurviveRetryAndAreForwardedNotResolvedByChannel()
    {
        var storage = new ChannelTestStorage();
        var store = new ChannelSessionStore(storage);
        var submission = new ClarificationSubmission("expired-request", "org:arbitrary", "stale-version");
        Assert.True(await store.CreateTurnAsync("s", "t", ClarificationCard.SubmissionQuestion,
            CancellationToken.None, submission));
        Assert.False(await store.CreateTurnAsync("s", "t", "replacement", CancellationToken.None,
            submission with { OptionId = "org:another" }));
        var hosted = new CardHostedAgent(new FinanceReply("That choice is no longer available. Please ask again."));
        var processor = new ChannelTurnProcessor(store, hosted, new FakeTimeProvider(),
            NullLogger<ChannelTurnProcessor>.Instance);
        await processor.ProcessAsync(new OrchestratorTurnRequest
        {
            SessionKey = "s", TurnId = "t", IdempotencyKey = "s:t",
            ConversationRecordId = "conversation", ChannelId = "msteams"
        }, new ChannelTestTurnContext(), _ => Task.FromResult("assertion"), CancellationToken.None);
        Assert.Equal(submission, hosted.Submission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedClientNegotiatesProtocolAndPreservesPlainFinanceAnswers(bool typed)
    {
        const string text = "Revenue **€1,234.50**\n\n[Source](https://example.invalid/source)";
        var handler = new RecordingHandler(typed ? FinanceReplyProtocol.SerializeReply(new FinanceReply(text)) : text);
        var client = CreateClient(handler);
        FinanceReply reply = await client.AskAsync("question", "signed-user-assertion", "conv_test", CancellationToken.None);
        Assert.Equal(text, reply.Text);
        Assert.Null(reply.Clarification);
        Assert.Equal(FinanceReplyProtocol.ReplyFormat, handler.Headers![FinanceReplyProtocol.ReplyFormatHeader]);
        Assert.DoesNotContain("signed-user-assertion", handler.Body);
    }

    [Theory]
    [InlineData("Select an organization:\n1. North\n2. South\nReply with the number.")]
    [InlineData("[Revenue](https://example.invalid/report) **€1,234.50**")]
    [InlineData("Please choose a KPI by name.")]
    public async Task LegacyPlaintextCompatibilityPreservesNumberingAndMarkdown(string text)
    {
        FinanceReply reply = await CreateClient(new RecordingHandler(text))
            .AskAsync("question", "assertion", null, CancellationToken.None);
        Assert.Equal(text, reply.Text);
        Assert.Null(reply.Clarification);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("North branch")]
    [InlineData("cancel")]
    public async Task PlainTextTurnsUseNegotiatedModeWithoutForgingASubmission(string text)
    {
        var handler = new RecordingHandler(FinanceReplyProtocol.SerializeReply(
            new FinanceReply("Choose an option.", Prompt)));
        await CreateClient(handler).AskAsync(text, "assertion", "conv_test", CancellationToken.None);
        Assert.Equal(FinanceReplyProtocol.ReplyFormat, handler.Headers![FinanceReplyProtocol.ReplyFormatHeader]);
        Assert.False(handler.Headers.ContainsKey(FinanceReplyProtocol.ClarificationHeader));
        using JsonDocument request = JsonDocument.Parse(handler.Body);
        Assert.Equal(text, request.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task SubmissionIsOnlyInForwardedHeaderAndNotFoundryInput()
    {
        var submission = new ClarificationSubmission("request-private", "org:selected", "v1");
        var handler = new RecordingHandler(FinanceReplyProtocol.SerializeReply(new FinanceReply("Done.")));
        await CreateClient(handler).AskAsync(ClarificationCard.SubmissionQuestion, "assertion", "conv_test",
            CancellationToken.None, submission);
        Assert.Equal(submission, FinanceReplyProtocol.DecodeSubmission(handler.Headers![FinanceReplyProtocol.ClarificationHeader]));
        Assert.DoesNotContain("request-private", handler.Body);
        Assert.DoesNotContain("org:selected", handler.Body);
        Assert.DoesNotContain("assertion", handler.Body);
    }

    [Fact]
    public async Task MalformedNegotiatedReplyIsNotReportedAsAnAnswer()
    {
        var handler = new RecordingHandler("""{"schema":"zava-finance.v1","text":"wrong","clarification":{}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateClient(handler).AskAsync("question", "assertion", "conv_test", CancellationToken.None));
    }

    [Fact]
    public async Task OldAgentCannotSilentlyAcceptAnUnsupportedCardSubmission()
    {
        var handler = new RecordingHandler("Legacy answer to a question it never understood");
        await Assert.ThrowsAsync<InvalidDataException>(() => CreateClient(handler).AskAsync(
            ClarificationCard.SubmissionQuestion, "assertion", "conv_test", CancellationToken.None,
            new ClarificationSubmission("r", "o", "v")));
    }

    private static FoundryHostedAgentClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler), new TestCredential(),
            new HostedAgentOptions { ResponsesEndpoint = "https://example.invalid/responses" },
            NullLogger<FoundryHostedAgentClient>.Instance);

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("workload-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RecordingHandler(string outputText) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        public Dictionary<string, string>? Headers { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { output_text = outputText }))
            };
        }
    }

    private sealed class CardHostedAgent(FinanceReply reply) : IHostedAgentClient
    {
        public int Calls { get; private set; }
        public ClarificationSubmission? Submission { get; private set; }
        public Task<string> CreateConversationAsync(CancellationToken cancellationToken) => Task.FromResult("conv_test");
        public Task<FinanceReply> AskAsync(string question, string userAssertion, string? conversationId,
            CancellationToken cancellationToken, ClarificationSubmission? submission = null)
        {
            Calls++;
            Submission = submission;
            return Task.FromResult(reply);
        }
    }
}
