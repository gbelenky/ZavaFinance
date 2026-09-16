using '../main.bicep'

param environmentName = 'dev'
param appName = readEnvironmentVariable('ZAVA_DEV_APP_NAME', 'zavafindev')
param location = readEnvironmentVariable('ZAVA_DEV_LOCATION')
param botAppId = readEnvironmentVariable('ZAVA_DEV_BOT_APP_ID')
param botTenantId = readEnvironmentVariable('ZAVA_DEV_TENANT_ID')
param hostedAgentResponsesEndpoint = readEnvironmentVariable('ZAVA_DEV_RESPONSES_ENDPOINT')
param hostedAgentName = 'zavafinance-dev'
param sessionKeySalt = readEnvironmentVariable('ZAVA_DEV_SESSION_KEY_SALT')
param botClientSecret = readEnvironmentVariable('ZAVA_DEV_BOT_CLIENT_SECRET')
param taskHubName = 'zavafinancedev'
param appServiceSku = 'P1v3'
param appServiceInstanceCount = 1
param storageSku = 'Standard_LRS'
param logRetentionInDays = 30
param schedulerIpAllowlist = json(readEnvironmentVariable('ZAVA_DEV_DTS_IP_ALLOWLIST'))
param vnetAddressPrefix = '10.10.0.0/16'
param appSubnetAddressPrefix = '10.10.1.0/24'
param privateEndpointSubnetAddressPrefix = '10.10.2.0/24'
param additionalTags = json(readEnvironmentVariable('ZAVA_DEV_TAGS', '{}'))
