using System.Net;
using System.Text;
using System.Web;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Agents.Storage;
using ZavaFinance.Channel;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class ChannelBlobStorageTests
{
    [Theory]
    [InlineData("ZavaFinance.Agent")]
    [InlineData("Removed.Assembly.That.Must.Not.Be.Loaded")]
    public async Task RawBlobReadIgnoresSdkClrMetadataAndUsesTheDownloadEtag(string oldAssembly)
    {
        var handler = new BlobHandler
        {
            Payload = $$"""
                {"$type":"ZavaFinance.Core.Agent.PendingTurn","$typeAssembly":"{{oldAssembly}}",
                 "question":"legacy question","answer":"legacy answer","eTag":"stale"}
                """
        };
        var storage = Storage(handler);
        var store = new ChannelSessionStore(storage);
        var turn = (await store.ReadTurnAsync("s", "t", CancellationToken.None))!;
        Assert.Equal(TurnDeliveryStatus.AnswerReady, turn.Status);
        Assert.Equal("legacy answer", turn.Answer);
        Assert.Equal("\"download-etag\"", turn.ETag);
        Assert.Equal("pending%2fs%2ft", HttpUtility.UrlEncode("pending/s/t"));
        Assert.Contains(handler.Urls, uri =>
            Uri.UnescapeDataString(uri.AbsolutePath) == "/state/pending%2fs%2ft");
        Assert.DoesNotContain(handler.Methods, method => method == HttpMethod.Head);
    }

    [Fact]
    public async Task WritesUseConditionalCreateAndUpdateWithNoClrMetadata()
    {
        var handler = new BlobHandler();
        var storage = Storage(handler);
        await storage.WriteAsync(new Dictionary<string, object>
        {
            ["pending/s/t"] = new TurnDeliveryRecord
            {
                Status = TurnDeliveryStatus.Pending, Question = "question"
            }
        });
        Assert.Equal("*", handler.IfNoneMatch);
        Assert.Null(handler.IfMatch);
        Assert.DoesNotContain("$type", handler.WrittenBody);

        await storage.WriteAsync(new Dictionary<string, object>
        {
            ["pending/s/t"] = new TurnDeliveryRecord
            {
                Status = TurnDeliveryStatus.AnswerReady, Answer = "cached", ETag = "\"version-1\""
            }
        });
        Assert.Equal("\"version-1\"", handler.IfMatch);
        Assert.Null(handler.IfNoneMatch);
        Assert.DoesNotContain("$type", handler.WrittenBody);
    }

    [Fact]
    public async Task ConditionalConflictIsNotTreatedAsSuccess()
    {
        var handler = new BlobHandler { WriteStatus = HttpStatusCode.PreconditionFailed };
        await Assert.ThrowsAsync<EtagException>(() => Storage(handler).WriteAsync(new Dictionary<string, object>
        {
            ["pending/s/t"] = new TurnDeliveryRecord { Question = "question", ETag = "\"stale\"" }
        }));
    }

    [Fact]
    public async Task UnconditionalWritesAreRejected()
    {
        var handler = new BlobHandler();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Storage(handler).WriteAsync(new Dictionary<string, object>
            {
                ["pending/s/t"] = new TurnDeliveryRecord { Question = "question", ETag = "*" }
            }));
        Assert.Empty(handler.Urls);
    }

    private static ChannelBlobStorage Storage(BlobHandler handler)
    {
        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler))
        };
        options.Retry.MaxRetries = 0;
        return new ChannelBlobStorage(new BlobContainerClient(
            new Uri("https://offline.invalid/state"), options));
    }

    private sealed class BlobHandler : HttpMessageHandler
    {
        public string Payload { get; init; } = "{}";
        public HttpStatusCode WriteStatus { get; init; } = HttpStatusCode.Created;
        public List<Uri> Urls { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public string WrittenBody { get; private set; } = "";
        public string? IfMatch { get; private set; }
        public string? IfNoneMatch { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!);
            Methods.Add(request.Method);
            if (request.Method == HttpMethod.Put)
            {
                WrittenBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                IfMatch = request.Headers.TryGetValues("If-Match", out var match) ? match.Single() : null;
                IfNoneMatch = request.Headers.TryGetValues("If-None-Match", out var none) ? none.Single() : null;
                var write = new HttpResponseMessage(WriteStatus);
                write.Headers.TryAddWithoutValidation("ETag", "\"written-etag\"");
                if (WriteStatus == HttpStatusCode.PreconditionFailed)
                {
                    write.Headers.TryAddWithoutValidation("x-ms-error-code", "ConditionNotMet");
                }
                return write;
            }

            if (Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Contains("turn-complete-"))
            {
                var missing = new HttpResponseMessage(HttpStatusCode.NotFound);
                missing.Headers.TryAddWithoutValidation("x-ms-error-code", "BlobNotFound");
                return missing;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Payload, Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("ETag", "\"download-etag\"");
            response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(Payload);
            return response;
        }
    }
}
