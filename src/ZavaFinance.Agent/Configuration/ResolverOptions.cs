namespace ZavaFinance.Core.Configuration;

public sealed class ResolverOptions
{
    public const string SectionName = "Resolver";
    public string SearchEndpoint { get; set; } = "";
    public string SemanticConfigurationName { get; set; } = "resolver-semantic";
    public string EmbeddingEndpoint { get; set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan ClarificationTimeToLive { get; set; } = TimeSpan.FromMinutes(15);
    public int MaxCandidates { get; set; } = 8;
    public bool IsSearchConfigured => !string.IsNullOrWhiteSpace(SearchEndpoint)
        && !string.IsNullOrWhiteSpace(EmbeddingEndpoint);

    public void Validate()
    {
        if (MaxCandidates is < 1 or > 25
            || Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(2)
            || ClarificationTimeToLive <= TimeSpan.Zero || ClarificationTimeToLive > TimeSpan.FromDays(1))
            throw new InvalidOperationException("Resolver limits are outside their supported ranges.");
        string[] settings = [SearchEndpoint, EmbeddingEndpoint];
        if (settings.Any(value => !string.IsNullOrWhiteSpace(value)) && !IsSearchConfigured)
            throw new InvalidOperationException("Resolver Search and embedding settings must be configured together.");
        if (IsSearchConfigured && (string.IsNullOrWhiteSpace(SemanticConfigurationName)
            || !IsHttpsEndpoint(SearchEndpoint) || !IsHttpsEndpoint(EmbeddingEndpoint)))
            throw new InvalidOperationException("Resolver requires HTTPS endpoints and a semantic configuration.");
    }

    private static bool IsHttpsEndpoint(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;
}
