using Microsoft.Extensions.Configuration;
using ZavaFinance.ActivitySupport;
using Xunit;

namespace ZavaFinance.Tests;

public sealed class ActivityOptionsTests
{
    [Fact]
    public void AcknowledgementDefaultsToToolNeutralText()
    {
        var options = new ActivityOptions();
        Assert.Equal("Working on that…", options.AcknowledgementText);
        Assert.DoesNotContain("KPIpedia", options.AcknowledgementText);
        Assert.DoesNotContain("get_", options.AcknowledgementText);
    }

    [Fact]
    public void ExistingSsoHandlerAndSectionDefaultsRemainCompatible()
    {
        Assert.Equal("Orchestrator", ActivityOptions.SectionName);
        Assert.Equal("mcs", new ActivityOptions().UserAuthorizationHandler);
        Assert.Empty(new ActivityOptions().SessionKeySalt);
    }

    [Fact]
    public void OptionsRetainExistingConfigurationSectionWithoutAgentSettings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:SessionKeySalt"] = "test-salt",
            ["Orchestrator:UserAuthorizationHandler"] = "test-handler",
            ["Orchestrator:AcknowledgementText"] = "Custom acknowledgement",
            ["Orchestrator:MaxHistoryMessages"] = "100"
        }).Build();
        var options = new ActivityOptions();
        configuration.GetSection(ActivityOptions.SectionName).Bind(options);
        Assert.Equal("test-salt", options.SessionKeySalt);
        Assert.Equal("test-handler", options.UserAuthorizationHandler);
        Assert.Equal("Custom acknowledgement", options.AcknowledgementText);
        Assert.DoesNotContain(typeof(ActivityOptions).GetProperties(), p => p.Name == "MaxHistoryMessages");
        Assert.DoesNotContain(typeof(ActivityOptions).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "ZavaFinance.Agent");
    }
}
