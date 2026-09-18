using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Identity;

namespace ZavaFinance.Agent;

public static class FinanceServiceCollectionExtensions
{
    public static IServiceCollection AddFinanceServices(
        this IServiceCollection services, IConfiguration configuration,
        string sessionStoreName = "zavafinance-sessions", TokenCredential? credential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionStoreName);
        var orchestratorOptions = new OrchestratorOptions();
        configuration.GetSection(OrchestratorOptions.SectionName).Bind(orchestratorOptions);
        var foundryOptions = new FoundryOptions();
        configuration.GetSection(FoundryOptions.SectionName).Bind(foundryOptions);

        // Hosted deployment reserves FOUNDRY_*; only the platform may inject the endpoint.
        foundryOptions.ProjectEndpoint =
            Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? foundryOptions.ProjectEndpoint;
        foundryOptions.ModelDeployment =
            configuration["ModelDeployment"]
            ?? Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
            ?? foundryOptions.ModelDeployment;
        foundryOptions.ReasoningEnabled =
            configuration.GetValue<bool?>("ModelReasoningEnabled")
            ?? foundryOptions.ReasoningEnabled;

        if (string.IsNullOrWhiteSpace(foundryOptions.ProjectEndpoint))
        {
            throw new InvalidOperationException(
                "FOUNDRY_PROJECT_ENDPOINT was not supplied. It is injected by the hosted-agent "
                + "platform, and must be set explicitly when running outside it.");
        }

        var fabricOptions = new FabricOptions();
        configuration.GetSection(FabricOptions.SectionName).Bind(fabricOptions);
        var resolverOptions = new ResolverOptions();
        configuration.GetSection(ResolverOptions.SectionName).Bind(resolverOptions);
        resolverOptions.Validate();
        var oboOptions = new OboOptions();
        configuration.GetSection(OboOptions.SectionName).Bind(oboOptions);
        if (!oboOptions.IsConfigured)
        {
            throw new InvalidOperationException(
                "Obo:ClientId, Obo:TenantId and Obo:Audience are required. Without them the agent "
                + "cannot exchange the forwarded user assertion for a delegated token.");
        }
        if (string.IsNullOrWhiteSpace(orchestratorOptions.SessionKeySalt))
        {
            throw new InvalidOperationException(
                "Orchestrator:SessionKeySalt is required. It is the isolation boundary's secret: "
                + "without it session keys are guessable from tenant, user and conversation ids.");
        }
        credential ??= new DefaultAzureCredential();
        services.AddSingleton(orchestratorOptions);
        services.AddSingleton(foundryOptions);
        services.AddSingleton(fabricOptions);
        services.AddSingleton(resolverOptions);
        services.AddSingleton(oboOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient(nameof(FabricDataAgentClient), (sp, client) =>
            client.Timeout = sp.GetRequiredService<FabricOptions>().DataAgentTimeout);
        services.AddSingleton(ConfidentialClientFactory.Create(oboOptions));
        services.AddSingleton(sp => new UserAssertionValidator(
            oboOptions.TenantId, oboOptions.Audience,
            sp.GetRequiredService<ILogger<UserAssertionValidator>>()));
        services.AddSingleton(new SessionKeyProvider(orchestratorOptions.SessionKeySalt));
        services.AddSingleton<IAgentSessionStore>(_ => new FoundrySessionStore(
            sessionStoreName, credential, orchestratorOptions.SessionTimeToLive));
        services.AddSingleton<ICopilotStudioClientFactory, CopilotStudioClientFactory>();
        services.AddSingleton<FabricClientFactory>();
        services.AddSingleton<IStatementQueryFactory>(
            sp => sp.GetRequiredService<FabricClientFactory>());
        services.AddSingleton<IFabricDataAgentClientFactory>(
            sp => sp.GetRequiredService<FabricClientFactory>());
        services.AddSingleton<IResolverSearch>(sp =>
        {
            var resolverCredential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
            var project = new AIProjectClient(new Uri(foundryOptions.ProjectEndpoint), resolverCredential);
            IChatClient reranker = project.GetProjectOpenAIClient().GetResponsesClient()
                .AsIChatClient(foundryOptions.ModelDeployment);
            return new AzureResolverSearch(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AzureResolverSearch)),
                resolverCredential, resolverOptions, reranker, foundryOptions.ModelDeployment,
                sp.GetRequiredService<ILogger<AzureResolverSearch>>(),
                reasoningEnabled: foundryOptions.ReasoningEnabled);
        });
        services.AddSingleton<IChatClient>(_ =>
            new AIProjectClient(new Uri(foundryOptions.ProjectEndpoint), credential)
                .GetProjectOpenAIClient().GetResponsesClient().AsIChatClient(foundryOptions.ModelDeployment));
        services.AddSingleton<FinanceAgentFactory>();
        services.AddSingleton<OrchestratorAgent>();
        return services;
    }
}
