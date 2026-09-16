using '../main.bicep'

param environmentName = 'prod'
param appName = 'zavafinprod'
param location = readEnvironmentVariable('ZAVA_PROD_LOCATION')
param botAppId = readEnvironmentVariable('ZAVA_PROD_BOT_APP_ID')
param botTenantId = readEnvironmentVariable('ZAVA_PROD_TENANT_ID')
param hostedAgentResponsesEndpoint = readEnvironmentVariable('ZAVA_PROD_RESPONSES_ENDPOINT')
param hostedAgentName = 'zavafinance-prod'
param sessionKeySalt = readEnvironmentVariable('ZAVA_PROD_SESSION_KEY_SALT')
param botClientSecret = readEnvironmentVariable('ZAVA_PROD_BOT_CLIENT_SECRET')
param taskHubName = 'zavafinanceprod'
param appServiceSku = 'P1v3'
param appServiceInstanceCount = 2
param storageSku = 'Standard_ZRS'
param logRetentionInDays = 90
param schedulerIpAllowlist = json(readEnvironmentVariable('ZAVA_PROD_DTS_IP_ALLOWLIST'))
param vnetAddressPrefix = '10.30.0.0/16'
param appSubnetAddressPrefix = '10.30.1.0/24'
param privateEndpointSubnetAddressPrefix = '10.30.2.0/24'
param additionalTags = json(readEnvironmentVariable('ZAVA_PROD_TAGS', '{}'))
