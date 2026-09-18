using './oauth.bicep'

param botName = readEnvironmentVariable('ONE_BOT_NAME')
param userAuthClientId = readEnvironmentVariable('OBO_CLIENT_ID')
param userAuthClientSecret = readEnvironmentVariable('OBO_CLIENT_SECRET')
param tenantId = readEnvironmentVariable('OBO_TENANT_ID')
param tokenExchangeResource = readEnvironmentVariable('ONE_TOKEN_EXCHANGE_RESOURCE')
param delegatedScopes = readEnvironmentVariable('ONE_DELEGATED_SCOPES')
