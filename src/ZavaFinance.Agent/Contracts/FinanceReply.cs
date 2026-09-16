namespace ZavaFinance.Contracts;

public sealed record ClarificationOption(string Id, string Label, string? Description = null);

public sealed record ClarificationPrompt(
    string RequestId, string Field, string Message,
    IReadOnlyList<ClarificationOption> Options, string CatalogVersion);

public sealed record ClarificationSubmission(string RequestId, string OptionId, string CatalogVersion);

public sealed record FinanceReply(string Text, ClarificationPrompt? Clarification = null);
