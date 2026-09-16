using System.ClientModel;
using System.Net;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;
using Xunit;

#pragma warning disable OPENAI001 // Verifies the existing Responses adapter's no-storage setting.
namespace ZavaFinance.Tests.Finance;

public sealed class AzureResolverSearchTests
{
    private static readonly ResolverRelease Release = new("v1", "resolver-v1", "embedding-model", 2);

    [Fact]
    public void ExactOnlyDefaultConfigurationIsValidButPartialFallbackConfigurationFails()
    {
        new ResolverOptions().Validate();
        Assert.Throws<InvalidOperationException>(() =>
            new ResolverOptions { SearchEndpoint = "https://search.example.test" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ResolverOptions { MaxCandidates = 26 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new ResolverOptions { ClarificationTimeToLive = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public async Task SearchUsesManagedCredentialHybridVectorSemanticAndVersionFilter()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var credential = new Credential();
        var resolver = Resolver(http, credential, chat);
        IReadOnlyList<ResolverCandidateId> results =
            await resolver.SearchAsync("  net ernings ", "kpi", Release, CancellationToken.None);
        Assert.Equal("candidate-one", Assert.Single(results).Id);
        Assert.Equal(["https://cognitiveservices.azure.com/.default", "https://search.azure.com/.default"],
            credential.Scopes);
        Assert.Equal("  net ernings ", handler.Bodies[0].GetProperty("input").GetString());
        Assert.Equal(2, handler.Bodies[0].GetProperty("dimensions").GetInt32());
        Assert.Contains("/deployments/embedding-model/embeddings", handler.Uris[0].AbsolutePath);
        Assert.Contains("/indexes/resolver-v1/docs/search", handler.Uris[1].AbsolutePath);
        JsonElement body = handler.Bodies[1];
        Assert.Equal("  net ernings ", body.GetProperty("search").GetString());
        Assert.Equal("semantic", body.GetProperty("queryType").GetString());
        Assert.Equal("resolver-semantic", body.GetProperty("semanticConfiguration").GetString());
        Assert.Equal("entity_id,catalog_version", body.GetProperty("select").GetString());
        Assert.Contains("catalog_version eq 'v1'", body.GetProperty("filter").GetString());
        Assert.Contains("entity_kind eq 'kpi_group'", body.GetProperty("filter").GetString());
        Assert.Equal("preFilter", body.GetProperty("vectorFilterMode").GetString());
        JsonElement vector = Assert.Single(body.GetProperty("vectorQueries").EnumerateArray());
        Assert.Equal("vector", vector.GetProperty("kind").GetString());
        Assert.Equal("content_vector", vector.GetProperty("fields").GetString());
        Assert.Equal(50, vector.GetProperty("k").GetInt32());
        Assert.Equal(2, vector.GetProperty("vector").GetArrayLength());
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public async Task OrgSearchIsExplicitlyKindConstrained()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        await Resolver(http, new Credential(), chat).SearchAsync("region", "org", Release, CancellationToken.None);
        Assert.Contains("entity_kind eq 'org'", handler.Bodies[1].GetProperty("filter").GetString());
        Assert.DoesNotContain("entity_kind eq 'kpi'", handler.Bodies[1].GetProperty("filter").GetString());
    }

    [Fact]
    public async Task CandidateChoiceHasNoSessionToolsOrFinancePayloadAndStrictIdsSchema()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var candidate = ResolverTestData.Entity("candidate-one", "kpi", "Operating result", code: "KPI-011");
        IReadOnlyList<string>? result = await Resolver(http, new Credential(), chat)
            .ChooseAsync("earnings", "kpi", [candidate], CancellationToken.None);
        Assert.Equal(["candidate-one"], result);
        Assert.Equal(2, chat.Messages.Count);
        Assert.Equal(ChatRole.System, chat.Messages[0].Role);
        Assert.Contains("untrusted data", chat.Messages[0].Text);
        Assert.Contains("Do not infer or broaden scope", chat.Messages[0].Text);
        JsonElement input = JsonSerializer.Deserialize<JsonElement>(chat.Messages[1].Text);
        Assert.Equal("earnings", input.GetProperty("rawTerm").GetString());
        Assert.Equal("candidate-one", Assert.Single(input.GetProperty("candidates").EnumerateArray())
            .GetProperty("id").GetString());
        Assert.Null(chat.Options!.Tools);
        Assert.Equal("gpt-4.1-mini", chat.Options.ModelId);
        Assert.IsType<ChatResponseFormatJson>(chat.Options.ResponseFormat);
        var raw = Assert.IsType<CreateResponseOptions>(chat.Options.RawRepresentationFactory!(chat));
        Assert.False(raw.StoredOutputEnabled);
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"ids\":42}")]
    [InlineData("{\"ids\":null}")]
    [InlineData("{\"ids\":[\"candidate-one\",42]}")]
    [InlineData("{\"ids\":[null]}")]
    [InlineData("{\"ids\":[\"\"]}")]
    public async Task MalformedModelResponseReturnsUncertainInsteadOfSelectingAScoreWinner(string response)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient { Response = response };
        var logger = new RecordingLogger();
        Assert.Null(await Resolver(http, new Credential(), chat, logger).ChooseAsync(
            "earnings", "kpi", ResolverTestData.Standard.Entities, CancellationToken.None));
        Assert.Matches(@"^Resolver candidate choice unavailable\. ErrorType=Json(?:Reader)?Exception$",
            logger.SingleWarning());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UnavailableRetrievalDoesNotBroadenOrReturnRawServiceErrors(HttpStatusCode status)
    {
        using var handler = new Handler { Status = status };
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var logger = new RecordingLogger();
        Assert.Empty(await Resolver(http, new Credential(), chat, logger)
            .SearchAsync("term", "org", Release, CancellationToken.None));
        Assert.Equal("Resolver retrieval unavailable. ErrorType=HttpRequestException", logger.SingleWarning());
        Assert.Empty(chat.Messages);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("authentication")]
    [InlineData("credential-unavailable")]
    public async Task ExpectedRetrievalFailuresAreLoggedWithoutSensitiveDetails(string failureKind)
    {
        const string sensitive = "private raw term; token and upstream response body";
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var credential = new Credential();
        var logger = new RecordingLogger();
        Exception failure = failureKind switch
        {
            "http" => new HttpRequestException(sensitive),
            "authentication" => new AuthenticationFailedException(sensitive),
            _ => new CredentialUnavailableException(sensitive)
        };
        if (failureKind == "http") handler.Failure = failure;
        else credential.Failure = failure;
        Assert.Empty(await Resolver(http, credential, chat, logger)
            .SearchAsync(sensitive, "org", Release, CancellationToken.None));
        Assert.Equal($"Resolver retrieval unavailable. ErrorType={failure.GetType().Name}", logger.SingleWarning());
        Assert.DoesNotContain("https://search.azure.com/.default", credential.Scopes);
        Assert.Empty(chat.Messages);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("authentication")]
    [InlineData("credential-unavailable")]
    public async Task ExpectedRerankerFailuresAreLoggedWithoutSensitiveDetails(string failureKind)
    {
        const string sensitive = "private raw term; token and upstream response body";
        Exception failure = failureKind switch
        {
            "service" => new ClientResultException(sensitive, null, null),
            "authentication" => new AuthenticationFailedException(sensitive),
            _ => new CredentialUnavailableException(sensitive)
        };
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient { Failure = failure };
        var logger = new RecordingLogger();
        Assert.Null(await Resolver(http, new Credential(), chat, logger)
            .ChooseAsync(sensitive, "kpi", ResolverTestData.Standard.Entities, CancellationToken.None));
        Assert.Equal($"Resolver candidate choice unavailable. ErrorType={failure.GetType().Name}",
            logger.SingleWarning());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedProgrammingErrorsAndUnrelatedCancellationPropagate(bool rerank)
    {
        foreach (Exception failure in new Exception[]
        {
            new InvalidOperationException("programming error"),
            new ArgumentException("programming error"),
            new NullReferenceException("programming error"),
            new OperationCanceledException("not a caller cancellation or a configured timeout")
        })
        {
            using var handler = new Handler { Failure = failure };
            using var http = new HttpClient(handler);
            using var chat = new ChoiceClient { Failure = failure };
            var logger = new RecordingLogger();
            AzureResolverSearch resolver = Resolver(http, new Credential(), chat, logger);
            Exception? actual = await Record.ExceptionAsync(async () =>
            {
                if (rerank)
                    await resolver.ChooseAsync("term", "kpi", ResolverTestData.Standard.Entities, CancellationToken.None);
                else
                    await resolver.SearchAsync("term", "org", Release, CancellationToken.None);
            });
            Assert.Same(failure, actual);
            Assert.Empty(logger.Entries);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalTimeoutReturnsUncertaintyAndLogsOnlyErrorType(bool rerank)
    {
        using var handler = new Handler { WaitForCancellation = true };
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient { WaitForCancellation = true };
        var logger = new RecordingLogger();
        AzureResolverSearch resolver = Resolver(http, new Credential(), chat, logger, TimeSpan.FromMilliseconds(10));
        if (rerank)
            Assert.Null(await resolver.ChooseAsync("private raw term", "kpi",
                ResolverTestData.Standard.Entities, CancellationToken.None));
        else
            Assert.Empty(await resolver.SearchAsync("private raw term", "org", Release, CancellationToken.None));
        Assert.Equal($"Resolver {(rerank ? "candidate choice" : "retrieval")} unavailable. ErrorType=TaskCanceledException",
            logger.SingleWarning());
    }

    [Fact]
    public async Task HttpClientTimeoutReturnsNoMatchesWithoutWaitingForResolverTimeout()
    {
        using var handler = new Handler { WaitForCancellation = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(10) };
        using var chat = new ChoiceClient();
        var logger = new RecordingLogger();
        Assert.Empty(await Resolver(http, new Credential(), chat, logger)
            .SearchAsync("private raw term", "org", Release, CancellationToken.None));
        Assert.Equal("Resolver retrieval unavailable. ErrorType=TaskCanceledException", logger.SingleWarning());
    }

    [Fact]
    public async Task UnconfiguredSearchDoesNotAcquireAnyCredential()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var credential = new Credential();
        var resolver = new AzureResolverSearch(http, credential, new ResolverOptions(),
            chat, "gpt-4.1-mini", NullLogger<AzureResolverSearch>.Instance);
        Assert.Empty(await resolver.SearchAsync("term", "org", Release, CancellationToken.None));
        Assert.Empty(credential.Scopes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CallerCancellationPropagates(bool rerank, bool duringRequest)
    {
        using var handler = new Handler { WaitForCancellation = true };
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient { WaitForCancellation = true };
        using var cancellation = new CancellationTokenSource();
        if (duringRequest) cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));
        else cancellation.Cancel();
        var logger = new RecordingLogger();
        AzureResolverSearch resolver = Resolver(http, new Credential(), chat, logger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (rerank)
                await resolver.ChooseAsync("term", "kpi", ResolverTestData.Standard.Entities, cancellation.Token);
            else
                await resolver.SearchAsync("term", "org", Release, cancellation.Token);
        });
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task EachRequestUsesOneCompletePublishedIndexEmbeddingBinding()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var resolver = Resolver(http, new Credential(), chat);
        await resolver.SearchAsync("term", "org", Release, CancellationToken.None);
        var next = new ResolverRelease("2026-09-16-v1", "resolver-2026-09-16-v1", "text-embedding-3-large", 1536);
        handler.EmbeddingDimensions = next.EmbeddingDimensions;
        await resolver.SearchAsync("term", "org", next, CancellationToken.None);
        Assert.Contains("/deployments/text-embedding-3-large/embeddings", handler.Uris[2].AbsolutePath);
        Assert.Contains("/indexes/resolver-2026-09-16-v1/docs/search", handler.Uris[3].AbsolutePath);
        Assert.Equal(1536, handler.Bodies[2].GetProperty("dimensions").GetInt32());
        Assert.Contains("catalog_version eq '2026-09-16-v1'", handler.Bodies[3].GetProperty("filter").GetString());
        Assert.Equal(1536, handler.Bodies[3].GetProperty("vectorQueries")[0].GetProperty("vector").GetArrayLength());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task WrongEmbeddingDimensionsNeverReachSearch(int dimensions)
    {
        using var handler = new Handler { EmbeddingDimensions = dimensions };
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var credential = new Credential();
        var logger = new RecordingLogger();
        Assert.Empty(await Resolver(http, credential, chat, logger)
            .SearchAsync("private raw term", "org", Release, CancellationToken.None));
        Assert.Equal("Resolver rejected query embedding. Reason=dimension_mismatch", logger.SingleWarning());
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain("https://search.azure.com/.default", credential.Scopes);
    }

    [Theory]
    [InlineData("1e1000")]
    [InlineData("-1e1000")]
    public async Task NonfiniteEmbeddingNeverReachesSearchAndLogsRejection(string nonfinite)
    {
        using var handler = new Handler { EmbeddingResponse = $$"""{"data":[{"embedding":[{{nonfinite}},0.1]}]}""" };
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var credential = new Credential();
        var logger = new RecordingLogger();
        Assert.Empty(await Resolver(http, credential, chat, logger)
            .SearchAsync("private raw term", "org", Release, CancellationToken.None));
        Assert.Equal("Resolver rejected query embedding. Reason=non_finite_value", logger.SingleWarning());
        Assert.Single(handler.Bodies);
        Assert.DoesNotContain("https://search.azure.com/.default", credential.Scopes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"data\":42}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[{\"embedding\":[0.1,0.1]},{\"embedding\":[0.1,0.1]}]}")]
    [InlineData("{\"data\":[{}]}")]
    [InlineData("{\"data\":[{\"embedding\":42}]}")]
    [InlineData("{\"data\":[{\"embedding\":[0.1,\"private payload\"]}]}")]
    [InlineData("{\"data\":[{\"embedding\":[0.1,null]}]}")]
    public async Task MalformedEmbeddingWireShapeIsHandledAsJsonFailure(string response)
    {
        using var handler = new Handler { EmbeddingResponse = response };
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var logger = new RecordingLogger();
        Assert.Empty(await Resolver(http, new Credential(), chat, logger)
            .SearchAsync("private raw term", "org", Release, CancellationToken.None));
        Assert.Single(handler.Bodies);
        Assert.Equal("Resolver retrieval unavailable. ErrorType=JsonException", logger.SingleWarning());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"value\":42}")]
    [InlineData("{\"value\":[null]}")]
    [InlineData("{\"value\":[{\"entity_id\":\"candidate-one\"}]}")]
    [InlineData("{\"value\":[{\"entity_id\":42,\"catalog_version\":\"v1\"}]}")]
    [InlineData("{\"value\":[{\"entity_id\":null,\"catalog_version\":\"v1\"}]}")]
    [InlineData("{\"value\":[{\"entity_id\":\"candidate-one\",\"catalog_version\":\"v1\"},{\"entity_id\":42,\"catalog_version\":\"v1\"}]}")]
    public async Task MalformedSearchWireShapeDoesNotReturnPartialCandidates(string response)
    {
        using var handler = new Handler { SearchResponse = response };
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var logger = new RecordingLogger();
        Assert.Empty(await Resolver(http, new Credential(), chat, logger)
            .SearchAsync("private raw term", "org", Release, CancellationToken.None));
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("Resolver retrieval unavailable. ErrorType=JsonException", logger.SingleWarning());
    }

    [Theory]
    [InlineData("", "resolver-v1", "embedding", 1536)]
    [InlineData("v'1", "resolver-v1", "embedding", 1536)]
    [InlineData("v1", "", "embedding", 1536)]
    [InlineData("v1", "../resolver-v1", "embedding", 1536)]
    [InlineData("v1", "resolver-v1", "", 1536)]
    [InlineData("v1", "resolver-v1", "../embedding", 1536)]
    [InlineData("v1", "resolver-v1", "embedding", 0)]
    [InlineData("v1", "resolver-v1", "embedding", 3073)]
    public async Task MalformedReleaseFailsBeforeAnyManagedIdentityOrNetworkUse(
        string version, string index, string model, int dimensions)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var chat = new ChoiceClient();
        var credential = new Credential();
        await Assert.ThrowsAsync<InvalidDataException>(() => Resolver(http, credential, chat)
            .SearchAsync("term", "org", new(version, index, model, dimensions), CancellationToken.None));
        Assert.Empty(credential.Scopes);
        Assert.Empty(handler.Bodies);
    }

    private static AzureResolverSearch Resolver(
        HttpClient http, Credential credential, ChoiceClient chat,
        ILogger<AzureResolverSearch>? logger = null, TimeSpan? timeout = null) =>
        new(http, credential, new ResolverOptions
        {
            SearchEndpoint = "https://search.example.test",
            EmbeddingEndpoint = "https://embeddings.example.test",
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        }, chat, "gpt-4.1-mini", logger ?? NullLogger<AzureResolverSearch>.Instance);

    private sealed class Credential : TokenCredential
    {
        public Exception? Failure { get; set; }
        public List<string> Scopes { get; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scopes.Add(Assert.Single(requestContext.Scopes));
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(new AccessToken("test-only-credential", DateTimeOffset.MaxValue));
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Exception? Failure { get; set; }
        public bool WaitForCancellation { get; set; }
        public string? EmbeddingResponse { get; set; }
        public string? SearchResponse { get; set; }
        public List<JsonElement> Bodies { get; } = [];
        public List<Uri> Uris { get; } = [];
        public int EmbeddingDimensions { get; set; } = 2;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.False(request.Headers.Contains("api-key"));
            Uris.Add(request.RequestUri!);
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("embeddings", StringComparison.Ordinal)
                    ? EmbeddingResponse ?? JsonSerializer.Serialize(new { data = new[] { new { index = 0,
                        embedding = Enumerable.Repeat(0.1, EmbeddingDimensions).ToArray() } } })
                    : SearchResponse ?? """{"value":[{"entity_id":"candidate-one","catalog_version":"v1","canonical_name":"NEVER READ THIS SEARCH TEXT"}]}""")
            };
        }
    }

    private sealed class ChoiceClient : IChatClient
    {
        public Exception? Failure { get; set; }
        public bool WaitForCancellation { get; set; }
        public string Response { get; set; } = """{"ids":["candidate-one"]}""";
        public List<ChatMessage> Messages { get; } = [];
        public ChatOptions? Options { get; private set; }
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Messages.AddRange(messages);
            Options = options;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, Response));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingLogger : ILogger<AzureResolverSearch>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));

        public string SingleWarning()
        {
            var entry = Assert.Single(Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            return entry.Message;
        }
    }
}
