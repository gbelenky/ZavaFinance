using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Agents.Storage;

namespace ZavaFinance.Channel;

/// <summary>
/// Channel records use plain JSON, not CLR type names. Agents SDK 1.8.77 resolves stored type
/// metadata before doing a typed read; old records name the now-independent Agent executable.
/// Read their properties directly instead, retaining the SDK's URL-encoded blob keys and ETags.
/// SDK-owned proactive/authentication records continue to use the unmodified SDK storage.
/// </summary>
public sealed class ChannelBlobStorage(BlobContainerClient container) : IStorage
{
    public async Task<IDictionary<string, TStoreItem>> ReadAsync<TStoreItem>(
        string[] keys, CancellationToken cancellationToken = default) where TStoreItem : class
    {
        IDictionary<string, object> items = await ReadAsync(keys, cancellationToken);
        return items.ToDictionary(p => p.Key, p =>
            ((JsonObject)p.Value).Deserialize<TStoreItem>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Channel state could not be read."));
    }

    public async Task<IDictionary<string, object>> ReadAsync(
        string[] keys, CancellationToken cancellationToken = default)
    {
        var items = new Dictionary<string, object>();
        foreach (string key in keys)
        {
            try
            {
                var response = await container.GetBlobClient(HttpUtility.UrlEncode(key))
                    .DownloadContentAsync(cancellationToken);
                JsonObject document = JsonNode.Parse(response.Value.Content.ToString()) as JsonObject
                    ?? throw new InvalidDataException("Channel state is not a JSON object.");
                // A snapshot's ETag must come from the same download, not a later properties read.
                foreach (string property in document.Select(p => p.Key)
                    .Where(p => p.Equals("etag", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    document.Remove(property);
                }

                document["ETag"] = response.Value.Details.ETag.ToString();
                items.Add(key, document);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Missing state is distinct from a record we cannot deserialize.
            }
        }

        return items;
    }

    public async Task WriteAsync(
        IDictionary<string, object> changes, CancellationToken cancellationToken = default)
    {
        foreach ((string key, object value) in changes)
        {
            string? etag = (value as IStoreItem)?.ETag;
            if (etag == "*")
            {
                throw new InvalidOperationException("Channel state requires conditional writes.");
            }

            var conditions = string.IsNullOrEmpty(etag)
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = new ETag(etag) };
            var options = new BlobUploadOptions
            {
                Conditions = conditions,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            };
            try
            {
                await container.GetBlobClient(HttpUtility.UrlEncode(key)).UploadAsync(
                    BinaryData.FromString(JsonSerializer.Serialize(value)), options, cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                throw new EtagException("Channel state changed concurrently.");
            }
        }
    }

    public async Task DeleteAsync(string[] keys, CancellationToken cancellationToken = default)
    {
        foreach (string key in keys)
        {
            await container.GetBlobClient(HttpUtility.UrlEncode(key))
                .DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }

    }

    public Task WriteAsync<TStoreItem>(
        IDictionary<string, TStoreItem> changes, CancellationToken cancellationToken = default)
        where TStoreItem : class
        => WriteAsync(changes.ToDictionary(p => p.Key, p => (object)p.Value), cancellationToken);
}
