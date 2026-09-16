targetScope = 'resourceGroup'

@description('Existing Foundry account. Deploy to its resource group; no account/project resources are recreated.')
param foundryAccountName string

param foundryProjectName string

@description('Channel user-assigned managed identity object ID, from main.bicep identityPrincipalId.')
@minLength(36)
@maxLength(36)
param channelPrincipalId string

@description('Current client creates project-level conversations and uses the project Responses API. Set false only after validating a dedicated-agent endpoint client with Foundry Agent Consumer.')
param channelUsesProjectApi bool = true

@description('Optional project infrastructure managed identity object ID, not the dedicated agent runtime identity. Empty leaves existing platform assignments unchanged.')
param projectPrincipalId string = ''

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  parent: account
  name: foundryProjectName
}

var foundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var consumerRoleId = 'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'
var channelRoleId = channelUsesProjectApi ? foundryUserRoleId : consumerRoleId

resource channelInvocation 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(project.id, channelPrincipalId, channelRoleId)
  scope: project
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', channelRoleId)
    principalId: channelPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource projectInfrastructureAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(projectPrincipalId)) {
  name: guid(account.id, projectPrincipalId, foundryUserRoleId)
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
    principalId: projectPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output channelInvocationAssignmentId string = channelInvocation.id
output channelInvocationRoleId string = channelRoleId
