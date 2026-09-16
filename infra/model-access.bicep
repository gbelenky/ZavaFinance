targetScope = 'resourceGroup'

@description('Existing Azure OpenAI/Foundry account hosting the embedding deployment. Deploy this template to that account resource group.')
param modelAccountName string

@description('Actual hosted-agent runtime identity object ID.')
@minLength(36)
@maxLength(36)
param agentPrincipalId string

@description('Separate catalogue publisher object ID.')
@minLength(36)
@maxLength(36)
param publisherPrincipalId string

@description('Set false when the publisher already has the required effective model-account grant, especially under a different role-assignment GUID. Existing grants are not changed or revoked.')
param assignPublisherAccess bool = true

@allowed([
  'ServicePrincipal'
  'User'
  'Group'
])
param publisherPrincipalType string = 'ServicePrincipal'

resource modelAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: modelAccountName
}

var openAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource runtimeEmbeddingAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(modelAccount.id, agentPrincipalId, openAiUserRoleId)
  scope: modelAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
    principalId: agentPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource publisherEmbeddingAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignPublisherAccess) {
  name: guid(modelAccount.id, publisherPrincipalId, openAiUserRoleId)
  scope: modelAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
    principalId: publisherPrincipalId
    principalType: publisherPrincipalType
  }
}

output runtimeEmbeddingAssignmentId string = runtimeEmbeddingAccess.id
output publisherEmbeddingAssignmentId string = assignPublisherAccess ? publisherEmbeddingAccess!.id : ''
