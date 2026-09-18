// Copyright (c) Microsoft Corporation.

using Xunit;

namespace ZavaFinance.Tests.Routing;

/// <summary>
/// Resolves the routing eval configuration. Legacy Foundry__ names are local test settings;
/// hosted-agent manifests must use neutral model settings because FOUNDRY_* is reserved.
/// </summary>
public static class FoundryTestEnvironment
{
    public static string? ProjectEndpoint { get; } =
        Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
        ?? Environment.GetEnvironmentVariable("Foundry__ProjectEndpoint");

    public static string? ModelDeployment { get; } =
        Environment.GetEnvironmentVariable("ModelDeployment")
        ?? Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
        ?? Environment.GetEnvironmentVariable("Foundry__ModelDeployment");

    public static bool ReasoningEnabled =>
        bool.Parse(Environment.GetEnvironmentVariable("ModelReasoningEnabled") ?? "false");

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProjectEndpoint) && !string.IsNullOrWhiteSpace(ModelDeployment);

    public const string SkipReason =
        "Routing eval skipped. Set FOUNDRY_PROJECT_ENDPOINT and ModelDeployment, and use an Azure "
        + "credential, to run the golden set. Set ModelReasoningEnabled=true for low-effort reasoning.";
}

/// <summary>
/// A theory that calls the routing model. It is skipped rather than failed when Foundry is not
/// configured, so <c>dotnet test</c> stays green on a machine with no Azure credentials while
/// still failing loudly in an environment that does have them.
/// </summary>
public sealed class RequiresFoundryTheoryAttribute : TheoryAttribute
{
    public RequiresFoundryTheoryAttribute()
    {
        if (!FoundryTestEnvironment.IsConfigured)
        {
            Skip = FoundryTestEnvironment.SkipReason;
        }
    }
}

/// <inheritdoc cref="RequiresFoundryTheoryAttribute"/>
public sealed class RequiresFoundryFactAttribute : FactAttribute
{
    public RequiresFoundryFactAttribute()
    {
        if (!FoundryTestEnvironment.IsConfigured)
        {
            Skip = FoundryTestEnvironment.SkipReason;
        }
    }
}
