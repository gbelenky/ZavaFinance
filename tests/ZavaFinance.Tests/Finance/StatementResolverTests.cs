using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Tools;
using Xunit;

namespace ZavaFinance.Tests.Finance;

internal static class ResolverTestData
{
    public static ResolverRelease Release => new("v1", "resolver-v1", "text-embedding-3-large", 1536);

    public static ResolverEntity Entity(string id, string kind, string name, string[]? aliases = null,
        string? code = null, string? parent = null, string? region = null, string? department = null,
        bool reportable = true) =>
        new("v1", id, kind, name, aliases ?? [], "Definition for " + name, parent,
            (region is null ? "" : region + " / ") + name, code, region, department, null, reportable);

    public static ResolverCatalog Standard => new(Release,
    [
        Entity("KPI-003", "kpi", "Net Revenue", ["net sales"], "KPI-003"),
        Entity("KPI-011", "kpi", "Operating Income (EBIT)", ["EBIT"], "KPI-011"),
        Entity("region-emea", "org", "EMEA", region: "EMEA"),
        Entity("region-apac", "org", "APAC", region: "APAC"),
        Entity("dept-east", "org", "Marketing", ["market operations"], region: "EMEA", department: "MKT"),
        Entity("dept-west", "org", "Marketing", ["market operations"], region: "APAC", department: "MKT"),
        Entity("company", "org", "All company", ["company"])
    ]);
}

public sealed class StatementResolverTests
{
    [Theory]
    [InlineData("KPI-003")]
    [InlineData(" NET SALES ")]
    [InlineData("Nét   Revenue")]
    public async Task ExactIdsNamesAndNormalizedAliasesResolveWithoutSearch(string term)
    {
        var query = new Query();
        var search = new Search();
        EntityResolution result = await new StatementResolver(query, search)
            .ResolveAsync(query.Snapshot, term, "kpi", CancellationToken.None);
        Assert.Equal(ResolutionStatus.Resolved, result.Status);
        Assert.Equal("KPI-003", result.Entity!.Id);
        Assert.Equal(0, search.Searches);
        Assert.Equal(0, search.Choices);
    }

    [Fact]
    public async Task SharedAliasCollisionAsksAllScopesWithoutUsingScoresOrModel()
    {
        var query = new Query();
        var search = new Search();
        EntityResolution result = await new StatementResolver(query, search)
            .ResolveAsync(query.Snapshot, "market operations", "org", CancellationToken.None);
        Assert.Equal(ResolutionStatus.Clarify, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(0, search.Searches);
        Assert.Equal(0, search.Choices);
    }

    [Fact]
    public async Task RetrievedNameCollisionCannotBePrunedToOneOrganizationByTheModel()
    {
        var query = new Query();
        var search = new Search
        {
            Hits = [new("dept-east", "v1"), new("dept-west", "v1")], Chosen = ["dept-east"]
        };
        EntityResolution result = await new StatementResolver(query, search)
            .ResolveAsync(query.Snapshot, "marketng", "org", CancellationToken.None);
        Assert.Equal(ResolutionStatus.Clarify, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(0, search.Choices);
    }

    [Fact]
    public async Task OneRetrievedSuggestionStillRequiresConfirmationWithoutAnExtraModelCall()
    {
        var query = new Query();
        var search = new Search { Hits = [new("KPI-003", "v1")] };
        EntityResolution result = await new StatementResolver(query, search)
            .ResolveAsync(query.Snapshot, "net reveneu", "kpi", CancellationToken.None);
        Assert.Equal(ResolutionStatus.Clarify, result.Status);
        Assert.Equal(0, search.Choices);
    }

    [Fact]
    public async Task PartialNameNeverUsesLongestContainedKpi()
    {
        var query = new Query();
        EntityResolution result = await new StatementResolver(query)
            .ResolveAsync(query.Snapshot, "income", "kpi", CancellationToken.None);
        Assert.Equal(ResolutionStatus.NoMatch, result.Status);
    }

    [Theory]
    [InlineData("company", "company")]
    [InlineData("EMEA", "region-emea")]
    [InlineData("EMEA / Marketing", "dept-east")]
    [InlineData("APAC / Marketing", "dept-west")]
    [InlineData("dept-east", "dept-east")]
    public async Task ExplicitScopesRemainVersionedNodesRatherThanBeingBroadened(string term, string expectedId)
    {
        var query = new Query();
        await Tool(query, new()).GetStatementAsync("Net Revenue", term, "2026");
        Assert.Equal(1, query.Calls);
        Assert.Equal(expectedId, query.Scope!.Code);
        Assert.Equal("v1", query.Scope.CatalogVersion);
        Assert.Equal(query.Snapshot.Release, query.Scope.Release);
    }

    [Fact]
    public async Task SearchMetadataIsRevalidatedBeforeCandidateModelAndRawTermIsPreserved()
    {
        var query = new Query();
        var search = new Search
        {
            Hits = [new("unauthorized", "v1"), new("region-emea", "old"),
                new("KPI-003", "v1"), new("KPI-011", "v1")],
            Chosen = ["KPI-011"]
        };
        EntityResolution result = await new StatementResolver(query, search)
            .ResolveAsync(query.Snapshot, "  operating ernings  ", "kpi", CancellationToken.None);
        Assert.Equal("  operating ernings  ", search.Raw);
        Assert.Equal(query.Snapshot.Release, search.Release);
        Assert.Equal(1, query.Loads);
        Assert.Equal(["KPI-003", "KPI-011"], search.Candidates!.Select(entity => entity.Id).Order());
        Assert.Equal(ResolutionStatus.Clarify, result.Status);
        Assert.Null(result.Entity);
    }

    [Theory]
    [InlineData("not-in-candidates")]
    [InlineData("company")]
    public async Task InventedOrBroaderModelIdsNeverResolve(string id)
    {
        var query = new Query();
        var search = new Search { Hits = [new("dept-east", "v1"), new("region-emea", "v1")], Chosen = [id] };
        EntityResolution result = await new StatementResolver(query, search)
            .ResolveAsync(query.Snapshot, "marketng", "org", CancellationToken.None);
        Assert.Equal(ResolutionStatus.Clarify, result.Status);
        Assert.Equal(["dept-east", "region-emea"], result.Candidates.Select(entity => entity.Id).Order());
    }

    [Fact]
    public async Task RevokedSearchHitNeverReachesModelOrUser()
    {
        var query = new Query();
        var search = new Search { Hits = [new("revoked", "v1")] };
        EntityResolution result = await new StatementResolver(query, search)
            .ResolveAsync(query.Snapshot, "restricted term", "org", CancellationToken.None);
        Assert.Equal(ResolutionStatus.NoMatch, result.Status);
        Assert.Equal(0, search.Choices);
    }

    [Fact]
    public async Task ReleaseChangedDuringRetrievalStopsResolution()
    {
        var query = new Query();
        ResolverCatalog old = query.Snapshot;
        query.Snapshot = old with { Release = old.Release with { CatalogVersion = "v2" } };
        var search = new Search { Hits = [new("dept-east", "v1")] };
        await Assert.ThrowsAsync<CatalogChangedException>(() =>
            new StatementResolver(query, search).ResolveAsync(old, "markting", "org", CancellationToken.None));
        Assert.Equal(0, search.Choices);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("deployment")]
    [InlineData("dimensions")]
    public async Task SameVersionBindingMutationDuringRetrievalFailsClosed(string mutation)
    {
        var query = new Query();
        ResolverCatalog old = query.Snapshot;
        query.Snapshot = old with { Release = ChangedBinding(old.Release, mutation) };
        var search = new Search { Hits = [new("dept-east", "v1")] };
        await Assert.ThrowsAsync<CatalogChangedException>(() =>
            new StatementResolver(query, search).ResolveAsync(old, "markting", "org", CancellationToken.None));
        Assert.Equal(0, search.Choices);
    }

    [Fact]
    public async Task NoMatchFromCandidateModelNeverRunsFinance()
    {
        var query = new Query();
        var search = new Search { Hits = [new("region-emea", "v1"), new("region-apac", "v1")], Chosen = [] };
        var tool = Tool(query, new(), search);
        string text = await tool.GetStatementAsync("Net Revenue", "unknown division", "2026");
        Assert.Contains("could not find", text);
        Assert.Equal(0, query.Calls);
    }

    [Fact]
    public async Task NumberedClarificationRetainsOriginalArgumentsAndExecutesOnlyChosenNode()
    {
        var query = new Query();
        var state = new OrchestratorSessionState();
        var tool = Tool(query, state);
        string text = await tool.GetStatementAsync("  NET SALES  ", "Marketing", "Q2 2026");
        Assert.Contains("1. Marketing", text);
        PendingClarification pending = Assert.IsType<PendingClarification>(state.PendingClarification);
        Assert.Equal("  NET SALES  ", pending.Arguments.Kpi);
        Assert.Equal("Marketing", pending.Arguments.Org);
        Assert.Equal("Q2 2026", pending.Arguments.DateRange);
        Assert.Equal(query.Snapshot.Release, pending.Release);
        Assert.Equal(0, query.Calls);
        Assert.True(ClarificationSelection.TryRead("second one", pending, out var selection));
        string answer = await tool.ContinueAsync(selection!, CancellationToken.None);
        Assert.Contains("Source:", answer);
        Assert.Equal(selection!.OptionId, query.Scope!.Code);
        Assert.Equal("v1", query.Scope.CatalogVersion);
        Assert.Equal(1, query.Calls);
        Assert.Null(state.PendingClarification);
    }

    [Fact]
    public async Task RelativeDateKeepsItsOriginalClockAcrossClarification()
    {
        var query = new Query();
        var state = new OrchestratorSessionState();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero));
        var tool = new StatementTool(query, state, new DateOnly(2026, 9, 30),
            NullLogger.Instance, sessionKey: "user", timeProvider: clock);
        await tool.GetStatementAsync("Net Revenue", "Marketing", "last month");
        PendingClarification pending = state.PendingClarification!;
        clock.Advance(TimeSpan.FromMinutes(2));
        tool = new StatementTool(query, state, new DateOnly(2026, 10, 1),
            NullLogger.Instance, sessionKey: "user", timeProvider: clock);
        await tool.ContinueAsync(new(pending.RequestId, pending.CandidateIds[0], "v1"), CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 8, 1), query.Period!.Start);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("user")]
    [InlineData("expired")]
    public async Task InvalidOrCrossUserSelectionNeverReadsFinance(string attack)
    {
        var query = new Query();
        var state = new OrchestratorSessionState();
        var clock = new FakeTimeProvider();
        var tool = Tool(query, state, clock: clock);
        await tool.GetStatementAsync("Net Revenue", "Marketing", "Q2 2026");
        PendingClarification pending = state.PendingClarification!;
        var submission = new ClarificationSubmission(pending.RequestId, pending.CandidateIds[0], "v1");
        switch (attack)
        {
            case "request": submission = submission with { RequestId = "forged" }; break;
            case "id": submission = submission with { OptionId = "company" }; break;
            case "version": submission = submission with { CatalogVersion = "v2" }; break;
            case "user": tool = Tool(query, state, owner: "another-user"); break;
            case "expired": clock.Advance(TimeSpan.FromHours(1)); break;
        }
        int loads = query.Loads;
        string answer = await tool.ContinueAsync(submission, CancellationToken.None);
        Assert.Contains("ask the statement again", answer);
        Assert.Equal(0, query.Calls);
        Assert.Equal(loads, query.Loads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishedVersionAndAuthorizationAreRecheckedAfterSelection(bool changedVersion)
    {
        var query = new Query();
        var state = new OrchestratorSessionState();
        var tool = Tool(query, state);
        await tool.GetStatementAsync("Net Revenue", "Marketing", "Q2 2026");
        PendingClarification pending = state.PendingClarification!;
        string id = pending.CandidateIds[0];
        query.Snapshot = changedVersion
            ? query.Snapshot with { Release = query.Snapshot.Release with { CatalogVersion = "v2" } }
            : query.Snapshot with { Entities = query.Snapshot.Entities.Where(entity => entity.Id != id).ToArray() };
        await tool.ContinueAsync(new(pending.RequestId, id, "v1"), CancellationToken.None);
        Assert.Equal(0, query.Calls);
        Assert.Null(state.PendingClarification);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("deployment")]
    [InlineData("dimensions")]
    public async Task SameVersionBindingMutationInvalidatesPendingChoice(string mutation)
    {
        var query = new Query();
        var state = new OrchestratorSessionState();
        var tool = Tool(query, state);
        await tool.GetStatementAsync("Net Revenue", "Marketing", "Q2 2026");
        PendingClarification pending = state.PendingClarification!;
        query.Snapshot = query.Snapshot with { Release = ChangedBinding(query.Snapshot.Release, mutation) };
        string text = await tool.ContinueAsync(
            new(pending.RequestId, pending.CandidateIds[0], "v1"), CancellationToken.None);
        Assert.Contains("catalogue has changed", text);
        Assert.Equal(0, query.Calls);
        Assert.Null(state.PendingClarification);
    }

    [Fact]
    public async Task LegacyPendingWithoutPublishedBindingCannotExecute()
    {
        var query = new Query();
        var state = new OrchestratorSessionState();
        var tool = Tool(query, state);
        await tool.GetStatementAsync("Net Revenue", "Marketing", "Q2 2026");
        PendingClarification pending = state.PendingClarification!;
        state.PendingClarification = pending with { Release = null };
        string text = await tool.ContinueAsync(
            new(pending.RequestId, pending.CandidateIds[0], "v1"), CancellationToken.None);
        Assert.Contains("binding has changed", text);
        Assert.Equal(0, query.Calls);
        Assert.Null(state.PendingClarification);
    }

    [Fact]
    public async Task GroupChoiceSelectsOneSupportedKpiAndNeverSumsGroup()
    {
        var query = new Query();
        query.Snapshot = new(ResolverTestData.Release,
        [
            ResolverTestData.Entity("group", "kpi_group", "Profitability", reportable: false),
            ResolverTestData.Entity("KPI-003", "kpi", "Net Revenue", code: "KPI-003", parent: "group"),
            ResolverTestData.Entity("KPI-011", "kpi", "Operating Income", code: "KPI-011", parent: "group"),
            ResolverTestData.Entity("region-emea", "org", "EMEA")
        ]);
        var state = new OrchestratorSessionState();
        var tool = Tool(query, state);
        await tool.GetStatementAsync("Profitability", "EMEA", "2026");
        Assert.Equal(0, query.Calls);
        Assert.Equal("kpi", state.PendingClarification!.Field);
        Assert.Equal(2, state.ReplyClarification!.Options.Count);
        var pending = state.PendingClarification;
        await tool.ContinueAsync(new(pending.RequestId, "KPI-011", "v1"), CancellationToken.None);
        Assert.Equal(1, query.Calls);
        Assert.Equal("KPI-011", query.Kpi!.Code);
    }

    [Fact]
    public async Task ExportedVocabularyCannotEnableAnUnimplementedCalculation()
    {
        var query = new Query { Snapshot = new(ResolverTestData.Release,
            [ResolverTestData.Entity("KPI-999", "kpi", "Unsupported", code: "KPI-999")]) };
        string text = await Tool(query, new()).GetStatementAsync("Unsupported", "EMEA", "2026");
        Assert.Contains("supported statement calculation", text);
        Assert.Equal(0, query.Calls);
    }

    [Theory]
    [InlineData("second one", 1)]
    [InlineData("the second option", 1)]
    [InlineData("option 2", 1)]
    [InlineData("2nd", 1)]
    [InlineData("1", 0)]
    public void TextChoicesAreBoundToPendingOrder(string text, int index)
    {
        var pending = new PendingClarification("request", "org", "v1", new(null, "", ""),
            ["a", "b"], DateTimeOffset.MaxValue, "user");
        Assert.True(ClarificationSelection.TryRead(text, pending, out var submission));
        Assert.Equal(pending.CandidateIds[index], submission!.OptionId);
        Assert.Equal(pending.RequestId, submission.RequestId);
    }

    [Theory]
    [InlineData("First unit", "a")]
    [InlineData("  FÍRST---unit  ", "a")]
    [InlineData("second   unit", "b")]
    [InlineData("  SECOND UNIT  ", "b")]
    [InlineData("b", "b")]
    public void LabelChoicesUseOnlyStoredDisplayedLabelsAndCandidateIds(string text, string expectedId)
    {
        var pending = new PendingClarification("request", "org", "v1", new(null, "", ""),
            ["a", "b"], DateTimeOffset.MaxValue, "user", ResolverTestData.Release,
            new[] { "First unit", "Second unit" }.Select(ClarificationSelection.HashLabel).ToArray());
        Assert.True(ClarificationSelection.TryRead(text, pending, out var submission));
        Assert.Equal(expectedId, submission!.OptionId);
        Assert.Equal(pending.RequestId, submission.RequestId);
        Assert.False(ClarificationSelection.TryRead("Third unit", pending, out _));
        Assert.False(ClarificationSelection.TryRead("First un", pending, out _));
    }

    [Fact]
    public void IdenticalTruncatedLabelsCannotPickAnArbitraryCandidate()
    {
        string label = new string('a', 199) + "…";
        var pending = new PendingClarification("request", "org", "v1", new(null, "", ""),
            ["a", "b"], DateTimeOffset.MaxValue, "user", ResolverTestData.Release,
            [ClarificationSelection.HashLabel(label), ClarificationSelection.HashLabel(label)]);
        Assert.True(ClarificationSelection.TryRead(label, pending, out var submission));
        Assert.Null(submission);
    }

    [Fact]
    public void DistinctLabelsThatNormalizeIdenticallyRemainAmbiguous()
    {
        var pending = new PendingClarification("request", "org", "v1", new(null, "", ""),
            ["a", "b"], DateTimeOffset.MaxValue, "user", ResolverTestData.Release,
            [ClarificationSelection.HashLabel("Márketing & Sales"),
                ClarificationSelection.HashLabel("marketing and sales")]);
        Assert.True(ClarificationSelection.TryRead("MARKETING & SALES", pending, out var submission));
        Assert.Null(submission);
        Assert.True(ClarificationSelection.TryRead("second one", pending, out submission));
        Assert.Equal("b", submission!.OptionId);
    }

    private static StatementTool Tool(Query query, OrchestratorSessionState state, Search? search = null,
        string owner = "user", TimeProvider? clock = null) =>
        new(query, state, new DateOnly(2026, 9, 16), NullLogger.Instance, search, owner, clock);

    private static ResolverRelease ChangedBinding(ResolverRelease release, string mutation) =>
        mutation switch
        {
            "index" => release with { SearchIndex = "different-index" },
            "deployment" => release with { EmbeddingDeployment = "different-model" },
            _ => release with { EmbeddingDimensions = 3072 }
        };

    private sealed class Query : IStatementQuery, IResolverCatalog
    {
        public ResolverCatalog Snapshot { get; set; } = ResolverTestData.Standard;
        public int Loads { get; private set; }
        public int Calls { get; private set; }
        public OrganizationScope? Scope { get; private set; }
        public KpiDefinition? Kpi { get; private set; }
        public FinancePeriod? Period { get; private set; }
        public Task<ResolverRelease> GetReleaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot.Release);
        public Task<ResolverCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(Snapshot);
        }
        public Task<StatementResult> GetStatementAsync(
            KpiDefinition kpi, OrganizationScope organization, FinancePeriod period, CancellationToken cancellationToken)
        {
            Calls++;
            Scope = organization;
            Kpi = kpi;
            Period = period;
            return Task.FromResult(new StatementResult(kpi, organization, period, 42m, 40m, "USD"));
        }
    }

    private sealed class Search : IResolverSearch
    {
        public IReadOnlyList<ResolverCandidateId> Hits { get; set; } = [];
        public IReadOnlyList<string>? Chosen { get; set; }
        public string? Raw { get; private set; }
        public ResolverRelease? Release { get; private set; }
        public IReadOnlyList<ResolverEntity>? Candidates { get; private set; }
        public int Searches { get; private set; }
        public int Choices { get; private set; }
        public Task<IReadOnlyList<ResolverCandidateId>> SearchAsync(
            string rawTerm, string field, ResolverRelease release, CancellationToken cancellationToken)
        {
            Raw = rawTerm;
            Release = release;
            Searches++;
            return Task.FromResult(Hits);
        }
        public Task<IReadOnlyList<string>?> ChooseAsync(
            string rawTerm, string field, IReadOnlyList<ResolverEntity> candidates, CancellationToken cancellationToken)
        {
            Candidates = candidates;
            Choices++;
            return Task.FromResult(Chosen);
        }
    }
}
