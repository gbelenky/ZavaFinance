targetScope = 'resourceGroup'

@description('Existing registry used by the Foundry code/image deployment. Deploy to its resource group.')
param registryName string

@description('Project infrastructure managed identity object ID used by the platform image pull.')
@minLength(36)
@maxLength(36)
param imagePullPrincipalId string

@description('Optional CI image publisher object ID. Not the runtime agent or resolver catalogue publisher.')
param imagePublisherPrincipalId string = ''

@description('Match the existing registry permission mode. ABAC registries ignore the legacy AcrPull/AcrPush roles.')
param registryUsesAbac bool = true

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: registryName
}

var pullRoleId = registryUsesAbac ? 'b93aa761-3e63-49ed-ac28-beffa264f7ac' : '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var pushRoleId = registryUsesAbac ? '2a1e307c-b015-4ebd-883e-5b7698a07328' : '8311e382-0749-4cb8-b61a-304f252e45ec'

resource imagePull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, imagePullPrincipalId, pullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', pullRoleId)
    principalId: imagePullPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource imagePush 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(imagePublisherPrincipalId)) {
  name: guid(registry.id, imagePublisherPrincipalId, pushRoleId)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', pushRoleId)
    principalId: imagePublisherPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output imagePullAssignmentId string = imagePull.id
