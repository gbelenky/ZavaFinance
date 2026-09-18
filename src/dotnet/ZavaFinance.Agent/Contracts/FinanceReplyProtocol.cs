using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZavaFinance.Contracts;

/// <summary>Versioned wire data only; a valid submission still requires server-side pending-state validation.</summary>
public static class FinanceReplyProtocol
{
    public const string Schema = "zava-finance.v1";
    public const string ReplyFormat = "zava-finance-v1";
    public const string ReplyFormatHeader = "x-client-reply-format";
    public const string ClarificationHeader = "x-client-clarification";
    public const int MaxOptions = 25;
    public const int MaxIdentifierLength = 128;
    public const int MaxTextLength = 100_000;
    public const int MaxPayloadBytes = 524_288;
    public const int MaxSubmissionBytes = 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public static string SerializeReply(FinanceReply reply)
    {
        Validate(reply);
        string json = JsonSerializer.Serialize(new ReplyEnvelope(Schema, reply.Text, reply.Clarification), JsonOptions);
        CheckSize(json, MaxPayloadBytes);
        return json;
    }

    public static FinanceReply DeserializeReply(string json)
    {
        ReplyEnvelope envelope = Read<ReplyEnvelope>(json, MaxPayloadBytes);
        if (envelope.Schema != Schema)
        {
            throw new InvalidDataException("Unsupported finance reply schema.");
        }
        var reply = new FinanceReply(envelope.Text, envelope.Clarification);
        Validate(reply);
        return reply;
    }

    public static string EncodeSubmission(ClarificationSubmission submission)
    {
        Validate(submission);
        string json = JsonSerializer.Serialize(submission, JsonOptions);
        CheckSize(json, MaxSubmissionBytes);
        return Convert.ToBase64String(StrictUtf8.GetBytes(json));
    }

    public static ClarificationSubmission DecodeSubmission(string encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > ((MaxSubmissionBytes + 2) / 3) * 4
            || encoded.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Invalid clarification header.");
        }
        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            var submission = Read<ClarificationSubmission>(StrictUtf8.GetString(bytes), MaxSubmissionBytes);
            Validate(submission);
            return submission;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("Invalid clarification header encoding.", ex);
        }
    }

    public static void Validate(FinanceReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        CheckText(reply.Text, nameof(reply.Text), MaxTextLength);
        if (reply.Clarification is not null) { Validate(reply.Clarification); }
    }

    public static void Validate(ClarificationPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        CheckIdentifier(prompt.RequestId);
        CheckIdentifier(prompt.CatalogVersion);
        if (prompt.Field is not ("org" or "kpi"))
        {
            throw new InvalidDataException("Unsupported clarification field.");
        }
        CheckText(prompt.Message, nameof(prompt.Message), 4000);
        if (prompt.Options is null || prompt.Options.Count is < 1 or > MaxOptions)
        {
            throw new InvalidDataException("Invalid clarification option count.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ClarificationOption? option in prompt.Options)
        {
            if (option is null) { throw new InvalidDataException("Missing clarification option."); }
            CheckIdentifier(option.Id);
            if (!ids.Add(option.Id)) { throw new InvalidDataException("Duplicate clarification option ID."); }
            CheckText(option.Label, nameof(option.Label), 200);
            if (option.Description is not null) { CheckText(option.Description, nameof(option.Description), 1000); }
        }
    }

    public static void Validate(ClarificationSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        CheckIdentifier(submission.RequestId);
        CheckIdentifier(submission.OptionId);
        CheckIdentifier(submission.CatalogVersion);
    }

    private static void CheckIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxIdentifierLength
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':' or '/')))
        {
            throw new InvalidDataException("Invalid clarification identifier.");
        }
    }

    private static void CheckText(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength
            || value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
        {
            throw new InvalidDataException($"Invalid finance {name}.");
        }
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException($"Invalid Unicode in finance {name}.", ex);
        }
    }

    private static void CheckSize(string json, int maximum)
    {
        try
        {
            if (json.Length > maximum || StrictUtf8.GetByteCount(json) > maximum)
            {
                throw new InvalidDataException("Finance payload exceeds the size limit.");
            }
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException("Finance payload is not valid Unicode.", ex);
        }
    }

    private static T Read<T>(string json, int maximum)
    {
        if (string.IsNullOrWhiteSpace(json)) { throw new InvalidDataException("Missing finance payload."); }
        CheckSize(json, maximum);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException("Missing finance payload.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed finance payload.", ex);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new InvalidDataException("Duplicate finance payload property."); }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) { RejectDuplicateProperties(item); }
        }
    }

    private sealed record ReplyEnvelope(string Schema, string Text, ClarificationPrompt? Clarification);
}
