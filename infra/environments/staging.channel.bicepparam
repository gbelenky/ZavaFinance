using '../main.bicep'

param environmentName = 'staging'
param appName = 'zavafinstg'
param location = readEnvironmentVariable('ZAVA_STAGING_LOCATION')
param botAppId = readEnvironmentVariable('ZAVA_STAGING_BOT_APP_ID')
param botTenantId = readEnvironmentVariable('ZAVA_STAGING_TENANT_ID')
param hostedAgentResponsesEndpoint = readEnvironmentVariable('ZAVA_STAGING_RESPONSES_ENDPOINT')
param hostedAgentName = 'zavafinance-staging'
param sessionKeySalt = readEnvironmentVariable('ZAVA_STAGING_SESSION_KEY_SALT')
param botClientSecret = readEnvironmentVariable('ZAVA_STAGING_BOT_CLIENT_SECRET')
param taskHubName = 'zavafinancestaging'
param appServiceSku = 'P1v3'
param appServiceInstanceCount = 1
param storageSku = 'Standard_ZRS'
param logRetentionInDays = 30
param schedulerIpAllowlist = json(readEnvironmentVariable('ZAVA_STAGING_DTS_IP_ALLOWLIST'))
param vnetAddressPrefix = '10.20.0.0/16'
param appSubnetAddressPrefix = '10.20.1.0/24'
param privateEndpointSubnetAddressPrefix = '10.20.2.0/24'
param additionalTags = json(readEnvironmentVariable('ZAVA_STAGING_TAGS', '{}'))
