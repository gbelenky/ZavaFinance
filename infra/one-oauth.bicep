targetScope = 'resourceGroup'

@description('New One bot created by the native Activity deployment. Never pass the original bot name.')
param botName string

@description('Existing confidential application used for delegated finance access, not the One bot identity.')
param userAuthClientId string

@secure()
param userAuthClientSecret string

param tenantId string
param tokenExchangeResource string
param delegatedScopes string

resource bot 'Microsoft.BotService/botServices@2022-09-15' existing = {
  name: botName
}

resource oauth 'Microsoft.BotService/botServices/connections@2022-09-15' = {
  parent: bot
  name: 'mcs'
  location: 'global'
  properties: {
    clientId: userAuthClientId
    clientSecret: userAuthClientSecret
    serviceProviderId: '30dd229c-58e3-4a48-bdfd-91ec48eb906c'
    scopes: delegatedScopes
    parameters: [
      { key: 'tenantId', value: tenantId }
      { key: 'tokenExchangeUrl', value: tokenExchangeResource }
    ]
  }
}

output connectionId string = oauth.id
