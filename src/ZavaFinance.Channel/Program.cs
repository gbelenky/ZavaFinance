// Copyright (c) Microsoft Corporation.

using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.Agents.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenTelemetry;
using ZavaFinance.Channel;
using ZavaFinance.Core.Identity;

FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

var orchestratorOptions = new ChannelOptions();
builder.Configuration.GetSection(ChannelOptions.SectionName).Bind(orchestratorOptions);

var hostedAgentOptions = new HostedAgentOptions();
builder.Configuration.GetSection(HostedAgentOptions.SectionName).Bind(hostedAgentOptions);

if (!hostedAgentOptions.IsConfigured)
{
    throw new InvalidOperationException(
        "HostedAgent:ResponsesEndpoint is required. The channel routes nothing itself; it "
        + "forwards the turn to the Foundry hosted agent.");
}

if (string.IsNullOrWhiteSpace(orchestratorOptions.SessionKeySalt))
{
    throw new InvalidOperationException("Orchestrator:SessionKeySalt is required.");
}

builder.Services.AddSingleton(orchestratorOptions);
builder.Services.AddSingleton(hostedAgentOptions);
builder.Services.AddSingleton(TimeProvider.System);

var credential = new DefaultAzureCredential();
builder.Services.AddSingleton<Azure.Core.TokenCredential>(credential);

builder.Services.AddSingleton<BlobContainerClient>(_ =>
{
    string container = builder.Configuration["State:ContainerUri"]
        ?? throw new InvalidOperationException("State:ContainerUri is required.");

    return new BlobContainerClient(new Uri(container), credential);
});

builder.Services.AddSingleton<IStorage>(services =>
    new BlobsStorage(services.GetRequiredService<BlobContainerClient>()));
builder.Services.AddSingleton(services =>
    new ChannelSessionStore(new ChannelBlobStorage(services.GetRequiredService<BlobContainerClient>())));
builder.Services.AddSingleton<ChannelTurnProcessor>();
builder.Services.AddSingleton<ISessionKeyProvider>(
    _ => new SessionKeyProvider(orchestratorOptions.SessionKeySalt));
builder.Services.AddSingleton<ICallerIdentityResolver>(
    _ => new CallerIdentityResolver(orchestratorOptions.UserAuthorizationHandler));

builder.Services
    .AddHttpClient<IHostedAgentClient, FoundryHostedAgentClient>(client =>
        client.Timeout = hostedAgentOptions.Timeout);

builder.Services.AddSingleton<IOrchestratorTurnScheduler, DurableTaskOrchestratorTurnScheduler>();

builder.Services.AddDurableTaskClient(client =>
    client.UseDurableTaskScheduler(
        builder.Configuration["DurableTask:ConnectionString"]
        ?? throw new InvalidOperationException("DurableTask:ConnectionString is required.")));

builder.AddAgentDefaults();
builder.AddAgent<OrchestratorChannel>();

// Bot Service presents a signed JWT. Without forced authorization an unauthenticated caller
// reaches the adapter, which is a live vulnerability rather than a theoretical one.
builder.AddAgentAuthorization(
    b => b.Services.AddAgentTokenValidation(b.Configuration), forceEnable: true);

builder.UseMicrosoftOpenTelemetry(o => o.Exporters = ExportTarget.AzureMonitor);

using IHost app = builder.Build();
app.Run();
