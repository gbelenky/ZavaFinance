targetScope = 'resourceGroup'

@description('Short name used to derive resource names.')
@minLength(3)
@maxLength(12)
param appName string = 'zavafin'

@description('Azure region. Must support App Service Premium v3 and Durable Task Scheduler.')
param location string = resourceGroup().location

@allowed([
  'dev'
  'staging'
  'prod'
])
param environmentName string = 'dev'

@description('Dedicated Linux App Service recommendation, not a measured sizing result.')
@allowed([
  'P1v3'
  'P2v3'
  'P3v3'
])
param appServiceSku string = 'P1v3'

@minValue(1)
param appServiceInstanceCount int = 1

@allowed([
  'Standard_LRS'
  'Standard_ZRS'
])
param storageSku string = 'Standard_LRS'

@minValue(30)
@maxValue(730)
param logRetentionInDays int = 30

@description('Use a distinct task hub for each independently deployed environment.')
param taskHubName string = 'zavafinance'

@description('DTS public endpoint source allowlist. Empty denies all traffic. Restrict to approved stable egress addresses where available.')
param schedulerIpAllowlist array = [
  '0.0.0.0/0'
]

@description('Non-overlapping address space approved by the network team.')
param vnetAddressPrefix string = '10.10.0.0/16'
param appSubnetAddressPrefix string = '10.10.1.0/24'
param privateEndpointSubnetAddressPrefix string = '10.10.2.0/24'

@description('Additional customer tags; solution and environment cannot be overridden.')
param additionalTags object = {}

@description('Client id of the Entra app registration backing the Azure Bot.')
param botAppId string

@description('Tenant id used for bot token validation.')
param botTenantId string = subscription().tenantId

@description('Responses endpoint of the deployed Foundry hosted agent.')
param hostedAgentResponsesEndpoint string

@description('Deployed hosted agent name, sent as the model identifier.')
param hostedAgentName string = 'zavafinance'

@description('Salt for the session key hash. Generate per environment and keep secret.')
@secure()
param sessionKeySalt string

@description('Client secret of the bot app registration, used for the OBO exchange. Stored as an encrypted Function App setting.')
@secure()
param botClientSecret string

@description('Object id of an approved DTS dashboard operator. The assigned data contributor role is operational access, not read-only.')
param operatorPrincipalId string = ''

@description('Only enable if adding a queue/blob trigger or an Azure Storage durable backend. DTS itself does not require this role.')
param enableQueueStorageRole bool = false

var suffix = uniqueString(resourceGroup().id, appName)
var storageName = take('st${toLower(appName)}${suffix}', 24)
var tags = union({
  purpose: 'demo'
  owner: 'gbelenky'
}, additionalTags, {
  solution: 'ZavaFinance'
  environment: environmentName
})

// ---------------------------------------------------------------------------
// Identity
// ---------------------------------------------------------------------------
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${appName}-${suffix}'
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: 'log-${appName}-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionInDays
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${appName}-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    // Message content is permissioned user data; sampling is fine, ingestion is not
    // enabled for prompt content by default.
    DisableIpMasking: false
  }
}

// ---------------------------------------------------------------------------
// Storage (Functions host). Managed identity only — shared keys disabled.
// ---------------------------------------------------------------------------
resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    name: storageSku
  }
  kind: 'StorageV2'
  properties: {
    allowSharedKeyAccess: false
    // Enforce the boundary in the template rather than relying on a policy mutation.
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
    }
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: 'deployments'
}

// Agents SDK IStorage: turn state, MAF conversation history, replay records,
// and proactive conversation references. Durable Task receives identifiers only.
resource stateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: 'agent-state'
}


// ---------------------------------------------------------------------------
// Durable Task Scheduler — orchestration backend.
// ---------------------------------------------------------------------------
resource scheduler 'Microsoft.DurableTask/schedulers@2026-02-01' = {
  name: 'dts-${appName}-${suffix}'
  location: location
  tags: tags
  properties: {
    ipAllowlist: schedulerIpAllowlist
    sku: {
      name: 'Consumption'
    }
  }
}

resource taskHub 'Microsoft.DurableTask/schedulers/taskHubs@2026-02-01' = {
  parent: scheduler
  name: taskHubName
  properties: {}
}

// ---------------------------------------------------------------------------
// The Function App reaches storage over private endpoints via VNet integration.
// ---------------------------------------------------------------------------
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${appName}-${suffix}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: 'snet-app'
        properties: {
          addressPrefix: appSubnetAddressPrefix
          delegations: [
            {
              name: 'delegation-appservice'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'snet-privatelink'
        properties: {
          addressPrefix: privateEndpointSubnetAddressPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource appSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-app'
}

resource privateLinkSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-privatelink'
}

var storageSubResources = [
  'blob'
  'queue'
  'table'
]

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for sub in storageSubResources: {
    name: 'privatelink.${sub}.${environment().suffixes.storage}'
    location: 'global'
    tags: tags
  }
]

resource dnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for (sub, i) in storageSubResources: {
    parent: privateDnsZones[i]
    name: 'link-${appName}'
    location: 'global'
    properties: {
      virtualNetwork: {
        id: vnet.id
      }
      registrationEnabled: false
    }
  }
]

resource storagePrivateEndpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [
  for (sub, i) in storageSubResources: {
    name: 'pe-${appName}-${sub}'
    location: location
    tags: tags
    properties: {
      subnet: {
        id: privateLinkSubnet.id
      }
      privateLinkServiceConnections: [
        {
          name: 'plsc-${sub}'
          properties: {
            privateLinkServiceId: storage.id
            groupIds: [
              sub
            ]
          }
        }
      ]
    }
  }
]

resource privateEndpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [
  for (sub, i) in storageSubResources: {
    parent: storagePrivateEndpoints[i]
    name: 'default'
    properties: {
      privateDnsZoneConfigs: [
        {
          name: sub
          properties: {
            privateDnsZoneId: privateDnsZones[i].id
          }
        }
      ]
    }
  }
]

// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
// One dedicated Function App hosts the Bot endpoint, custom orchestration
// triggers, MAF durable entities, and proactive continuation activities.
// Zip deploy writes to the app's own filesystem, not the private storage account.
// ---------------------------------------------------------------------------
resource hostingPlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: 'plan-${appName}-${suffix}'
  location: location
  tags: tags
  sku: {
    name: appServiceSku
    tier: 'PremiumV3'
    capacity: appServiceInstanceCount
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-11-01' = {
  name: 'app-${appName}-${suffix}'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  // Without this the host can start before the private DNS A records exist,
  // resolve the blocked public storage IP, and come up unhealthy.
  dependsOn: [
    privateEndpointDns
  ]
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    // VNet integration exists to reach the storage private endpoints. Those are
    // RFC1918 addresses inside this VNet, so they route over the integration
    // without forcing all egress through it. Foundry, Copilot Studio and Bot
    // Connector are public endpoints and keep their normal outbound path, which
    // is why no NAT gateway is required.
    virtualNetworkSubnetId: appSubnet.id
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      alwaysOn: true
      healthCheckPath: '/health'
      appSettings: [
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storage.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: identity.properties.clientId
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING'
          value: 'ClientId=${identity.properties.clientId};Authorization=AAD'
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: identity.properties.clientId
        }
        {
          name: 'DurableTask__ConnectionString'
          value: 'Endpoint=${scheduler.properties.endpoint};Authentication=ManagedIdentity;ClientID=${identity.properties.clientId};TaskHub=${taskHub.name}'
        }
        {
          name: 'DURABLE_TASK_SCHEDULER_CONNECTION_STRING'
          value: 'Endpoint=${scheduler.properties.endpoint};Authentication=ManagedIdentity;ClientID=${identity.properties.clientId};TaskHub=${taskHub.name}'
        }
        {
          name: 'TASKHUB_NAME'
          value: taskHub.name
        }
        {
          name: 'State__ContainerUri'
          value: '${storage.properties.primaryEndpoints.blob}${stateContainer.name}'
        }
        // The channel routes nothing and calls no downstream resource, so it needs no Foundry
        // model, Copilot Studio or Fabric configuration. Those belong to the hosted agent,
        // which holds them as its own settings.
        {
          name: 'HostedAgent__ResponsesEndpoint'
          value: hostedAgentResponsesEndpoint
        }
        {
          name: 'HostedAgent__AgentName'
          value: hostedAgentName
        }
        {
          name: 'TokenValidation__Audiences__0'
          value: botAppId
        }
        {
          name: 'TokenValidation__TenantId'
          value: botTenantId
        }
        {
          name: 'Orchestrator__SessionKeySalt'
          value: sessionKeySalt
        }
        {
          name: 'Orchestrator__UserAuthorizationHandler'
          value: 'mcs'
        }
        {
          name: 'Orchestrator__LogSubagentText'
          value: 'false'
        }
        {
          name: 'Connections__ServiceConnection__Settings__AuthType'
          value: 'ClientSecret'
        }
        {
          name: 'Connections__ServiceConnection__Settings__ClientId'
          value: botAppId
        }
        {
          name: 'Connections__ServiceConnection__Settings__ClientSecret'
          value: botClientSecret
        }
        {
          name: 'Connections__ServiceConnection__Settings__AuthorityEndpoint'
          value: '${environment().authentication.loginEndpoint}${botTenantId}'
        }
        {
          name: 'Connections__ServiceConnection__Settings__Scopes__0'
          value: 'https://api.botframework.com/.default'
        }
        {
          name: 'Connections__MCSConnection__Settings__AuthType'
          value: 'ClientSecret'
        }
        {
          name: 'Connections__MCSConnection__Settings__ClientId'
          value: botAppId
        }
        {
          name: 'Connections__MCSConnection__Settings__AuthorityEndpoint'
          value: '${environment().authentication.loginEndpoint}${botTenantId}'
        }
        {
          name: 'Connections__MCSConnection__Settings__ClientSecret'
          value: botClientSecret
        }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// Azure Bot — the router in front of the agent. One Teams channel serves both
// Teams and Microsoft 365 Copilot.
// ---------------------------------------------------------------------------
resource bot 'Microsoft.BotService/botServices@2023-09-15-preview' = {
  name: 'bot-${appName}-${suffix}'
  location: 'global'
  tags: tags
  sku: {
    name: 'F0'
  }
  kind: 'azurebot'
  properties: {
    displayName: 'Zava Finance'
    endpoint: 'https://${functionApp.properties.defaultHostName}/api/messages'
    msaAppId: botAppId
    msaAppType: 'SingleTenant'
    msaAppTenantId: botTenantId
    isStreamingSupported: false
  }
}

resource teamsChannel 'Microsoft.BotService/botServices/channels@2023-09-15-preview' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: 'global'
  properties: {
    channelName: 'MsTeamsChannel'
    properties: {
      isEnabled: true
    }
  }
}

// ---------------------------------------------------------------------------
// Azure data-plane RBAC uses managed identity. Bot/OBO credentials are separate.
// ---------------------------------------------------------------------------
var storageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageTableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var monitoringMetricsPublisher = '3913510d-42f4-4e42-8a64-420c390055eb'
var durableTaskDataContributor = '0ad04412-c4d5-4796-b79c-f76d14c8d402'

resource storageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, storageBlobDataOwner)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataOwner
    )
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableQueueStorageRole) {
  scope: storage
  name: guid(storage.id, identity.id, storageQueueDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageQueueDataContributor
    )
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, storageTableDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageTableDataContributor
    )
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource metricsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: appInsights
  name: guid(appInsights.id, identity.id, monitoringMetricsPublisher)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      monitoringMetricsPublisher
    )
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource durableTaskRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: scheduler
  name: guid(scheduler.id, identity.id, durableTaskDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      durableTaskDataContributor
    )
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Dashboard access exposes orchestration metadata and may expose conversation content after a
// future MAF Durable Extension migration, so it is granted explicitly.
resource operatorDurableTaskRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorPrincipalId)) {
  scope: scheduler
  name: guid(scheduler.id, operatorPrincipalId, durableTaskDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      durableTaskDataContributor
    )
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------
output functionAppName string = functionApp.name
output functionAppHostName string = functionApp.properties.defaultHostName
output messagingEndpoint string = 'https://${functionApp.properties.defaultHostName}/api/messages'
output botName string = bot.name
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output identityResourceId string = identity.id
output storageAccountName string = storage.name
output storageAccountId string = storage.id
output privateEndpointSubnetId string = privateLinkSubnet.id
output vnetId string = vnet.id
output logAnalyticsWorkspaceId string = logAnalytics.id
output stateContainerUri string = '${storage.properties.primaryEndpoints.blob}${stateContainer.name}'
output schedulerEndpoint string = scheduler.properties.endpoint
output schedulerId string = scheduler.id
output taskHubName string = taskHub.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
