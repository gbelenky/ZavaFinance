using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using ZavaFinance.Contracts;

namespace ZavaFinance.Channel;

public static class ClarificationCard
{
    public const string SubmitAction = "zavaFinanceClarification";
    public const string SubmissionQuestion = "I submitted a clarification choice.";
    private const int MaxCardBytes = 28_000;
    private const int MaxActivityValueBytes = 8192;
    private const string ContentType = "application/vnd.microsoft.card.adaptive";
    private static readonly JsonSerializerOptions ActivityValueOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8
    };

    public static string CreateJson(ClarificationPrompt prompt)
    {
        FinanceReplyProtocol.Validate(prompt);
        string label = prompt.Field == "org" ? "Choose an organization" : "Choose a KPI";
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = prompt.Message, ["wrap"] = true, ["weight"] = "Bolder" },
            new JsonObject { ["type"] = "TextBlock", ["text"] = label, ["wrap"] = true }
        };
        for (int index = 0; index < prompt.Options.Count; index++)
        {
            ClarificationOption option = prompt.Options[index];
            string title = $"{index + 1}. {option.Label}";
            var items = new JsonArray
            {
                new JsonObject { ["type"] = "TextBlock", ["text"] = title, ["wrap"] = true, ["weight"] = "Bolder" }
            };
            if (!string.IsNullOrWhiteSpace(option.Description))
            {
                items.Add(new JsonObject
                {
                    ["type"] = "TextBlock", ["text"] = option.Description,
                    ["wrap"] = true, ["spacing"] = "Small", ["isSubtle"] = true
                });
                title += $" — {option.Description}";
            }
            body.Add(new JsonObject
            {
                ["type"] = "Container", ["style"] = "emphasis", ["spacing"] = "Small",
                ["items"] = items,
                ["selectAction"] = new JsonObject
                {
                    ["type"] = "Action.Submit", ["title"] = title, ["associatedInputs"] = "none",
                    ["data"] = new JsonObject
                    {
                        ["schema"] = FinanceReplyProtocol.Schema, ["action"] = SubmitAction,
                        ["requestId"] = prompt.RequestId, ["catalogVersion"] = prompt.CatalogVersion,
                        ["optionId"] = option.Id
                    }
                }
            });
        }
        body.Add(new JsonObject
        {
            ["type"] = "TextBlock", ["wrap"] = true,
            ["text"] = "Click an option to use it, or reply with its number. Type reset to start over."
        });
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.3",
            ["fallbackText"] = CreateFallbackText(new FinanceReply(prompt.Message, prompt)),
            ["body"] = body
        };
        string json = card.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > MaxCardBytes)
        {
            throw new InvalidDataException("Clarification card exceeds the channel size limit.");
        }
        return json;
    }

    public static string CreateFallbackText(FinanceReply reply)
    {
        FinanceReplyProtocol.Validate(reply);
        if (reply.Clarification is not { } prompt) { return reply.Text; }
        string options = string.Join("\n", prompt.Options.Select((option, index) =>
            $"{index + 1}. {option.Label}"
            + (string.IsNullOrWhiteSpace(option.Description) ? "" : $" — {option.Description}")));
        return $"{prompt.Message}\n\n{options}\n\nReply with the option number or select a choice. Type reset to start over.";
    }

    public static IActivity CreateMessage(string text, string? cardJson)
    {
        if (cardJson is null) { return MessageFactory.Text(text); }
        if (Encoding.UTF8.GetByteCount(cardJson) > MaxCardBytes)
        {
            throw new InvalidDataException("Cached clarification card exceeds the channel size limit.");
        }
        using JsonDocument document = JsonDocument.Parse(cardJson);
        // Mixed text/card callbacks can return no single message ID in MCS. Keep numbering
        // inside the card and send one attachment, including when replaying a cached answer.
        return MessageFactory.Attachment(
            new Attachment { ContentType = ContentType, Content = document.RootElement.Clone() });
    }

    /// <summary>Parses channel transport shapes, never text and never a resolved hierarchy ID.</summary>
    public static ClarificationSubmission? ReadSubmission(IActivity activity)
    {
        if (activity.Value is null) { return null; }
        if (activity.Type != ActivityTypes.Message && !IsSupportedInvoke(activity))
        {
            throw new InvalidDataException("Unsupported clarification activity.");
        }
        try
        {
            string json = activity.Value as string ?? JsonSerializer.Serialize(activity.Value, ActivityValueOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaxActivityValueBytes)
            {
                throw new InvalidDataException("Clarification activity exceeds the size limit.");
            }
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement data = document.RootElement;
            RejectDuplicateProperties(data);
            if (activity.Type == ActivityTypes.Invoke && activity.Name == "adaptiveCard/action")
            {
                JsonElement action = data.GetProperty("action");
                string? type = action.GetProperty("type").GetString();
                if (type is not ("Action.Execute" or "Action.Submit")
                    || (action.TryGetProperty("verb", out JsonElement verb)
                        && verb.ValueKind != JsonValueKind.Null && verb.GetString() != SubmitAction))
                {
                    throw new InvalidDataException("Unsupported clarification action.");
                }
                data = action.GetProperty("data");
            }
            else if (activity.Type == ActivityTypes.Invoke && activity.Name == "task/submit")
            {
                data = data.GetProperty("data");
            }
            if (data.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Invalid clarification selection.");
            }
            string[] allowed = ["schema", "action", "requestId", "optionId", "catalogVersion"];
            if (data.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))
                || data.GetProperty("schema").GetString() != FinanceReplyProtocol.Schema
                || data.GetProperty("action").GetString() != SubmitAction)
            {
                throw new InvalidDataException("Invalid clarification selection.");
            }
            var submission = new ClarificationSubmission(
                data.GetProperty("requestId").GetString()!,
                data.GetProperty("optionId").GetString()!,
                data.GetProperty("catalogVersion").GetString()!);
            FinanceReplyProtocol.Validate(submission);
            return submission;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Malformed clarification selection.", ex);
        }
    }

    public static bool IsSupportedInvoke(IActivity activity)
        => activity.Type == ActivityTypes.Invoke && activity.Name is "adaptiveCard/action" or "task/submit";

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new InvalidDataException("Duplicate clarification property."); }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) { RejectDuplicateProperties(item); }
        }
    }
}
