using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ZavaFinance.Agent;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Identity;
using ZavaFinance.Core.Tools;
using ZavaFinance.One;
using ZavaFinance.Tests.Finance;
using Xunit;

namespace ZavaFinance.Tests.One;

public sealed class FinanceActivityReuseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeBoundaryReusesTypedAndCardContinuationWithoutASecondModelCall(bool card)
    {
        using var finance = new FinanceFixture();
        using var native = new FinanceActivityApplicationTests.Fixture(finance.Runner);
        await native.RunAsync("message");
        JsonElement choice = ReadChoice(native);

        await native.RunAsync("message", card ? choice : null, card ? "ignored" : "2");

        Assert.Equal(1, finance.Chat.Calls);
        Assert.Equal(1, finance.Query.Calls);
        Assert.Contains("Source:", native.Output.Messages[^1]);
        Assert.IsType<OboTokenProvider>(finance.Query.Tokens);
        Assert.DoesNotContain(FinanceActivityApplicationTests.Fixture.Assertion,
            string.Join("", finance.Store.Items.Values.Select(FoundrySessionStore.Serialize)));
    }

    [Fact]
    public async Task NativeResetClearsExistingOrchestratorStateWithoutCallingModel()
    {
        using var finance = new FinanceFixture();
        using var native = new FinanceActivityApplicationTests.Fixture(finance.Runner);
        await native.RunAsync("message");
        string key = native.Keys.GetSessionKey(native.Validator.Caller, "conversation-1");
        Assert.NotNull(finance.Store.Items[key].PendingClarification);

        await native.RunAsync("message", text: "reset");

        Assert.Contains("conversation has been reset", native.Output.Messages[^1]);
        Assert.Null(finance.Store.Items[key].PendingClarification);
        Assert.Null(finance.Store.Items[key].LastKpiName);
        Assert.Equal(1, finance.Chat.Calls);
        Assert.Equal(0, finance.Query.Calls);
    }

    [Fact]
    public async Task CopiedCardCannotAccessAnotherValidatedUsersFinanceSession()
    {
        using var finance = new FinanceFixture();
        using var alice = new FinanceActivityApplicationTests.Fixture(finance.Runner);
        using var bob = new FinanceActivityApplicationTests.Fixture(finance.Runner);
        bob.Validator.Caller = CallerIdentity.Create("tenant-1", "user-2");
        await alice.RunAsync("message");
        JsonElement choice = ReadChoice(alice);

        await bob.RunAsync("message", choice);

        Assert.Contains("no valid pending choice", Assert.Single(bob.Output.Messages));
        Assert.Equal(0, finance.Query.Calls);
        Assert.Equal(1, finance.Chat.Calls);
        string aliceKey = alice.Keys.GetSessionKey(alice.Validator.Caller, "conversation-1");
        Assert.NotNull(finance.Store.Items[aliceKey].PendingClarification);
        Assert.Equal(2, finance.Store.Items.Count);
    }

    [Fact]
    public async Task GenericClarificationRemainsOnePlainTextResponse()
    {
        using var finance = new FinanceFixture(org: "");
        using var native = new FinanceActivityApplicationTests.Fixture(finance.Runner);
        await native.RunAsync("message");
        var message = Assert.Single(native.Output.Activities);
        Assert.Contains("Which organization", message.Text);
        Assert.True(message.Attachments is null || message.Attachments.Count == 0);
        Assert.Equal(1, finance.Chat.Calls);
        Assert.Equal(0, finance.Query.Calls);
    }

    private static JsonElement ReadChoice(FinanceActivityApplicationTests.Fixture native)
    {
        var attachment = Assert.Single(Assert.Single(native.Output.Activities).Attachments);
        var card = Assert.IsType<JsonElement>(attachment.Content);
        return card.GetProperty("body")[3].GetProperty("selectAction").GetProperty("data");
    }

    private sealed class FinanceFixture : IDisposable
    {
        public MemoryStore Store { get; } = new();
        public StatementQuery Query { get; } = new();
        public RoutingChat Chat { get; }
        public FinanceActivityRunner Runner { get; }

        public FinanceFixture(string org = "Marketing")
        {
            Chat = new RoutingChat(org);
            var factory = new FinanceAgentFactory(Chat, new FoundryOptions());
            var otherTools = new UnusedTools();
            var orchestrator = new OrchestratorAgent(factory, otherTools, Store, Query, otherTools,
                new FabricOptions(), new OrchestratorOptions(), TimeProvider.System, NullLoggerFactory.Instance);
            Runner = new FinanceActivityRunner(orchestrator);
        }

        public void Dispose() => Chat.Dispose();
    }

    private sealed class MemoryStore : IAgentSessionStore
    {
        public Dictionary<string, OrchestratorSessionState> Items { get; } = [];
        public Task<OrchestratorSessionState> LoadAsync(string sessionKey, CancellationToken cancellationToken)
            => Task.FromResult(Items.GetValueOrDefault(sessionKey) ?? new OrchestratorSessionState());
        public Task SaveAsync(string sessionKey, OrchestratorSessionState state, CancellationToken cancellationToken)
        {
            Items[sessionKey] = state;
            return Task.CompletedTask;
        }
    }

    private sealed class StatementQuery : IStatementQueryFactory, IStatementQuery, IResolverCatalog
    {
        public int Calls { get; private set; }
        public IDownstreamTokenProvider? Tokens { get; private set; }
        public IStatementQuery Create(IDownstreamTokenProvider tokenProvider)
        {
            Tokens = tokenProvider;
            return this;
        }
        public Task<ResolverRelease> GetReleaseAsync(CancellationToken cancellationToken)
            => Task.FromResult(ResolverTestData.Standard.Release);
        public Task<ResolverCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
            => Task.FromResult(ResolverTestData.Standard);
        public Task<StatementResult> GetStatementAsync(
            KpiDefinition kpi, OrganizationScope organization, FinancePeriod period, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new StatementResult(kpi, organization, period, 12m, 10m, "USD"));
        }
    }

    private sealed class UnusedTools : ICopilotStudioClientFactory, IFabricDataAgentClientFactory
    {
        public CopilotClient Create(IDownstreamTokenProvider tokenProvider) => throw new InvalidOperationException("Unexpected tool.");
        FabricDataAgentClient IFabricDataAgentClientFactory.Create(IDownstreamTokenProvider tokenProvider)
            => throw new InvalidOperationException("Unexpected tool.");
    }

    private sealed class RoutingChat(string org) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (++Calls > 1) throw new InvalidOperationException("A second model call is not allowed.");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("statement-1", FinanceToolNames.StatementTool,
                    new Dictionary<string, object?>
                    {
                        ["kpi"] = "Net Revenue", ["org"] = org, ["dateRange"] = "Q2 2026"
                    })
            ])));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
