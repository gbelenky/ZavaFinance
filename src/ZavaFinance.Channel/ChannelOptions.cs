namespace ZavaFinance.Channel;

public sealed class ChannelOptions
{
    // Preserve existing deployment settings and environment variable names.
    public const string SectionName = "Orchestrator";

    public string SessionKeySalt { get; set; } = string.Empty;

    public string UserAuthorizationHandler { get; set; } = "mcs";

    public string AcknowledgementText { get; set; } = "Working on that…";
}
