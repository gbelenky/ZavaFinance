// Copyright (c) Microsoft Corporation.

using Azure.AI.AgentServer.Responses;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Identity.Client;
using ZavaFinance.Agent;
using ZavaFinance.Core.Agent;
using ZavaFinance.Core.Configuration;
using ZavaFinance.Core.CopilotStudio;
using ZavaFinance.Core.Finance;
using ZavaFinance.Core.Identity;

// The hosted-agent runtime contract - port 8088, GET /readiness, POST /responses, SSE and
// graceful shutdown - is implemented by the adapter. Only the handler below is ours.
ResponsesServer.Run<ZavaFinanceResponseHandler>(configure: builder =>
{
    IConfiguration configuration = builder.Configuration;

    // Emitted once at startup so a log always identifies which build is actually running.
    // Diagnosing a stale deployment from behaviour alone cost real time: a fixed defect kept
    // reproducing, and the only evidence that the running code was older than the source was a
    // three-line discrepancy in a stack trace.
    Console.WriteLine(
        $"ZavaFinance.Agent build {ThisAssembly.BuildMarker} starting.");

    var orchestratorOptions = new OrchestratorOptions();
    configuration.GetSection(OrchestratorOptions.SectionName).Bind(orchestratorOptions);

    var foundryOptions = new FoundryOptions();
    configuration.GetSection(FoundryOptions.SectionName).Bind(foundryOptions);

    // The platform injects FOUNDRY_PROJECT_ENDPOINT and reserves the whole FOUNDRY_* and
    // AGENT_* namespaces — declaring an environment variable that binds to the "Foundry"
    // configuration section is rejected at deploy time with a ValidationError. So the endpoint
    // is taken from the injected variable, and the model deployment travels under a name the
    // platform does not own.
    foundryOptions.ProjectEndpoint =
        Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
        ?? foundryOptions.ProjectEndpoint;

    foundryOptions.ModelDeployment =
        configuration["ModelDeployment"]
        ?? Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
        ?? foundryOptions.ModelDeployment;

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
        // Fail at startup rather than per turn. An agent that cannot perform the exchange can
        // only answer "I could not confirm who you are", which reads like an outage.
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

    var credential = new DefaultAzureCredential();

    builder.Services.AddSingleton(orchestratorOptions);
    builder.Services.AddSingleton(foundryOptions);
    builder.Services.AddSingleton(fabricOptions);
    builder.Services.AddSingleton(resolverOptions);
    builder.Services.AddSingleton(oboOptions);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHttpClient();

    builder.Services.AddSingleton(
        ConfidentialClientFactory.Create(oboOptions));

    builder.Services.AddSingleton(sp => new UserAssertionValidator(
        oboOptions.TenantId,
        oboOptions.Audience,
        sp.GetRequiredService<ILogger<UserAssertionValidator>>()));

    builder.Services.AddSingleton(new SessionKeyProvider(orchestratorOptions.SessionKeySalt));

    // Per-user state lives in the platform's own state store, keyed by a session key this agent
    // derives itself. That removes the customer storage account entirely — tenant policy forces
    // storage accounts to private-only, which a Foundry-managed container cannot reach without a
    // VNet-injected project, private endpoints and private DNS.
    builder.Services.AddSingleton<IAgentSessionStore>(_ => new FoundrySessionStore(
        storeName: "zavafinance-sessions",
        credential,
        itemTtl: orchestratorOptions.SessionTimeToLive));

    builder.Services.AddSingleton<ICopilotStudioClientFactory, CopilotStudioClientFactory>();
    builder.Services.AddSingleton<FabricClientFactory>();
    builder.Services.AddSingleton<IStatementQueryFactory>(
        sp => sp.GetRequiredService<FabricClientFactory>());
    builder.Services.AddSingleton<IFabricDataAgentClientFactory>(
        sp => sp.GetRequiredService<FabricClientFactory>());
    builder.Services.AddSingleton<IResolverSearch>(sp =>
    {
        // Runtime retrieval uses managed identity, never a search key or a caller's SQL token.
        var resolverCredential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        var project = new AIProjectClient(new Uri(foundryOptions.ProjectEndpoint), resolverCredential);
        IChatClient reranker = project.GetProjectOpenAIClient().GetResponsesClient()
            .AsIChatClient(foundryOptions.ModelDeployment);
        return new AzureResolverSearch(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AzureResolverSearch)),
            resolverCredential, resolverOptions, reranker, foundryOptions.ModelDeployment,
            sp.GetRequiredService<ILogger<AzureResolverSearch>>());
    });

    // Hosted execution and the golden-set evaluation share one routing definition.
    builder.Services.AddSingleton<AIAgent>(_ =>
    {
        var projectClient = new AIProjectClient(
            new Uri(foundryOptions.ProjectEndpoint), credential);

        return OrchestratorAgent.CreateRoutingAgent(projectClient, foundryOptions);
    });

    builder.Services.AddSingleton<OrchestratorAgent>();
});
