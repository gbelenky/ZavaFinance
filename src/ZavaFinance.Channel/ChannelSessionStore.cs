using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Storage;

namespace ZavaFinance.Channel;

public sealed class ChannelSessionState : IStoreItem
{
    public string? HostedAgentConversationId { get; set; }
    public string? ETag { get; set; }
}

[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter<TurnDeliveryStatus>))]
public enum TurnDeliveryStatus { Pending, AnswerReady, Delivered }

/// <summary>
/// Private channel storage only. Delivered records are content-free replay tombstones;
/// pending questions and cached answers must never be put in Durable Task history.
/// </summary>
public sealed class TurnDeliveryRecord : IStoreItem
{
    public TurnDeliveryStatus Status { get; set; }
    public string? Question { get; set; }
    public string? Answer { get; set; }
    public string? ETag { get; set; }
}

public sealed class ChannelSessionStore(IStorage storage)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static string SessionKey(string sessionKey) => $"orchestrator/{sessionKey}";
    private static string TurnKey(string sessionKey, string turnId) => $"pending/{sessionKey}/{turnId}";
    private static string LegacyCompletionKey(string sessionKey, string turnId)
        => "turn-complete-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionKey}:{turnId}")));

    public async Task<ChannelSessionState> LoadAsync(string sessionKey, CancellationToken ct)
    {
        string key = SessionKey(sessionKey);
        IDictionary<string, object> items = await storage.ReadAsync([key], ct);
        return items.TryGetValue(key, out object? value)
            ? Read<ChannelSessionState>(value) : new ChannelSessionState();
    }

    public Task SaveAsync(string sessionKey, ChannelSessionState state, CancellationToken ct)
        => storage.WriteAsync(new Dictionary<string, object> { [SessionKey(sessionKey)] = state }, ct);

    public Task ResetAsync(string sessionKey, CancellationToken ct)
        => storage.DeleteAsync([SessionKey(sessionKey)], ct);

    /// <summary>
    /// Insert only: a duplicate inbound message must never replace an answer awaiting delivery,
    /// or revive a delivered turn. The return value controls the one initial acknowledgement.
    /// </summary>
    public async Task<bool> CreateTurnAsync(
        string sessionKey, string turnId, string question, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (await ReadTurnAsync(sessionKey, turnId, ct) is not null)
            {
                return false;
            }

            try
            {
                await WriteTurnAsync(sessionKey, turnId, new TurnDeliveryRecord
                {
                    Status = TurnDeliveryStatus.Pending,
                    Question = question
                }, ct);
                return true;
            }
            catch (EtagException) when (attempt < 2) { }
        }
    }

    public async Task<TurnDeliveryRecord?> ReadTurnAsync(
        string sessionKey, string turnId, CancellationToken ct)
    {
        string key = TurnKey(sessionKey, turnId);
        string legacyKey = LegacyCompletionKey(sessionKey, turnId);
        for (int attempt = 0; ; attempt++)
        {
            IDictionary<string, object> items = await storage.ReadAsync([key, legacyKey], ct);
            TurnDeliveryRecord? turn = items.TryGetValue(key, out object? value)
                ? Read<TurnDeliveryRecord>(value) : null;

            // Old completion wins even if an inbound retry had recreated its pending record.
            if (items.ContainsKey(legacyKey))
            {
                turn ??= new TurnDeliveryRecord();
                turn.Status = TurnDeliveryStatus.Delivered;
                turn.Question = null;
                turn.Answer = null;
                try
                {
                    await WriteTurnAsync(sessionKey, turnId, turn, ct);
                    await storage.DeleteAsync([legacyKey], ct);
                    // Reload the new ETag before returning a migrated snapshot.
                    continue;
                }
                catch (EtagException) when (attempt < 2) { continue; }
            }

            if (turn is not null)
            {
                // Legacy PendingTurn has no status. Never discard its cached answer just because
                // the stored type was defined in another assembly.
                if (turn.Status == TurnDeliveryStatus.Pending && turn.Answer is not null)
                {
                    turn.Status = TurnDeliveryStatus.AnswerReady;
                }

                if (!Enum.IsDefined(turn.Status)
                    || (turn.Status == TurnDeliveryStatus.Pending && string.IsNullOrWhiteSpace(turn.Question))
                    || (turn.Status == TurnDeliveryStatus.AnswerReady && string.IsNullOrWhiteSpace(turn.Answer)))
                {
                    throw new InvalidDataException("Channel delivery record is invalid.");
                }
            }

            return turn;
        }
    }

    public Task SaveAnswerAsync(
        string sessionKey, string turnId, TurnDeliveryRecord turn, string answer, CancellationToken ct)
    {
        if (turn.Status != TurnDeliveryStatus.Pending || string.IsNullOrWhiteSpace(answer))
        {
            throw new InvalidOperationException("Only a pending turn can receive an answer.");
        }

        return WriteTurnAsync(sessionKey, turnId, new TurnDeliveryRecord
        {
            Status = TurnDeliveryStatus.AnswerReady,
            Answer = answer,
            ETag = turn.ETag
        }, ct);
    }

    public Task MarkDeliveredAsync(
        string sessionKey, string turnId, TurnDeliveryRecord turn, CancellationToken ct)
    {
        if (turn.Status != TurnDeliveryStatus.AnswerReady)
        {
            throw new InvalidOperationException("Only a cached answer can be delivered.");
        }

        return WriteTurnAsync(sessionKey, turnId, new TurnDeliveryRecord
        {
            Status = TurnDeliveryStatus.Delivered,
            ETag = turn.ETag
        }, ct);
    }

    private Task WriteTurnAsync(
        string sessionKey, string turnId, TurnDeliveryRecord turn, CancellationToken ct)
        => storage.WriteAsync(new Dictionary<string, object> { [TurnKey(sessionKey, turnId)] = turn }, ct);

    private static T Read<T>(object value) where T : class
    {
        // Plain typed deserialization ignores old SDK CLR metadata and unrelated agent fields.
        // It never asks Type.GetType/Assembly.Load to interpret the blob.
        string json = value is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidDataException("Channel state could not be read.");
    }
}
