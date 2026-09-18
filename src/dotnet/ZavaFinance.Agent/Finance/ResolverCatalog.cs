using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZavaFinance.Core.Finance;

public sealed record ResolverEntity(
    string CatalogVersion,
    string Id,
    string Kind,
    string Name,
    IReadOnlyList<string> Aliases,
    string Definition,
    string? ParentId,
    string HierarchyPath,
    string? KpiCode,
    string? RegionCode,
    string? DepartmentCode,
    string? DepartmentGroup,
    bool IsReportable);

public sealed record ResolverRelease(
    string CatalogVersion, string SearchIndex, string EmbeddingDeployment, int EmbeddingDimensions)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CatalogVersion) || CatalogVersion.Length > 64
            || !Regex.IsMatch(CatalogVersion, @"\A[a-zA-Z0-9][a-zA-Z0-9.-]*\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            || string.IsNullOrWhiteSpace(SearchIndex)
            || !Regex.IsMatch(SearchIndex, @"\A[a-z0-9][a-z0-9-]{1,126}[a-z0-9]\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            || string.IsNullOrWhiteSpace(EmbeddingDeployment) || EmbeddingDeployment.Length > 64
            || !Regex.IsMatch(EmbeddingDeployment, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            || EmbeddingDimensions is < 1 or > 3072)
            throw new InvalidDataException("The published resolver release has an invalid Search/embedding binding.");
    }
}

public sealed record ResolverCatalog(ResolverRelease Release, IReadOnlyList<ResolverEntity> Entities)
{
    public string Version => Release.CatalogVersion;
}

/// <summary>Every read is authorized with the current caller's delegated SQL token.</summary>
public interface IResolverCatalog
{
    Task<ResolverRelease> GetReleaseAsync(CancellationToken cancellationToken);
    Task<ResolverCatalog> LoadCatalogAsync(CancellationToken cancellationToken);
}

public sealed record ResolverCandidateId(string Id, string CatalogVersion);

public interface IResolverSearch
{
    Task<IReadOnlyList<ResolverCandidateId>> SearchAsync(
        string rawTerm, string field, ResolverRelease release, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>?> ChooseAsync(
        string rawTerm, string field, IReadOnlyList<ResolverEntity> authorizedCandidates,
        CancellationToken cancellationToken);
}

public enum ResolutionStatus { Resolved, Clarify, NoMatch }

public sealed record EntityResolution(
    ResolutionStatus Status, IReadOnlyList<ResolverEntity> Candidates)
{
    public ResolverEntity? Entity => Status == ResolutionStatus.Resolved ? Candidates.Single() : null;
}

public static class ResolverNormalization
{
    public static string Normalize(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                result.Append(c);
            else if (c == '%')
                result.Append(" percent ");
            else if (c == '&')
                result.Append(" and ");
            else
                result.Append(' ');
        }
        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Only unambiguous exact metadata matches execute without a human confirmation.</summary>
public sealed class StatementResolver(IResolverCatalog catalog, IResolverSearch? search = null)
{
    public async Task<EntityResolution> ResolveAsync(
        ResolverCatalog snapshot, string rawTerm, string field, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = ResolverNormalization.Normalize(rawTerm);
        if (normalized.Length == 0) return new(ResolutionStatus.NoMatch, []);

        ResolverEntity[] eligible = snapshot.Entities.Where(entity => InField(entity, field)).ToArray();
        // An explicit ID identifies a single node, even if its display name is shared.
        ResolverEntity[] ids = eligible.Where(entity =>
            string.Equals(entity.Id, rawTerm.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ids.Length > 0) return Exact(ids);

        ResolverEntity[] matches = eligible.Where(entity =>
            Terms(entity).Any(term => ResolverNormalization.Normalize(term) == normalized)).ToArray();
        if (matches.Length > 0) return Exact(matches);

        if (search is null) return new(ResolutionStatus.NoMatch, []);
        IReadOnlyList<ResolverCandidateId> hits =
            await search.SearchAsync(rawTerm, field, snapshot.Release, cancellationToken);
        if (hits.Count == 0) return new(ResolutionStatus.NoMatch, []);

        // Search is privileged vocabulary retrieval, not an authorization boundary. Reload
        // metadata as the caller before sending ANY candidate text to a model or user.
        ResolverCatalog fresh = await catalog.LoadCatalogAsync(cancellationToken);
        if (fresh.Release != snapshot.Release)
            throw new CatalogChangedException();

        HashSet<string> idsFound = hits.Where(hit => hit.CatalogVersion == fresh.Version)
            .Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal);
        ResolverEntity[] candidates = fresh.Entities
            .Where(entity => InField(entity, field) && idsFound.Contains(entity.Id))
            .OrderBy(entity => entity.HierarchyPath, StringComparer.Ordinal)
            .ThenBy(entity => entity.Id, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return new(ResolutionStatus.NoMatch, []);
        if (candidates.Length == 1 || HasVocabularyCollision(candidates))
            return new(ResolutionStatus.Clarify, candidates);

        IReadOnlyList<string>? choices =
            await search.ChooseAsync(rawTerm, field, candidates, cancellationToken);
        if (choices is { Count: 0 }) return new(ResolutionStatus.NoMatch, []);
        if (choices is not null)
        {
            var allowed = candidates.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal);
            // Invalid model output must not become an arbitrary top score winner.
            if (choices.Distinct(StringComparer.Ordinal).Count() != choices.Count
                || choices.Any(id => !allowed.Contains(id)))
                return new(ResolutionStatus.Clarify, candidates);
            candidates = candidates.Where(entity => choices.Contains(entity.Id, StringComparer.Ordinal)).ToArray();
        }
        // Even a single semantic suggestion is confirmed. A similarity score (or model
        // preference) is not proof of identity and cannot silently broaden a finance scope.
        return new(ResolutionStatus.Clarify, candidates);
    }

    internal static bool InField(ResolverEntity entity, string field) =>
        field == "org" ? entity.Kind == "org" : entity.Kind is "kpi" or "kpi_group";

    internal static IEnumerable<string> Terms(ResolverEntity entity) =>
        entity.Aliases.Prepend(entity.Name).Append(entity.HierarchyPath).Append(entity.KpiCode ?? "");

    private static bool HasVocabularyCollision(IEnumerable<ResolverEntity> candidates) =>
        candidates.SelectMany(entity => Terms(entity).Select(ResolverNormalization.Normalize)
                .Where(term => term.Length > 0).Distinct(StringComparer.Ordinal)
                .Select(term => (Term: term, entity.Id)))
            .GroupBy(item => item.Term, StringComparer.Ordinal)
            .Any(group => group.Select(item => item.Id).Distinct(StringComparer.Ordinal).Skip(1).Any());

    private static EntityResolution Exact(ResolverEntity[] matches) =>
        new(matches.Length == 1 ? ResolutionStatus.Resolved : ResolutionStatus.Clarify,
            matches.OrderBy(entity => entity.HierarchyPath, StringComparer.Ordinal)
                .ThenBy(entity => entity.Id, StringComparer.Ordinal).ToArray());
}

public sealed class CatalogChangedException() : Exception("The published catalogue changed.");
