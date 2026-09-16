using '../search.bicep'

param environmentName = 'prod'
param searchServiceName = readEnvironmentVariable('ZAVA_PROD_SEARCH_NAME')
param location = readEnvironmentVariable('ZAVA_PROD_SEARCH_LOCATION')
param agentPrincipalId = readEnvironmentVariable('ZAVA_PROD_AGENT_PRINCIPAL_ID')
param publisherPrincipalId = readEnvironmentVariable('ZAVA_PROD_PUBLISHER_PRINCIPAL_ID')
param publisherPrincipalType = readEnvironmentVariable('ZAVA_PROD_PUBLISHER_PRINCIPAL_TYPE', 'ServicePrincipal')
param network = json(readEnvironmentVariable('ZAVA_PROD_SEARCH_NETWORK'))
param logAnalyticsWorkspaceId = readEnvironmentVariable('ZAVA_PROD_LOG_ANALYTICS_ID')
param sku = 'standard'
param replicaCount = 3
param partitionCount = 1
param additionalTags = json(readEnvironmentVariable('ZAVA_PROD_TAGS', '{}'))
