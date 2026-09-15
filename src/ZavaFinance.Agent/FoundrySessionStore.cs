using System.Text.Json;
using Azure.AI.AgentServer.Core.Storage;
using Azure.Core;
using ZavaFinance.Core.Agent;

namespace ZavaFinance.Agent;

/// <summary>Typed session persistence in the hosted platform's state store.</summary>
public sealed class FoundrySessionStore : IAgentSessionStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storeName;
    private readonly TokenCredential _credential;
    private readonly int _ttlSeconds;
    private FoundryStateStore? _store;

    public FoundrySessionStore(string storeName, TokenCredential credential, TimeSpan itemTtl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        ArgumentNullException.ThrowIfNull(credential);
        if (itemTtl.TotalSeconds < 1 || itemTtl.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(itemTtl));
        }

        _storeName = storeName;
        _credential = credential;
        _ttlSeconds = (int)itemTtl.TotalSeconds;
    }

    public async Task<OrchestratorSessionState> LoadAsync(
        string sessionKey, CancellationToken cancellationToken)
    {
        string key = KeyFor(sessionKey);
        FoundryStateStore store = await GetStoreAsync(cancellationToken);
        StateStoreItem? item = await store.GetItemAsync(key, cancellationToken);
        if (item is null)
        {
            return new OrchestratorSessionState();
        }

        return item.Value.TryGetValue("item", out BinaryData? data)
            ? Deserialize(data)
            : throw new InvalidDataException("The stored agent session has no item field.");
    }

    public async Task SaveAsync(
        string sessionKey, OrchestratorSessionState state, CancellationToken cancellationToken)
    {
        string key = KeyFor(sessionKey);
        ArgumentNullException.ThrowIfNull(state);
        FoundryStateStore store = await GetStoreAsync(cancellationToken);
        await store.SetItemAsync(
            key,
            new Dictionary<string, BinaryData> { ["item"] = Serialize(state) },
            tags: null, ifMatch: null, requireExists: false, cancellationToken);
    }

    internal static string KeyFor(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        // Keep the original encoded key so existing conversations remain reachable.
        return $"orchestrator_{sessionKey}";
    }

    internal static BinaryData Serialize(OrchestratorSessionState state) =>
        BinaryData.FromObjectAsJson(state, Json);

    internal static OrchestratorSessionState Deserialize(BinaryData data)
    {
        using JsonDocument document = JsonDocument.Parse(data);
        JsonElement value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An agent session must be a JSON object.");
        }

        // Read the former generic envelope without loading arbitrary CLR types.
        if (value.TryGetProperty("type", out JsonElement type))
        {
            if (type.ValueKind != JsonValueKind.String
                || type.GetString()!.Split(',')[0] != "ZavaFinance.Core.Agent.OrchestratorSessionState")
            {
                throw new JsonException("The stored item is not an agent session.");
            }
            value = value.TryGetProperty("value", out JsonElement legacyValue)
                ? legacyValue
                : throw new JsonException("The stored agent session has no value.");
        }

        return value.Deserialize<OrchestratorSessionState>(Json)
            ?? throw new JsonException("The stored agent session is null.");
    }

    private async Task<FoundryStateStore> GetStoreAsync(CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            return _store;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Lazy initialization keeps external storage availability out of readiness.
            _store ??= await FoundryStateStore.GetOrCreateAsync(
                _storeName, _credential, userIsolation: false,
                itemTtlSeconds: _ttlSeconds,
                description: "ZavaFinance per-user session state.",
                cancellationToken: cancellationToken);
            return _store;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
