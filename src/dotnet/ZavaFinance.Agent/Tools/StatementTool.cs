// Copyright (c) Microsoft Corporation.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using ZavaFinance.Contracts;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;

namespace ZavaFinance.Core.Tools;

/// <summary>
/// Statement tool. Backed by a parameterised query against the Fabric lakehouse SQL analytics
/// endpoint, executed as the signed-in user.
/// </summary>
public sealed class StatementTool
{
    private readonly IStatementQuery? _query;
    private readonly OrchestratorSessionState _state;
    private readonly DateOnly _today;
    private readonly ILogger _logger;
    private readonly IResolverCatalog? _catalog;
    private readonly StatementResolver? _resolver;
    private readonly string _sessionKey;
    private readonly TimeProvider _time;
    private readonly ResolverOptions _options;

    public StatementTool(
        IStatementQuery? query,
        OrchestratorSessionState state,
        DateOnly today,
        ILogger logger,
        IResolverSearch? search = null,
        string sessionKey = "local",
        TimeProvider? timeProvider = null,
        ResolverOptions? resolverOptions = null)
    {
        _query = query;
        _state = state;
        _today = today;
        _logger = logger;
        _catalog = query as IResolverCatalog;
        _resolver = _catalog is null ? null : new StatementResolver(_catalog, search);
        _sessionKey = sessionKey;
        _time = timeProvider ?? TimeProvider.System;
        _options = resolverOptions ?? new ResolverOptions();
    }

    public async Task<string> GetStatementAsync(
        string? kpi = null,
        string org = "",
        string dateRange = "",
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(new StatementArguments(
            string.IsNullOrWhiteSpace(kpi) ? _state.LastKpiName : kpi, org, dateRange, AsOfDate: _today),
            expectedRelease: null, cancellationToken);

    public async Task<string> ContinueAsync(
        ClarificationSubmission submission, CancellationToken cancellationToken)
    {
        PendingClarification? pending = _state.PendingClarification;
        _state.ReplyClarification = null;
        if (pending is null || pending.OwnerSessionKey != _sessionKey
            || pending.RequestId != submission.RequestId
            || pending.CatalogVersion != submission.CatalogVersion
            || !pending.CandidateIds.Contains(submission.OptionId, StringComparer.Ordinal))
            return "That selection does not belong to your pending question. Please ask the statement again.";
        _state.PendingClarification = null;
        if (pending.ExpiresAtUtc <= _time.GetUtcNow())
            return "That clarification has expired. Please ask the statement again.";
        if (pending.Release is null || pending.Release.CatalogVersion != pending.CatalogVersion)
            return "The finance catalogue binding has changed. Please ask the statement again.";
        StatementArguments arguments = pending.Field == "kpi"
            ? pending.Arguments with { SelectedKpiId = submission.OptionId }
            : pending.Arguments with { SelectedOrganizationId = submission.OptionId };
        return await ExecuteAsync(arguments, pending.Release, cancellationToken);
    }

    private async Task<string> ExecuteAsync(
        StatementArguments arguments, ResolverRelease? expectedRelease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state.ReplyClarification = null;
        if (string.IsNullOrWhiteSpace(arguments.Kpi))
            return "Which KPI would you like a statement for?";
        if (string.IsNullOrWhiteSpace(arguments.Org))
            return $"Which organization should I report {arguments.Kpi} for? "
                + "You can name a region, a department, or the whole company.";
        FinancePeriod? period = FinancePeriodParser.Parse(arguments.DateRange, arguments.AsOfDate ?? _today);
        if (period is null)
            return string.IsNullOrWhiteSpace(arguments.DateRange)
                ? $"Which period should I report {arguments.Kpi} for, for example 'Q3 2026'?"
                : $"I could not interpret '{arguments.DateRange}' as a period. Try 'Q3 2026', "
                    + "'November 2025' or 'January to March 2026'.";
        if (_query is null)
            return "The finance warehouse is not configured in this environment.";
        if (_catalog is null || _resolver is null)
            return "The finance resolver catalogue is not configured in this environment.";
        try
        {
            ResolverCatalog snapshot = await _catalog.LoadCatalogAsync(cancellationToken);
            if (expectedRelease is not null && snapshot.Release != expectedRelease)
                throw new CatalogChangedException();
            EntityResolution kpi = await ResolveAsync(snapshot, arguments.Kpi,
                arguments.SelectedKpiId, "kpi", cancellationToken);
            if (kpi.Status != ResolutionStatus.Resolved)
                return ResolutionReply(snapshot, kpi, "kpi", arguments);
            ResolverEntity metric = kpi.Entity!;
            if (metric.Kind == "kpi_group")
            {
                ResolverEntity[] members = snapshot.Entities.Where(entity => entity.Kind == "kpi"
                    && entity.IsReportable && KpiCatalog.All.Any(item => item.Code == entity.KpiCode)
                    && IsDescendant(entity, metric.Id, snapshot)).ToArray();
                return members.Length == 0 ? "That KPI group has no supported reportable KPI. Please name one KPI."
                    : ResolutionReply(snapshot, new(ResolutionStatus.Clarify, members), "kpi", arguments);
            }
            // Vocabulary is Fabric-owned; this code list only identifies implemented arithmetic.
            KpiDefinition? calculation = KpiCatalog.All.SingleOrDefault(item => item.Code == metric.KpiCode);
            if (!metric.IsReportable || calculation is null)
                return "That KPI does not have a supported statement calculation. Please choose another KPI.";
            calculation = calculation with { Name = metric.Name };
            arguments = arguments with { SelectedKpiId = metric.Id };
            EntityResolution org = await ResolveAsync(snapshot, arguments.Org,
                arguments.SelectedOrganizationId, "org", cancellationToken);
            if (org.Status != ResolutionStatus.Resolved)
                return ResolutionReply(snapshot, org, "org", arguments);
            ResolverEntity organization = org.Entity!;
            if (!organization.IsReportable)
                return "That organization is not a reportable scope. Please specify a reportable organization.";
            if (snapshot.Release != await _catalog.GetReleaseAsync(cancellationToken))
                throw new CatalogChangedException();
            StatementResult result = await _query.GetStatementAsync(calculation,
                new OrganizationScope("org", organization.Id, organization.HierarchyPath.Length == 0
                    ? organization.Name : organization.HierarchyPath, snapshot.Version, snapshot.Release),
                period, cancellationToken);
            _state.LastKpiName = metric.Name;
            _state.PendingClarification = null;
            return Render(result);
        }
        catch (CatalogChangedException)
        {
            _state.PendingClarification = null;
            return "The finance catalogue has changed. Please ask the statement again so I can resolve the current choices.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"The finance warehouse did not respond in time for {arguments.Kpi}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("get_statement failed. ErrorType={ErrorType}", ex.GetType().Name);
            return $"I could not retrieve {arguments.Kpi} from the finance warehouse.";
        }
    }

    private Task<EntityResolution> ResolveAsync(
        ResolverCatalog snapshot, string rawTerm, string? selectedId, string field,
        CancellationToken cancellationToken)
    {
        if (selectedId is null)
            return _resolver!.ResolveAsync(snapshot, rawTerm, field, cancellationToken);
        ResolverEntity[] selected = snapshot.Entities.Where(entity =>
            entity.Id == selectedId && StatementResolver.InField(entity, field)).ToArray();
        return Task.FromResult(new EntityResolution(
            selected.Length == 1 ? ResolutionStatus.Resolved : ResolutionStatus.NoMatch, selected));
    }

    private string ResolutionReply(
        ResolverCatalog snapshot, EntityResolution resolution, string field, StatementArguments arguments)
    {
        string term = field == "kpi" ? arguments.Kpi! : arguments.Org;
        if (resolution.Status == ResolutionStatus.NoMatch)
            return $"I could not find an authorized {field} match for '{term}'. Please use a catalogue name or ID.";
        if (resolution.Candidates.Count > FinanceReplyProtocol.MaxOptions)
            return $"'{term}' has too many possible {field} matches. Please add a full name, region, or catalogue ID.";
        string message = $"Please confirm which {field} you mean by '{term}'.";
        string requestId = Guid.NewGuid().ToString("N");
        var options = resolution.Candidates.Select(entity => new ClarificationOption(entity.Id,
            Limit(entity.Name, 200)!, Limit(
                string.Join(" — ", new[] { entity.HierarchyPath, entity.Definition }
                    .Where(value => !string.IsNullOrWhiteSpace(value))), 1000))).ToArray();
        var prompt = new ClarificationPrompt(requestId, field, message, options, snapshot.Version);
        FinanceReplyProtocol.Validate(prompt);
        _state.PendingClarification = new PendingClarification(requestId, field, snapshot.Version,
            arguments, options.Select(option => option.Id).ToArray(),
            _time.GetUtcNow() + _options.ClarificationTimeToLive, _sessionKey, snapshot.Release,
            options.Select(option => ClarificationSelection.HashLabel(option.Label)).ToArray(),
            resolution.Candidates.Select(entity => (IReadOnlyList<string>)StatementResolver.Terms(entity)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Select(ClarificationSelection.HashLabel).Distinct(StringComparer.Ordinal).ToArray()).ToArray());
        _state.ReplyClarification = prompt;
        return message + "\n\n" + string.Join("\n", options.Select((option, index) =>
            $"{index + 1}. {option.Label}" + (option.Description is null ? "" : $" — {option.Description}")))
            + "\n\nReply with the option number or select a choice.";
    }

    private static string? Limit(string text, int count) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= count ? text : text[..(count - 1)] + "…";

    private static bool IsDescendant(ResolverEntity entity, string groupId, ResolverCatalog snapshot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? parent = entity.ParentId;
        while (parent is not null && seen.Add(parent))
        {
            if (parent == groupId) return true;
            parent = snapshot.Entities.SingleOrDefault(candidate => candidate.Id == parent)?.ParentId;
        }
        return false;
    }

    internal static string Render(StatementResult result)
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"**{result.Kpi.Name} — {result.Organization.Name} — {result.Period.Label}**");
        builder.AppendLine();

        if (result.Value is null)
        {
            // Reported as unavailable rather than as zero: a fabricated zero is a wrong number.
            builder.Append("No data is available for that combination.");

            return builder.ToString().TrimEnd();
        }

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"- {result.Period.Label}: {Format(result.Value.Value, result.Kpi, result.Currency)}");

        if (result.PriorValue is not null)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"- Prior period: {Format(result.PriorValue.Value, result.Kpi, result.Currency)}");

            string? change = Change(result.Value.Value, result.PriorValue.Value, result.Kpi);

            if (change is not null)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Change: {change}");
            }
        }

        // Attribution comes from the one shared definition, so all three tools name their
        // source the same way. The formula is included here because a computed figure is only
        // checkable if the arithmetic behind it is stated.
        return SourceFooter.Append(
            builder.ToString().TrimEnd(),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SourceFooter.Lakehouse}. {result.Kpi.Name} = {result.Kpi.Formula}"));
    }

    private static string Format(decimal value, KpiDefinition kpi, string currency) =>
        kpi.Unit switch
        {
            KpiUnit.Percent => string.Create(CultureInfo.InvariantCulture, $"{value:N2}%"),
            KpiUnit.Count => string.Create(CultureInfo.InvariantCulture, $"{value:N0} FTE"),
            KpiUnit.Days => string.Create(CultureInfo.InvariantCulture, $"{value:N1} days"),
            _ => FormatCurrency(value, currency)
        };

    private static string FormatCurrency(decimal value, string currency) =>
        Math.Abs(value) >= 1_000_000m
            ? string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000m:N1} M {currency}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:N0} {currency}");

    private static string? Change(decimal current, decimal prior, KpiDefinition kpi)
    {
        // A percentage KPI moves in percentage *points*. Expressing that as a percent change of
        // a percent is a category error that reads as a far larger move than occurred.
        if (kpi.Unit == KpiUnit.Percent)
        {
            decimal points = Math.Round(current - prior, 2);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Math.Abs(points):N2} pp {(points >= 0 ? "up" : "down")} versus prior period");
        }

        if (prior == 0)
        {
            return null;
        }

        decimal change = Math.Round((current - prior) / Math.Abs(prior) * 100m, 1);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(change):N1}% {(change >= 0 ? "up" : "down")} versus prior period");
    }
}
