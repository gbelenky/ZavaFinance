// Copyright (c) Microsoft Corporation.

using Xunit;

namespace ZavaFinance.Tests.Routing;

/// <summary>
/// Resolves the Foundry configuration the routing eval needs. The names match the Function App
/// settings (<c>Foundry__ProjectEndpoint</c>, <c>Foundry__ModelDeployment</c>) so the same values
/// work locally and in CI without a second naming convention.
/// </summary>
public static class FoundryTestEnvironment
{
    public static string? ProjectEndpoint { get; } =
        Environment.GetEnvironmentVariable("Foundry__ProjectEndpoint");

    public static string? ModelDeployment { get; } =
        Environment.GetEnvironmentVariable("Foundry__ModelDeployment");

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProjectEndpoint) && !string.IsNullOrWhiteSpace(ModelDeployment);

    public const string SkipReason =
        "Routing eval skipped. Set Foundry__ProjectEndpoint and Foundry__ModelDeployment, and "
        + "sign in with 'az login', to run the golden set against the real routing model.";
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
