using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZavaFinance.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.Finance;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class FinanceServiceCollectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitModelSettingsOverrideSectionDefaults(bool reasoningEnabled)
    {
        Dictionary<string, string?> settings = ValidSettings();
        settings["Foundry:ModelDeployment"] = "section-model";
        settings["Foundry:ReasoningEnabled"] = (!reasoningEnabled).ToString();
        settings["ModelDeployment"] = "gpt-5.4-mini";
        settings["ModelReasoningEnabled"] = reasoningEnabled.ToString();
        var services = new ServiceCollection();
        services.AddFinanceServices(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            sessionStoreName: "zavafinance-one-sessions");
        using ServiceProvider provider = services.BuildServiceProvider();

        FoundryOptions options = provider.GetRequiredService<FoundryOptions>();
        Assert.Equal("gpt-5.4-mini", options.ModelDeployment);
        Assert.Equal(reasoningEnabled, options.ReasoningEnabled);
    }

    [Fact]
    public void OriginalReasoningRemainsDisabledWithoutOptIn()
    {
        var services = new ServiceCollection();
        services.AddFinanceServices(new ConfigurationBuilder().AddInMemoryCollection(ValidSettings()).Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<FoundryOptions>().ReasoningEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(15)]
    [InlineData(300)]
    [InlineData(600)]
    public void FabricMcpHttpTimeoutMatchesConfiguredAnalysisBudget(int? timeoutSeconds)
    {
        Dictionary<string, string?> settings = ValidSettings();
        if (timeoutSeconds is { } seconds)
        {
            settings["Fabric:DataAgentTimeout"] = TimeSpan.FromSeconds(seconds).ToString("c");
        }
        var services = new ServiceCollection();
        services.AddFinanceServices(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        using HttpClient fabric = factory.CreateClient(nameof(FabricDataAgentClient));
        using HttpClient resolver = factory.CreateClient(nameof(AzureResolverSearch));
        using HttpClient other = factory.CreateClient();

        Assert.Equal(provider.GetRequiredService<FabricOptions>().DataAgentTimeout, fabric.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(100), resolver.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(100), other.Timeout);
    }

    [Theory]
    [InlineData("Obo:ClientId")]
    [InlineData("Obo:TenantId")]
    [InlineData("Obo:Audience")]
    [InlineData("Orchestrator:SessionKeySalt")]
    public void MissingAuthorizationOrIsolationConfigurationFailsAtStartup(string key)
    {
        Dictionary<string, string?> settings = ValidSettings();
        settings.Remove(key);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddFinanceServices(configuration));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyStateStoreNameIsRejected(string storeName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(ValidSettings()).Build();
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddFinanceServices(configuration, sessionStoreName: storeName));
    }

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["Foundry:ProjectEndpoint"] = "https://test.services.ai.azure.com/api/projects/test",
        ["Obo:ClientId"] = "11111111-1111-1111-1111-111111111111",
        ["Obo:TenantId"] = "22222222-2222-2222-2222-222222222222",
        ["Obo:Audience"] = "api://unit-test",
        ["Obo:ClientSecret"] = "unit-test-not-a-real-credential",
        ["Orchestrator:SessionKeySalt"] = "unit-test-session-isolation-salt"
    };
}
