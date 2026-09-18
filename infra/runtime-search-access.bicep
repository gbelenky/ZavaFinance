targetScope = 'resourceGroup'

@description('Existing dedicated resolver Search service. This template does not update the service or its indexes.')
param searchServiceName string

@description('Verified selected DotNet or Python runtime principal object ID, not the project infrastructure identity.')
@minLength(36)
@maxLength(36)
param agentPrincipalId string

resource search 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: searchServiceName
}

var readerRoleId = '1407120a-92aa-4202-b7e9-c0e197c71c8f'

resource runtimeReadAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, agentPrincipalId, readerRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: agentPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output assignmentId string = runtimeReadAccess.id
