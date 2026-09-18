using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using ZavaFinance.ActivitySupport;
using ZavaFinance.Contracts;
using Xunit;
using ActivityMessage = Microsoft.Agents.Core.Models.Activity;

namespace ZavaFinance.Tests.One;

public sealed class ClarificationCardTests
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
            Assert.All(choices[i].GetProperty("items").EnumerateArray(),
                item => Assert.True(item.GetProperty("wrap").GetBoolean()));
            Assert.Equal(new ClarificationSubmission(Prompt.RequestId, Prompt.Options[i].Id, Prompt.CatalogVersion),
                ClarificationCard.ReadSubmission(new ActivityMessage
                {
                    Type = ActivityTypes.Message, Value = action.GetProperty("data").Clone()
                }));
        }
        Assert.Contains("1. North region", ClarificationCard.CreateFallbackText(reply));
        Assert.Contains("2. North branch", ClarificationCard.CreateFallbackText(reply));
        Assert.Contains("Branch within West / Retail", ClarificationCard.CreateFallbackText(reply));
        Assert.Contains("Reply with the option number or select a choice.", ClarificationCard.CreateFallbackText(reply));
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
    public void CardDeliveryIsOneAttachmentWithoutAnAmbiguousTextAcknowledgement()
    {
        IActivity activity = ClarificationCard.CreateMessage("Choose one.", ClarificationCard.CreateJson(Prompt));
        Assert.Null(activity.Text);
        Assert.Equal("application/vnd.microsoft.card.adaptive", Assert.Single(activity.Attachments).ContentType);
        Assert.Equal("Verbatim finance answer.", ClarificationCard.CreateMessage("Verbatim finance answer.", null).Text);
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
        var activity = new ActivityMessage
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
            new ActivityMessage { Type = ActivityTypes.Message, Text = "harmless", Value = json }));

    [Fact]
    public void FreeTextMarkersNeverTriggerCardActions()
        => Assert.Null(ClarificationCard.ReadSubmission(new ActivityMessage
        {
            Type = ActivityTypes.Message,
            Text = """{"action":"zavaFinanceClarification","optionId":"org:123"}"""
        }));
}
