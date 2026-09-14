// Copyright (c) Microsoft Corporation.

using System.Text.Json;
using Azure.AI.AgentServer.Core.Storage;
using Azure.Core;
using Microsoft.Agents.Storage;

namespace ZavaFinance.Agent;

/// <summary>
/// Backs <see cref="IStorage"/> with the platform's own state store.
/// <para>
/// This replaces a customer storage account, and not merely for convenience. Tenant policy
/// forces <c>publicNetworkAccess: Disabled</c> on every storage account — an attempt to enable it
/// is silently reverted — so a Foundry-managed container could only reach blob storage through a
/// VNet-injected project, private endpoints and private DNS. <see cref="FoundryStateStore"/>
/// removes that entire dependency: it is reached over the project endpoint with the agent's own
/// managed identity.
/// </para>
/// <para>
/// Isolation is unchanged and still ours. Keys are the session keys derived from the caller's
/// validated claims, so two participants in one conversation cannot address each other's state.
/// The store's own <c>userIsolation</c> partitioning is deliberately not relied upon: our key is
/// the stronger boundary because it also includes the conversation, and it is the same derivation
/// the channel host uses.
/// </para>
/// </summary>
public sealed class FoundryStateStorage : IStorage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The single field every item is stored under.</summary>
    private const string ItemField = "item";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storeName;
    private readonly TokenCredential _credential;
    private readonly TimeSpan _itemTtl;
    private FoundryStateStore? _store;

    public FoundryStateStorage(string storeName, TokenCredential credential, TimeSpan itemTtl)
    {
        _storeName = storeName;
        _credential = credential;
        _itemTtl = itemTtl;
    }

    /// <summary>
    /// Binds the store on first use, creating it if it does not exist.
    /// <para>
    /// Deliberately lazy. Binding at startup makes the container's readiness depend on an
    /// external call, so a transient storage fault presents as a container that never becomes
    /// ready — which the platform reports as <c>session_not_ready</c>, with the real cause
    /// visible only in stderr.
    /// </para>
    /// </summary>
    private async Task<FoundryStateStore> GetStoreAsync(CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            return _store;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            // Optional arguments are omitted rather than passed as null: apiVersion and
            // endpoint are used to build a URI directly, so an explicit null throws inside
            // Uri.EscapeDataString instead of falling back to a default.
            _store ??= await FoundryStateStore.GetOrCreateAsync(
                _storeName,
                _credential,
                userIsolation: false,
                itemTtlSeconds: (int)_itemTtl.TotalSeconds,
                description: "ZavaFinance per-user session state.",
                cancellationToken: cancellationToken);

            return _store;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IDictionary<string, object>> ReadAsync(
        string[] keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var result = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (string key in keys)
        {
            FoundryStateStore store = await GetStoreAsync(cancellationToken);
            StateStoreItem? item = await store.GetItemAsync(Encode(key), cancellationToken);

            if (item is null)
            {
                continue;
            }

            if (!item.Value.TryGetValue(ItemField, out BinaryData? raw))
            {
                continue;
            }

            StoredItem? stored;

            try
            {
                stored = raw.ToObjectFromJson<StoredItem>(Json);
            }
            catch (JsonException)
            {
                // An item written by an older shape is not worth failing a turn over; a fresh
                // session is the recoverable outcome.
                continue;
            }

            if (stored is null)
            {
                continue;
            }

            Type? type = Type.GetType(stored.Type);

            if (type is null)
            {
                // A stored item whose type no longer exists must not fail the turn. Skipping it
                // starts a fresh session, which is recoverable; throwing would brick the user.
                continue;
            }

            object? value = stored.Value.Deserialize(type, Json);

            if (value is not null)
            {
                if (value is IStoreItem storeItem)
                {
                    storeItem.ETag = item.Etag;
                }

                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// One item as stored. A single JSON object, so the store never receives a bare value it
    /// cannot parse.
    /// </summary>
    private sealed record StoredItem(string Type, JsonElement Value);

    public async Task WriteAsync(
        IDictionary<string, object> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        foreach ((string key, object value) in changes)
        {
            string? etag = (value as IStoreItem)?.ETag;

            // Every field handed to the store is written with Utf8JsonWriter.WriteRawValue, which
            // requires each one to be valid JSON on its own. Writing a type name as a bare string
            // fails with "'Z' is an invalid start of a value" — the Z being ZavaFinance — and the
            // failure lands after the turn's tool has already run, discarding its answer.
            //
            // Serialising one object and wrapping it removes the whole class of problem: there is
            // exactly one field, and it is always a JSON object.
            using JsonDocument inner = JsonDocument.Parse(
                JsonSerializer.Serialize(value, value.GetType(), Json));

            var stored = new StoredItem(
                value.GetType().AssemblyQualifiedName ?? value.GetType().FullName!,
                inner.RootElement);

            var payload = new Dictionary<string, BinaryData>(StringComparer.Ordinal)
            {
                [ItemField] = BinaryData.FromObjectAsJson(stored, Json)
            };

            // "*" means "any version" in this API and would defeat the concurrency check, so it
            // is treated as no precondition at all.
            string? ifMatch = string.IsNullOrEmpty(etag) || etag == "*" ? null : etag;

            FoundryStateStore store = await GetStoreAsync(cancellationToken);

            await store.SetItemAsync(
                Encode(key),
                payload,
                tags: null,
                ifMatch: ifMatch,
                requireExists: false,
                cancellationToken);
        }
    }

    public async Task DeleteAsync(string[] keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        foreach (string key in keys)
        {
            try
            {
                FoundryStateStore store = await GetStoreAsync(cancellationToken);
                await store.DeleteItemAsync(Encode(key), ifMatch: null, cancellationToken);
            }
            catch (Exception)
            {
                // Deleting an item that is already gone is the expected outcome of a replay,
                // not an error worth failing a turn over.
            }
        }
    }

    /// <summary>Typed read. The untyped path already rehydrates the stored concrete type.</summary>
    public async Task<IDictionary<string, TStoreItem>> ReadAsync<TStoreItem>(
        string[] keys, CancellationToken cancellationToken = default)
        where TStoreItem : class
    {
        IDictionary<string, object> items = await ReadAsync(keys, cancellationToken);

        var typed = new Dictionary<string, TStoreItem>(StringComparer.Ordinal);

        foreach ((string key, object value) in items)
        {
            if (value is TStoreItem match)
            {
                typed[key] = match;
            }
        }

        return typed;
    }

    /// <summary>Typed write.</summary>
    public Task WriteAsync<TStoreItem>(
        IDictionary<string, TStoreItem> changes, CancellationToken cancellationToken = default)
        where TStoreItem : class
    {
        ArgumentNullException.ThrowIfNull(changes);

        var untyped = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach ((string key, TStoreItem value) in changes)
        {
            untyped[key] = value;
        }

        return WriteAsync(untyped, cancellationToken);
    }

    /// <summary>
    /// Makes a store key out of a logical key.
    /// <para>
    /// The logical keys are path-shaped — <c>orchestrator/{sessionKey}</c>,
    /// <c>pending/{sessionKey}/{turnId}</c> — and a separator that is legal in one store is not
    /// necessarily legal in another. Replacing it keeps the key one flat token while preserving
    /// uniqueness, because the segments themselves contain no separator.
    /// </para>
    /// </summary>
    private static string Encode(string key) => key.Replace('/', '_');
}
