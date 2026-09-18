using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZavaFinance.Agent;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Abstractions;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Tools;
using ZavaFinance.Tests.Finance;
using Xunit;

namespace ZavaFinance.Tests.Routing;

public sealed class NativeFunctionCallingTests
{
    [Fact]
    public async Task VersionOneHistoryStartsAFreshVersionTwoModelSession()
    {
        using var chat = new FakeChatClient(StatementCall("fresh"));
        var fixture = new Fixture(chat);
        AgentSession legacySession = await fixture.Agent.CreateSessionAsync();
        new InMemoryChatHistoryProvider().SetMessages(legacySession,
        [
            new ChatMessage(ChatRole.User, "version-one-question"),
            StatementCall("version-one-call"),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("version-one-call", "legacy-result")])
        ]);
        await fixture.Store.SaveAsync("user-1", new OrchestratorSessionState
        {
            AgentSessionVersion = 1,
            AgentSessionJson = (await fixture.Agent.SerializeSessionAsync(legacySession)).GetRawText()
        }, CancellationToken.None);

        await fixture.RunAsync("A fresh statement");

        OrchestratorSessionState stored = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.Equal(2, stored.AgentSessionVersion);
        Assert.Single(chat.Requests[0], message => message.Role == ChatRole.User);
        Assert.DoesNotContain(chat.Requests[0].SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent);
        string wire = JsonSerializer.Serialize(chat.Requests);
        foreach (string legacy in new[] { "version-one-question", "version-one-call", "legacy-result" })
        {
            Assert.DoesNotContain(legacy, wire);
            Assert.DoesNotContain(legacy, stored.AgentSessionJson!);
        }
    }

    [Fact]
    public async Task RestoredHistoryFiltersPrivateResultsAndIncidentalModelProse()
    {
        using var chat = new FakeChatClient(StatementCall("fresh"));
        var fixture = new Fixture(chat);
        AgentSession oldSession = await fixture.Agent.CreateSessionAsync();
        new InMemoryChatHistoryProvider().SetMessages(oldSession,
        [
            new ChatMessage(ChatRole.System, "legacy-server-secret"),
            new ChatMessage(ChatRole.User, "Earlier question"),
            StatementCall("old"),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("old", "private-finance-result")])
        ]);
        await fixture.Store.SaveAsync("user-1", new OrchestratorSessionState
        {
            AgentSessionVersion = OrchestratorAgent.NativeSessionVersion,
            AgentSessionJson = (await fixture.Agent.SerializeSessionAsync(oldSession)).GetRawText()
        }, CancellationToken.None);

        await fixture.RunAsync("Repeat it");

        string wire = JsonSerializer.Serialize(chat.Requests);
        string saved = (await fixture.Store.LoadAsync("user-1", CancellationToken.None)).AgentSessionJson!;
        foreach (string forbidden in new[] { "legacy-server-secret", "private-finance-result", "This model text" })
        {
            Assert.DoesNotContain(forbidden, wire);
            Assert.DoesNotContain(forbidden, saved);
        }
        Assert.Equal(FinanceHistory.WithheldResult, Assert.Single(chat.Requests[0]
            .SelectMany(message => message.Contents).OfType<FunctionResultContent>()).Result);
    }

    [Fact]
    public async Task ReusingATurnAgentCannotMakeASecondOuterModelRequest()
    {
        using var chat = new FakeChatClient(StatementCall("first"), StatementCall("forbidden"));
        var agent = new FinanceAgentFactory(chat, new FoundryOptions()).Create(FinanceTestTools.Create(), 20);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("One question", session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("Another question", session));

        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task RelativePeriodsAndToolMetadataUseTheSameTurnUtcClock()
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("relative", FinanceToolNames.StatementTool,
                new Dictionary<string, object?>
                {
                    ["kpi"] = "Net Revenue", ["org"] = "EMEA", ["dateRange"] = "last quarter"
                })]));
        var clock = new FakeTimeProvider(new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.Zero));
        var fixture = new Fixture(chat, time: clock);

        await fixture.RunAsync("Revenue in EMEA last quarter");

        Assert.Equal(new DateOnly(2026, 10, 1), fixture.Query.Result!.Period.Start);
        Assert.Equal(new DateOnly(2027, 1, 1), fixture.Query.Result.Period.End);
        Assert.Contains("2027-01-01", chat.Options.Single()!.Tools!
            .Single(tool => tool.Name == FinanceToolNames.StatementTool).Description);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostConsumesCardAndOrdinalChoicesWithoutSendingCandidatesOrFiguresToModel(bool card)
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("ambiguous-call", FinanceToolNames.StatementTool,
                new Dictionary<string, object?> { ["kpi"] = "Net Revenue", ["org"] = "Marketing", ["dateRange"] = "Q2 2026" })]));
        var fixture = new Fixture(chat);
        FinanceReply reply = await fixture.RunReplyAsync("Show Net Revenue for Marketing in Q2 2026");
        ClarificationPrompt prompt = Assert.IsType<ClarificationPrompt>(reply.Clarification);
        Assert.Equal(2, prompt.Options.Count);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Single(chat.Requests);
        ClarificationSubmission? submission = card
            ? new(prompt.RequestId, prompt.Options[1].Id, prompt.CatalogVersion) : null;
        FinanceReply answer = await fixture.RunReplyAsync(card ? "" : "second one", submission);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Null(answer.Clarification);
        Assert.Equal(prompt.Options[1].Id, fixture.Query.Result!.Organization.Code);
        Assert.Single(chat.Requests);
        var state = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.Null(state.PendingClarification);
        Assert.DoesNotContain("Definition for", state.AgentSessionJson!);
        Assert.DoesNotContain("298.0", state.AgentSessionJson!);
        Assert.DoesNotContain(FakeTokens.Token, state.AgentSessionJson!);
        FinanceReply replay = await fixture.RunReplyAsync("", new(prompt.RequestId, prompt.Options[1].Id, prompt.CatalogVersion));
        Assert.Contains("no valid pending choice", replay.Text);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task DuplicateDisplayedLabelAsksForNumberWithoutExecutingOrCallingRouter()
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("ambiguous-call", FinanceToolNames.StatementTool,
                new Dictionary<string, object?> { ["kpi"] = "Net Revenue", ["org"] = "Marketing", ["dateRange"] = "2026" })]));
        var fixture = new Fixture(chat);
        FinanceReply reply = await fixture.RunReplyAsync("A scoped statement");
        FinanceReply answer = await fixture.RunReplyAsync(reply.Clarification!.Options[0].Label);
        Assert.Contains("does not uniquely identify", answer.Text);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Single(chat.Requests);
        Assert.NotNull((await fixture.Store.LoadAsync("user-1", CancellationToken.None)).PendingClarification);
    }

    [Theory]
    [InlineData("Operating Margin", false)]
    [InlineData("Operating Margin %", false)]
    [InlineData("Operating Margin percent", false)]
    [InlineData("  OPERATING MÁRGIN  ", false)]
    [InlineData("operating profitability", false)]
    [InlineData("Operating Margin", true)]
    public async Task TypedKpiChoiceResumesPendingStatementWithoutAnotherModelRequest(
        string choice, bool legacyPendingState)
    {
        using var chat = new FakeChatClient(MarginCall("ambiguous"));
        var fixture = new Fixture(chat);
        fixture.Query.Catalog = MarginCatalog(fixture.Query.Catalog);
        fixture.Query.Value = -26.65m;
        fixture.Query.PriorValue = -24.77m;
        FinanceReply prompt = await fixture.RunReplyAsync("Show margin for EMEA in Q3 2025");
        Assert.Equal(3, prompt.Clarification!.Options.Count);
        OrchestratorSessionState pending = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.DoesNotContain("operating profitability", FoundrySessionStore.Serialize(pending).ToString());
        if (legacyPendingState)
        {
            pending.PendingClarification = pending.PendingClarification! with { CandidateTermHashes = null };
            await fixture.Store.SaveAsync("user-1", pending, CancellationToken.None);
        }

        FinanceReply reply = await fixture.RunReplyAsync(choice);

        Assert.Single(chat.Requests);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Equal("KPI-012", fixture.Query.Result!.Kpi.Code);
        Assert.Equal("region-emea", fixture.Query.Result.Organization.Code);
        Assert.Equal(new DateOnly(2025, 7, 1), fixture.Query.Result.Period.Start);
        Assert.Equal(new DateOnly(2025, 10, 1), fixture.Query.Result.Period.End);
        Assert.Equal(StatementTool.Render(fixture.Query.Result), reply.Text);
        Assert.Contains("-26.65%", reply.Text);
        Assert.Contains("-24.77%", reply.Text);
        Assert.Null(reply.Clarification);
        Assert.Same(fixture.Tokens, fixture.Statements.Caller);
        Assert.Equal(0, fixture.OtherTools.Calls);
        OrchestratorSessionState stored = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.Null(stored.PendingClarification);
        Assert.Equal(pending.AgentSessionJson, stored.AgentSessionJson);
    }

    [Fact]
    public async Task TypedChoiceAfterAnEarlierButtonClickUsesTheNewPendingStatement()
    {
        using var chat = new FakeChatClient(MarginCall("first"), MarginCall("second"));
        var fixture = new Fixture(chat);
        fixture.Query.Catalog = MarginCatalog(fixture.Query.Catalog);
        fixture.Query.Value = 43.22m;
        FinanceReply first = await fixture.RunReplyAsync("Show margin for EMEA in Q3 2025");
        ClarificationPrompt original = first.Clarification!;
        FinanceReply gross = await fixture.RunReplyAsync("",
            new(original.RequestId, "KPI-006", original.CatalogVersion));
        Assert.Equal("KPI-006", fixture.Query.Result!.Kpi.Code);
        Assert.Contains("43.22%", gross.Text);

        FinanceReply second = await fixture.RunReplyAsync("Show margin for EMEA in Q3 2025");
        Assert.NotEqual(original.RequestId, second.Clarification!.RequestId);
        Assert.Equal(3, second.Clarification.Options.Count);
        fixture.Query.Value = -26.65m;
        fixture.Query.PriorValue = -24.77m;
        FinanceReply operating = await fixture.RunReplyAsync("Operating Margin");

        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(2, fixture.Query.Calls);
        Assert.Equal("KPI-012", fixture.Query.Result!.Kpi.Code);
        Assert.Equal("region-emea", fixture.Query.Result.Organization.Code);
        Assert.Equal(new DateOnly(2025, 7, 1), fixture.Query.Result.Period.Start);
        Assert.Equal(new DateOnly(2025, 10, 1), fixture.Query.Result.Period.End);
        Assert.Contains("-26.65%", operating.Text);
        Assert.Contains("-24.77%", operating.Text);
        Assert.Null(operating.Clarification);
    }

    [Theory]
    [InlineData("margin")]
    [InlineData("profitability")]
    [InlineData("Operating Margin")]
    public async Task AmbiguousAliasesAndLabelsRetainPendingChoicesWithoutExecuting(string choice)
    {
        using var chat = new FakeChatClient(MarginCall("ambiguous"));
        var fixture = new Fixture(chat);
        fixture.Query.Catalog = MarginCatalog(fixture.Query.Catalog);
        fixture.Query.Catalog = fixture.Query.Catalog with
        {
            Entities = fixture.Query.Catalog.Entities.Select(entity => entity.Id switch
            {
                "KPI-006" => entity with { Aliases = [.. entity.Aliases, "profitability", "Operating Margin"] },
                "KPI-012" => entity with { Aliases = [.. entity.Aliases, "profitability"] },
                _ => entity
            }).ToArray()
        };
        FinanceReply prompt = await fixture.RunReplyAsync("Show margin for EMEA in Q3 2025");

        FinanceReply reply = await fixture.RunReplyAsync(choice);

        Assert.Contains("does not uniquely identify", reply.Text);
        Assert.Single(chat.Requests);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Equal(prompt.Clarification!.RequestId,
            (await fixture.Store.LoadAsync("user-1", CancellationToken.None)).PendingClarification!.RequestId);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("authorization")]
    [InlineData("expired")]
    [InlineData("owner")]
    public async Task TypedKpiAliasesCannotBypassPendingStatementValidation(string change)
    {
        using var chat = new FakeChatClient(MarginCall("ambiguous"));
        var fixture = new Fixture(chat);
        fixture.Query.Catalog = MarginCatalog(fixture.Query.Catalog);
        await fixture.RunReplyAsync("Show margin for EMEA in Q3 2025");
        OrchestratorSessionState state = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        switch (change)
        {
            case "release":
                fixture.Query.Catalog = fixture.Query.Catalog with
                {
                    Release = fixture.Query.Catalog.Release with { SearchIndex = "changed-index" }
                };
                break;
            case "authorization":
                fixture.Query.Catalog = fixture.Query.Catalog with
                {
                    Entities = fixture.Query.Catalog.Entities.Where(entity => entity.Id != "KPI-012").ToArray()
                };
                break;
            case "expired":
                state.PendingClarification = state.PendingClarification! with
                {
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
                };
                break;
            case "owner":
                state.PendingClarification = state.PendingClarification! with { OwnerSessionKey = "other-user" };
                break;
        }
        await fixture.Store.SaveAsync("user-1", state, CancellationToken.None);

        FinanceReply reply = await fixture.RunReplyAsync("operating profitability");

        Assert.DoesNotContain("Source:", reply.Text);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Single(chat.Requests);
    }

    private static ChatMessage MarginCall(string id) => new(ChatRole.Assistant,
        [new FunctionCallContent(id, FinanceToolNames.StatementTool,
            new Dictionary<string, object?> { ["kpi"] = "margin", ["org"] = "EMEA", ["dateRange"] = "Q3 2025" })]);

    private static ResolverCatalog MarginCatalog(ResolverCatalog catalog) => catalog with
    {
        Entities =
        [
            .. catalog.Entities,
            ResolverTestData.Entity("KPI-009", "kpi", "EBITDA Margin %", ["EBITDA margin", "margin"],
                "KPI-009"),
            ResolverTestData.Entity("KPI-006", "kpi", "Gross Margin %", ["gross margin", "margin"],
                "KPI-006"),
            ResolverTestData.Entity("KPI-012", "kpi", "Operating Margin %", ["operating margin", "operating profitability", "margin"],
                "KPI-012")
        ]
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UniqueDisplayedLabelsResumeBoundOptionsWithoutSendingLabelsToModel(bool longLabel, bool differentCase)
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("ambiguous-call", FinanceToolNames.StatementTool,
                new Dictionary<string, object?> { ["kpi"] = "Net Revenue", ["org"] = "market operations", ["dateRange"] = "2026" })]));
        var fixture = new Fixture(chat);
        fixture.Query.Catalog = UniqueDepartmentLabels(fixture.Query.Catalog, longLabel);
        FinanceReply reply = await fixture.RunReplyAsync("A scoped statement");
        ClarificationOption option = reply.Clarification!.Options[1];
        string label = differentCase ? "  " + option.Label.ToUpperInvariant() + "  " : option.Label;
        OrchestratorSessionState pendingState = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.DoesNotContain(option.Label, FoundrySessionStore.Serialize(pendingState).ToString());
        FinanceReply answer = await fixture.RunReplyAsync(label);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Equal(option.Id, fixture.Query.Result!.Organization.Code);
        Assert.Contains("Source:", answer.Text);
        Assert.Single(chat.Requests);
        OrchestratorSessionState state = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.Null(state.PendingClarification);
        Assert.DoesNotContain(option.Label, state.AgentSessionJson!);
        Assert.DoesNotContain("298.0", state.AgentSessionJson!);
        Assert.DoesNotContain(FakeTokens.Token, state.AgentSessionJson!);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("authorization")]
    [InlineData("expired")]
    [InlineData("owner")]
    public async Task LabelChoicesUseTheSameReleaseCallerExpiryAndAuthorizationChecksAsCards(string change)
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("ambiguous-call", FinanceToolNames.StatementTool,
                new Dictionary<string, object?> { ["kpi"] = "Net Revenue", ["org"] = "market operations", ["dateRange"] = "2026" })]));
        var fixture = new Fixture(chat);
        fixture.Query.Catalog = UniqueDepartmentLabels(fixture.Query.Catalog);
        FinanceReply reply = await fixture.RunReplyAsync("A scoped statement");
        ClarificationOption option = reply.Clarification!.Options[1];
        OrchestratorSessionState state = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        switch (change)
        {
            case "release":
                fixture.Query.Catalog = fixture.Query.Catalog with
                {
                    Release = fixture.Query.Catalog.Release with { SearchIndex = "changed-index" }
                };
                break;
            case "authorization":
                fixture.Query.Catalog = fixture.Query.Catalog with
                {
                    Entities = fixture.Query.Catalog.Entities.Where(entity => entity.Id != option.Id).ToArray()
                };
                break;
            case "expired":
                state.PendingClarification = state.PendingClarification! with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
                break;
            case "owner":
                state.PendingClarification = state.PendingClarification! with { OwnerSessionKey = "other-user" };
                break;
        }
        await fixture.Store.SaveAsync("user-1", state, CancellationToken.None);
        FinanceReply answer = await fixture.RunReplyAsync(option.Label);
        Assert.DoesNotContain("Source:", answer.Text);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Single(chat.Requests);
    }

    private static ResolverCatalog UniqueDepartmentLabels(ResolverCatalog catalog, bool longLabel = false) =>
        catalog with
        {
            Entities = catalog.Entities.Select(entity => entity.Id switch
            {
                "dept-east" => entity with { Name = "First unit" },
                "dept-west" => entity with { Name = "Second unit" + (longLabel ? new string('x', 220) : "") },
                _ => entity
            }).ToArray()
        };

    [Fact]
    public async Task ForgedAndOtherUserChoicesNeverExecuteOrCallTheRouter()
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("ambiguous-call", FinanceToolNames.StatementTool,
                new Dictionary<string, object?> { ["kpi"] = "Net Revenue", ["org"] = "Marketing", ["dateRange"] = "2026" })]));
        var fixture = new Fixture(chat);
        FinanceReply reply = await fixture.RunReplyAsync("A scoped statement");
        ClarificationPrompt prompt = reply.Clarification!;
        await fixture.RunReplyAsync("", new(prompt.RequestId, "company", "v1"));
        await fixture.RunReplyAsync("", new(prompt.RequestId, prompt.Options[0].Id, "v1"), "other-user");
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Single(chat.Requests);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("Hello")]
    public async Task ResetAndNewTurnsInvalidatePendingCards(string nextQuestion)
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("ambiguous-call", FinanceToolNames.StatementTool,
                new Dictionary<string, object?> { ["kpi"] = "Net Revenue", ["org"] = "Marketing", ["dateRange"] = "2026" })]),
            new ChatMessage(ChatRole.Assistant, "I can help with finance."));
        var fixture = new Fixture(chat);
        FinanceReply reply = await fixture.RunReplyAsync("A scoped statement");
        ClarificationPrompt prompt = reply.Clarification!;
        await fixture.RunReplyAsync(nextQuestion);
        FinanceReply expired = await fixture.RunReplyAsync("", new(prompt.RequestId, prompt.Options[0].Id, "v1"));
        Assert.Contains("no valid pending choice", expired.Text);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Equal(nextQuestion == "reset" ? 1 : 2, chat.Requests.Count);
        var state = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.Null(state.PendingClarification);
    }

    [Theory]
    [InlineData(FinanceToolNames.KpiInfoTool, "kpi", "I need a KPI name to look up.")]
    [InlineData(FinanceToolNames.ExploreFinanceTool, "question", "What would you like me to analyse?")]
    public async Task DispatchesNaturalLanguageToolsAndReturnsTheirClarificationVerbatim(
        string tool, string parameter, string expected)
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", tool,
                new Dictionary<string, object?> { [parameter] = string.Empty })]));
        var options = new FabricOptions { WorkspaceId = "ws", DataAgentId = "agent" };
        using var http = new HttpClient(new FabricMcpHandler { Answer = "unused" });
        var fabric = new FabricFactory(http, options);
        var fixture = new Fixture(chat, fabric, options);

        Assert.Equal(expected, await fixture.RunAsync("A question with a missing business argument"));
        Assert.Single(chat.Requests);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Equal(0, fixture.Tokens.Calls);
    }

    [Fact]
    public async Task FabricNativeCallPreservesMarkdownCitationsAndSourceWithoutModelSynthesis()
    {
        const string sourced = "**Margin** fell by 1.25%.\n\n| Driver | Impact |\n|---|---|\n"
            + "| Costs | -1.25% |\n\n[Evidence](https://example.test/finance)";
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", FinanceToolNames.ExploreFinanceTool,
                new Dictionary<string, object?> { ["question"] = "Why did margin fall?" })]));
        var options = new FabricOptions { WorkspaceId = "ws", DataAgentId = "agent" };
        using var handler = new FabricMcpHandler { Answer = sourced };
        using var http = new HttpClient(handler);
        var fabric = new FabricFactory(http, options);
        var fixture = new Fixture(chat, fabric, options);

        string answer = await fixture.RunAsync("Why did margin fall?");

        Assert.Equal(SourceFooter.Append(sourced, SourceFooter.DataAgent), answer);
        Assert.Single(chat.Requests);
        Assert.Same(fixture.Tokens, fabric.Caller);
        Assert.Single(handler.Requests, request => request.Method == "tools/call");
        Assert.Equal(0, fixture.OtherTools.Calls);
        Assert.Null(fixture.Statements.Caller);
        OrchestratorSessionState stored = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.DoesNotContain("1.25", stored.AgentSessionJson!);
        Assert.DoesNotContain("example.test/finance", stored.AgentSessionJson!);
        Assert.DoesNotContain(FakeTokens.Token, stored.AgentSessionJson!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutesOnceWithCallerIdentityAndReturnsTheExactStatement(bool reasoningEnabled)
    {
        using var chat = new FakeChatClient(StatementCall("call-1"));
        var fixture = new Fixture(chat, foundryOptions: new FoundryOptions { ReasoningEnabled = reasoningEnabled });
        string answer = await fixture.RunAsync("Show Net Revenue for EMEA in Q2 2026");
        Assert.Equal(StatementTool.Render(Assert.IsType<StatementResult>(fixture.Query.Result)), answer);
        Assert.Contains("Source: Zava finance lakehouse", answer);
        Assert.Single(chat.Requests);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Same(fixture.Tokens, fixture.Statements.Caller);
        Assert.Equal(1, fixture.Tokens.Calls);
        Assert.Equal(0, fixture.OtherTools.Calls);

        ChatOptions options = Assert.IsType<ChatOptions>(chat.Options.Single());
        Assert.Null(options.ResponseFormat);
        Assert.False(options.AllowMultipleToolCalls);
        Assert.Equal(3, options.Tools!.Count);
        Assert.All(options.Tools, tool =>
        {
            Assert.IsAssignableFrom<AIFunction>(tool);
        });
    }

    [Fact]
    public async Task RestoresPairedHistoryWithoutFinanceAnswersOrAccessTokens()
    {
        using var chat = new FakeChatClient(StatementCall("call-1"), StatementCall("call-2"));
        var fixture = new Fixture(chat);
        string first = await fixture.RunAsync("Show Net Revenue for EMEA in Q2 2026");
        OrchestratorSessionState stored = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        string json = Assert.IsType<string>(stored.AgentSessionJson);

        Assert.DoesNotContain(FakeTokens.Token, json);
        Assert.DoesNotContain("298.0", json);
        Assert.DoesNotContain("Source:", json);
        Assert.DoesNotContain("This model text", json);
        Assert.Equal("Net Revenue", stored.LastKpiName);

        string second = await fixture.RunAsync("Show that again");

        Assert.Equal(first, second);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(2, fixture.Query.Calls);
        ChatMessage[] request = chat.Requests[1];
        FunctionCallContent call = Assert.Single(request.SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>());
        FunctionResultContent result = Assert.Single(request.SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>());
        Assert.Equal(call.CallId, result.CallId);
        Assert.DoesNotContain(first, string.Join("\n", request.Select(m => m.Text)));
        Assert.DoesNotContain(FakeTokens.Token, result.Result?.ToString() ?? string.Empty);
        Assert.DoesNotContain("298.0", result.Result?.ToString() ?? string.Empty);
        Assert.Equal(FinanceHistory.WithheldResult, result.Result?.ToString());
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("clr-null")]
    [InlineData("json-null")]
    public async Task LegacyHistoryIsResetWithoutLosingStickyKpiOrSubagentConversation(string kpiArgument)
    {
        var arguments = new Dictionary<string, object?> { ["org"] = "EMEA", ["dateRange"] = "Q2 2026" };
        if (kpiArgument != "omitted")
        {
            arguments["kpi"] = kpiArgument == "json-null"
                ? JsonSerializer.SerializeToElement<string?>(null)
                : null;
        }
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-1", FinanceToolNames.StatementTool, arguments)
        ]));
        var fixture = new Fixture(chat);
        await fixture.Store.SaveAsync("user-1", new OrchestratorSessionState
        {
            AgentSessionJson = """{"legacyServerConversation":"must-not-be-replayed"}""",
            LastKpiName = "Net Revenue",
            CopilotStudioConversationId = "kpipedia-conversation"
        }, CancellationToken.None);

        string answer = await fixture.RunAsync("Show it for EMEA in Q2 2026");

        Assert.Contains("298.0", answer);
        Assert.Single(chat.Requests[0], message => message.Role == ChatRole.User);
        OrchestratorSessionState stored = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.Equal(OrchestratorAgent.NativeSessionVersion, stored.AgentSessionVersion);
        Assert.Equal("kpipedia-conversation", stored.CopilotStudioConversationId);
        Assert.Equal("Net Revenue", stored.LastKpiName);
        Assert.DoesNotContain("must-not-be-replayed", stored.AgentSessionJson!);
    }

    [Fact]
    public async Task SdkBindsJsonStatementArgumentsThroughTheProductionPath()
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-1", FinanceToolNames.StatementTool,
                new Dictionary<string, object?>
                {
                    ["kpi"] = JsonSerializer.SerializeToElement("Net Revenue"),
                    ["org"] = JsonSerializer.SerializeToElement("EMEA"),
                    ["dateRange"] = JsonSerializer.SerializeToElement("Q2 2026")
                })
        ]));
        var fixture = new Fixture(chat);

        string answer = await fixture.RunAsync("Show Net Revenue for EMEA in Q2 2026");

        Assert.Equal(StatementTool.Render(Assert.IsType<StatementResult>(fixture.Query.Result)), answer);
        Assert.Single(chat.Requests);
        Assert.Equal(1, fixture.Query.Calls);
        Assert.Equal(1, fixture.Tokens.Calls);
        Assert.Equal(0, fixture.OtherTools.Calls);
    }

    [Fact]
    public async Task OmittedStatementArgumentsUseMethodDefaultsForClarification()
    {
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", FinanceToolNames.StatementTool)]));
        var fixture = new Fixture(chat);

        Assert.Equal("Which KPI would you like a statement for?", await fixture.RunAsync("Show a statement"));
        Assert.Single(chat.Requests);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Equal(0, fixture.Tokens.Calls);
        Assert.Equal(0, fixture.OtherTools.Calls);
    }

    [Fact]
    public async Task SharedAgentBindsEachToolToTheCurrentCaller()
    {
        using var chat = new FakeChatClient(StatementCall("user-1-call"), StatementCall("user-2-call"));
        var fixture = new Fixture(chat);
        var secondCaller = new FakeTokens();

        await fixture.RunAsync("First user's statement");
        Assert.Same(fixture.Tokens, fixture.Statements.Caller);

        await fixture.RunAsync("Second user's statement", sessionKey: "user-2", tokens: secondCaller);

        Assert.Same(secondCaller, fixture.Statements.Caller);
        Assert.Equal(1, fixture.Tokens.Calls);
        Assert.Equal(1, secondCaller.Calls);
        Assert.Equal(2, fixture.Query.Calls);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(0, fixture.OtherTools.Calls);
    }

    [Fact]
    public async Task SharedAgentNeverSharesHistoryBetweenSessionKeys()
    {
        using var chat = new FakeChatClient(StatementCall("user-1-call"), StatementCall("user-2-call"));
        var fixture = new Fixture(chat);

        await fixture.RunAsync("First user's finance question");
        await fixture.RunAsync("Second user's finance question", sessionKey: "user-2");

        Assert.DoesNotContain(chat.Requests[1], message => message.Text.Contains("First user's"));
        Assert.DoesNotContain(chat.Requests[1].SelectMany(message => message.Contents),
            content => content is FunctionCallContent);
    }

    [Fact]
    public async Task ToolFailureDoesNotTriggerModelSynthesisOrPersistFailureDetails()
    {
        using var chat = new FakeChatClient(StatementCall("call-1"), StatementCall("call-2"));
        var fixture = new Fixture(chat);
        fixture.Query.Fail = true;

        string answer = await fixture.RunAsync("Show Net Revenue for EMEA in Q2 2026");

        Assert.Equal("I could not retrieve Net Revenue from the finance warehouse.", answer);
        Assert.Single(chat.Requests);
        OrchestratorSessionState stored = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.DoesNotContain("permission-denied-detail", stored.AgentSessionJson!);
        Assert.DoesNotContain("could not retrieve", stored.AgentSessionJson!);

        fixture.Query.Fail = false;
        Assert.Contains("298.0", await fixture.RunAsync("Try again"));
        Assert.Single(chat.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("multiple")]
    [InlineData("invalid-argument")]
    [InlineData("missing-argument")]
    [InlineData("empty")]
    public async Task InvalidSelectionNeverExecutesAndDoesNotPoisonTheNextTurn(string kind)
    {
        ChatMessage invalid = kind switch
        {
            "unknown" => new(ChatRole.Assistant, [new FunctionCallContent("bad-1", "delete_data")]),
            "multiple" => new(ChatRole.Assistant, [.. StatementCall("bad-1").Contents,
                .. StatementCall("bad-2").Contents]),
            "invalid-argument" => new(ChatRole.Assistant,
                [new FunctionCallContent("bad-1", FinanceToolNames.StatementTool,
                    new Dictionary<string, object?> { ["org"] = 123 })]),
            "missing-argument" => new(ChatRole.Assistant,
                [new FunctionCallContent("bad-1", FinanceToolNames.KpiInfoTool)]),
            _ => new(ChatRole.Assistant, string.Empty)
        };
        using var chat = new FakeChatClient(invalid, StatementCall("valid-1"));
        var fixture = new Fixture(chat);

        string answer = await fixture.RunAsync("First question");
        Assert.Contains("could not select a valid finance tool", answer);
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Equal(0, fixture.OtherTools.Calls);
        Assert.Null(fixture.Statements.Caller);

        await fixture.RunAsync("Show Net Revenue for EMEA in Q2 2026");
        Assert.Equal(1, fixture.Query.Calls);
        ChatMessage[] nextRequest = chat.Requests[1];
        string[] callIds = nextRequest.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Select(call => call.CallId).Order().ToArray();
        string[] resultIds = nextRequest.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(result => result.CallId).Order().ToArray();
        Assert.Equal(callIds, resultIds);
    }

    [Fact]
    public async Task NoToolUsesTheSingleModelResponseWithoutCreatingClients()
    {
        const string help = "I can explain KPIs or report finance figures.";
        using var chat = new FakeChatClient(new ChatMessage(ChatRole.Assistant, help));
        var fixture = new Fixture(chat);

        Assert.Equal(help, await fixture.RunAsync("Hello"));
        Assert.Single(chat.Requests);
        Assert.Null(fixture.Statements.Caller);
        Assert.Equal(0, fixture.OtherTools.Calls);
    }

    [Fact]
    public async Task NoToolPreservesMultipartTextAndReplaysItWithoutToolResults()
    {
        ChatMessage reply = new(ChatRole.Assistant,
        [
            new TextContent("**Finance help**\n\n"),
            new TextContent("I can explain KPIs, report figures, or analyse finance questions.")
        ]);
        using var chat = new FakeChatClient(reply, StatementCall("call-2"));
        var fixture = new Fixture(chat);
        string answer = await fixture.RunAsync("Hello");

        Assert.Equal(reply.Text, answer);
        Assert.Single(chat.Requests);
        Assert.Null(fixture.Statements.Caller);
        Assert.Equal(0, fixture.OtherTools.Calls);

        await fixture.RunAsync("Show Net Revenue for EMEA in Q2 2026");

        ChatMessage assistant = Assert.Single(chat.Requests[1], message => message.Role == ChatRole.Assistant);
        Assert.Equal(answer, assistant.Text);
        Assert.DoesNotContain(chat.Requests[1].SelectMany(message => message.Contents),
            content => content is FunctionCallContent or FunctionResultContent);
        Assert.Equal(2, chat.Requests.Count);
    }

    [Fact]
    public async Task CancellationBeforeDispatchDoesNotExecuteOrLeaveUnpairedCalls()
    {
        using var chat = new FakeChatClient(StatementCall("call-1"), StatementCall("call-2"));
        var fixture = new Fixture(chat);
        using var cts = new CancellationTokenSource();
        chat.AfterResponse = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync("First question",
            cancellationToken: cts.Token));
        Assert.Equal(0, fixture.Query.Calls);
        Assert.Null(fixture.Statements.Caller);
        OrchestratorSessionState stored = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
        Assert.NotNull(stored.AgentSessionJson);

        chat.AfterResponse = null;
        await fixture.RunAsync("Show Net Revenue for EMEA in Q2 2026");
        ChatMessage[] nextRequest = chat.Requests[1];
        Assert.Equal(nextRequest.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count(),
            nextRequest.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(20)]
    public async Task HistoryCapDropsWholeTurnsAndKeepsCallsPaired(int limit)
    {
        using var chat = new FakeChatClient(Enumerable.Range(0, 25)
            .Select(i => i % 2 == 0 ? StatementCall($"call-{i}")
                : new ChatMessage(ChatRole.Assistant, "I can help with finance.")).ToArray());
        var fixture = new Fixture(chat, options: new OrchestratorOptions { MaxHistoryMessages = limit });

        for (int i = 0; i < 25; i++)
        {
            string question = $"Question {i}";
            await fixture.RunAsync(question);
            ChatMessage[] sent = chat.Requests[i];
            Assert.InRange(sent.Length, 1, limit);
            Assert.Equal(ChatRole.User, sent[0].Role);
            Assert.Single(sent, message => message.Role == ChatRole.User && message.Text == question);
            Assert.Equal(
                sent.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId),
                sent.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId));

            OrchestratorSessionState stored = await fixture.Store.LoadAsync("user-1", CancellationToken.None);
            using JsonDocument json = JsonDocument.Parse(stored.AgentSessionJson!);
            AgentSession restored = await fixture.Agent.DeserializeSessionAsync(json.RootElement);
            var history = new FinanceHistory(limit);
            ChatMessage[] persisted = history.GetMessages(restored).ToArray();
            Assert.InRange(persisted.Length, 2, limit);
            Assert.Equal(ChatRole.User, persisted[0].Role);
            Assert.Equal(
                persisted.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId),
                persisted.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId));
        }
        Assert.DoesNotContain(chat.Requests[^1], message => message.Text == "Question 0");
    }

    private static ChatMessage StatementCall(string id) => new(ChatRole.Assistant,
    [
        new TextContent("This model text must not replace or prefix the tool answer."),
        new FunctionCallContent(id, FinanceToolNames.StatementTool,
            new Dictionary<string, object?>
            {
                ["kpi"] = "Net Revenue", ["org"] = "EMEA", ["dateRange"] = "Q2 2026"
            })
    ]);

    private sealed class Fixture
    {
        public Fixture(IChatClient chat, IFabricDataAgentClientFactory? fabric = null,
            FabricOptions? fabricOptions = null, OrchestratorOptions? options = null,
            FoundryOptions? foundryOptions = null, TimeProvider? time = null)
        {
            Statements = new StatementFactory(Query);
            var factory = new FinanceAgentFactory(chat, foundryOptions ?? new FoundryOptions());
            Agent = factory.Create([], (options ?? new OrchestratorOptions()).MaxHistoryMessages);
            Orchestrator = new OrchestratorAgent(factory, OtherTools, Store, Statements, fabric ?? OtherTools,
                fabricOptions ?? new FabricOptions(), options ?? new OrchestratorOptions(), time ?? TimeProvider.System,
                NullLoggerFactory.Instance);
        }

        public FakeTokens Tokens { get; } = new();
        public FakeQuery Query { get; } = new();
        public StatementFactory Statements { get; }
        public UnselectedFactories OtherTools { get; } = new();
        public MemorySessionStore Store { get; } = new();
        public AIAgent Agent { get; }
        private OrchestratorAgent Orchestrator { get; }

        public Task<string> RunAsync(string question,
            CancellationToken cancellationToken = default, string sessionKey = "user-1",
            IDownstreamTokenProvider? tokens = null) =>
            Orchestrator.RunAsync(tokens ?? Tokens, sessionKey, question,
                cancellationToken);

        public Task<FinanceReply> RunReplyAsync(string question, ClarificationSubmission? submission = null,
            string sessionKey = "user-1") =>
            Orchestrator.RunReplyAsync(Tokens, sessionKey, question, submission, CancellationToken.None);
    }

    private sealed class MemorySessionStore : IAgentSessionStore
    {
        private readonly Dictionary<string, BinaryData> _sessions = new();

        public Task<OrchestratorSessionState> LoadAsync(string sessionKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_sessions.TryGetValue(sessionKey, out BinaryData? data)
                ? FoundrySessionStore.Deserialize(data) : new OrchestratorSessionState());
        }

        public Task SaveAsync(string sessionKey, OrchestratorSessionState state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sessions[sessionKey] = FoundrySessionStore.Serialize(state);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTokens : IDownstreamTokenProvider
    {
        public const string Token = "unit-test-delegated-token-not-a-credential";
        public int Calls { get; private set; }
        public Task<string> GetTokenAsync(string[] scopes, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Token);
        }
    }

    private sealed class StatementFactory(FakeQuery query) : IStatementQueryFactory
    {
        public IDownstreamTokenProvider? Caller { get; private set; }
        public IStatementQuery Create(IDownstreamTokenProvider tokenProvider)
        {
            Caller = tokenProvider;
            query.Caller = tokenProvider;
            return query;
        }
    }

    private sealed class FabricFactory(HttpClient http, FabricOptions options) : IFabricDataAgentClientFactory
    {
        public IDownstreamTokenProvider? Caller { get; private set; }
        public FabricDataAgentClient Create(IDownstreamTokenProvider tokenProvider)
        {
            Caller = tokenProvider;
            return new FabricDataAgentClient(http, options,
                ct => tokenProvider.GetTokenAsync(FabricOptions.DataAgentScopes, ct), NullLogger.Instance);
        }
    }

    private sealed class FakeQuery : IStatementQuery, IResolverCatalog
    {
        public IDownstreamTokenProvider? Caller { get; set; }
        public StatementResult? Result { get; private set; }
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public decimal Value { get; set; } = 298_000_000m;
        public decimal PriorValue { get; set; } = 250_100_000m;

        public ResolverCatalog Catalog { get; set; } = ResolverTestData.Standard;
        public Task<ResolverRelease> GetReleaseAsync(CancellationToken cancellationToken) => Task.FromResult(Catalog.Release);
        public Task<ResolverCatalog> LoadCatalogAsync(CancellationToken cancellationToken) => Task.FromResult(Catalog);

        public async Task<StatementResult> GetStatementAsync(KpiDefinition kpi,
            OrganizationScope organization, FinancePeriod period, CancellationToken cancellationToken)
        {
            await Caller!.GetTokenAsync(FabricOptions.SqlScopes, cancellationToken);
            Calls++;
            if (Fail)
            {
                throw new InvalidOperationException("permission-denied-detail");
            }
            return Result = new(kpi, organization, period, Value, PriorValue, "USD");
        }
    }

    private sealed class UnselectedFactories : ICopilotStudioClientFactory, IFabricDataAgentClientFactory
    {
        public int Calls { get; private set; }
        public CopilotClient Create(IDownstreamTokenProvider tokenProvider)
        {
            Calls++;
            throw new InvalidOperationException("An unselected KPIpedia client must not be created.");
        }

        FabricDataAgentClient IFabricDataAgentClientFactory.Create(IDownstreamTokenProvider tokenProvider)
        {
            Calls++;
            throw new InvalidOperationException("An unselected Fabric client must not be created.");
        }
    }

    private sealed class FakeChatClient(params ChatMessage[] replies) : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public List<ChatOptions?> Options { get; } = [];
        public Action? AfterResponse { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            Options.Add(options?.Clone());
            if (Requests.Count > replies.Length)
            {
                throw new InvalidOperationException("Unexpected model synthesis call.");
            }
            AfterResponse?.Invoke();
            return Task.FromResult(new ChatResponse(replies[Requests.Count - 1]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
