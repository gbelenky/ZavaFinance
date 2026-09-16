using '../search.bicep'

param environmentName = 'staging'
param searchServiceName = readEnvironmentVariable('ZAVA_STAGING_SEARCH_NAME')
param location = readEnvironmentVariable('ZAVA_STAGING_SEARCH_LOCATION')
param agentPrincipalId = readEnvironmentVariable('ZAVA_STAGING_AGENT_PRINCIPAL_ID')
param publisherPrincipalId = readEnvironmentVariable('ZAVA_STAGING_PUBLISHER_PRINCIPAL_ID')
param publisherPrincipalType = readEnvironmentVariable('ZAVA_STAGING_PUBLISHER_PRINCIPAL_TYPE', 'ServicePrincipal')
param network = json(readEnvironmentVariable('ZAVA_STAGING_SEARCH_NETWORK'))
param logAnalyticsWorkspaceId = readEnvironmentVariable('ZAVA_STAGING_LOG_ANALYTICS_ID')
param sku = 'standard'
param replicaCount = 2
param partitionCount = 1
param additionalTags = json(readEnvironmentVariable('ZAVA_STAGING_TAGS', '{}'))
