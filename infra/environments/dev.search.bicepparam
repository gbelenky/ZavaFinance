using '../search.bicep'

param environmentName = 'dev'
param searchServiceName = readEnvironmentVariable('ZAVA_DEV_SEARCH_NAME', 'srch-zavafin-dev')
param location = readEnvironmentVariable('ZAVA_DEV_SEARCH_LOCATION', 'swedencentral')
param agentPrincipalId = readEnvironmentVariable('ZAVA_DEV_AGENT_PRINCIPAL_ID')
param publisherPrincipalId = readEnvironmentVariable('ZAVA_DEV_PUBLISHER_PRINCIPAL_ID')
param publisherPrincipalType = readEnvironmentVariable('ZAVA_DEV_PUBLISHER_PRINCIPAL_TYPE', 'ServicePrincipal')
param network = json(readEnvironmentVariable('ZAVA_DEV_SEARCH_NETWORK'))
param logAnalyticsWorkspaceId = readEnvironmentVariable('ZAVA_DEV_LOG_ANALYTICS_ID')
param sku = 'standard'
param replicaCount = 1
param partitionCount = 1
param additionalTags = json(readEnvironmentVariable('ZAVA_DEV_TAGS', '{}'))
