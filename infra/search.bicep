targetScope = 'resourceGroup'

@description('Dedicated Search service for this environment; lowercase, globally unique.')
@minLength(2)
@maxLength(60)
param searchServiceName string

@description('Approved Azure region with semantic ranker and the selected SKU available.')
param location string = resourceGroup().location

@allowed([
  'dev'
  'staging'
  'prod'
])
param environmentName string

@description('Object ID of the identity actually used inside the hosted agent. Not its client ID or the deployment principal.')
@minLength(36)
@maxLength(36)
param agentPrincipalId string

@description('Object ID of a separate catalogue publisher. Never use the runtime identity here.')
@minLength(36)
@maxLength(36)
param publisherPrincipalId string

@allowed([
  'ServicePrincipal'
  'User'
  'Group'
])
param publisherPrincipalType string = 'ServicePrincipal'

@description('Schema creation requires Search Service Contributor. Disable for a document-only publisher after index administration is separated.')
param grantPublisherSchemaManagement bool = true

@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param sku string = 'standard'

@minValue(1)
@maxValue(12)
param replicaCount int = 1

@allowed([
  1
  2
  3
  4
  6
  12
])
param partitionCount int = 1

@discriminator('mode')
type networkConfiguration = {
  mode: 'Private'
  @description('Existing private endpoint subnet; separate from the delegated hosted-agent subnet.')
  @minLength(1)
  privateEndpointSubnetId: string
  @description('Existing privatelink.search.windows.net zone, linked/forwarded to both runtime and publisher networks.')
  @minLength(1)
  searchPrivateDnsZoneId: string
} | {
  mode: 'Public'
  @description('Approved IPv4/CIDR sources. Empty explicitly means public reachability from all IPs, still Entra authenticated.')
  allowedIpRanges: string[]
}

@description('Explicit network decision. Private is usable only when hosted-agent egress and publisher DNS/routing are ready.')
param network networkConfiguration

@description('Existing Log Analytics workspace ID. Empty omits Search diagnostics; approval is required for that exception.')
param logAnalyticsWorkspaceId string = ''

@description('Query logs can contain finance terminology. Enable only with approved access controls and retention.')
param enableQueryLogs bool = false

@description('Additional required customer tags; mandatory solution/environment tags are preserved.')
param additionalTags object = {}

var tags = union({
  purpose: 'demo'
  owner: 'gbelenky'
}, additionalTags, {
  solution: 'ZavaFinance'
  environment: environmentName
})
var readerRoleId = '1407120a-92aa-4202-b7e9-c0e197c71c8f'
var dataContributorRoleId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var serviceContributorRoleId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
var searchId = resourceId('Microsoft.Search/searchServices', searchServiceName)
var readerAssignmentName = guid(searchId, agentPrincipalId, readerRoleId)
var publisherDataAssignmentName = guid(searchId, publisherPrincipalId, dataContributorRoleId)
var publisherSchemaAssignmentName = guid(searchId, publisherPrincipalId, serviceContributorRoleId)

module search 'br/public:avm/res/search/search-service:0.13.0' = {
  name: 'search-${environmentName}'
  params: {
    name: searchServiceName
    location: location
    enableTelemetry: false
    tags: tags
    sku: sku
    replicaCount: replicaCount
    partitionCount: partitionCount
    semanticSearch: 'standard'
    disableLocalAuth: true
    publicNetworkAccess: network.mode == 'Private' ? 'Disabled' : 'Enabled'
    networkRuleSet: {
      bypass: 'None'
      ipRules: network.mode == 'Public' ? map(network.allowedIpRanges, ip => { value: ip }) : []
    }
    privateEndpoints: network.mode == 'Private' ? [
      {
        name: 'pe-${searchServiceName}'
        subnetResourceId: network.privateEndpointSubnetId
        privateDnsZoneGroup: {
          privateDnsZoneGroupConfigs: [
            {
              privateDnsZoneResourceId: network.searchPrivateDnsZoneId
            }
          ]
        }
      }
    ] : []
    diagnosticSettings: empty(logAnalyticsWorkspaceId) ? [] : [
      {
        name: 'resolver-search'
        workspaceResourceId: logAnalyticsWorkspaceId
        metricCategories: [
          {
            category: 'AllMetrics'
          }
        ]
        logCategoriesAndGroups: enableQueryLogs ? [
          {
            category: 'OperationLogs'
          }
        ] : []
      }
    ]
    roleAssignments: concat([
      {
        name: readerAssignmentName
        principalId: agentPrincipalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: readerRoleId
      }
      {
        name: publisherDataAssignmentName
        principalId: publisherPrincipalId
        principalType: publisherPrincipalType
        roleDefinitionIdOrName: dataContributorRoleId
      }
    ], grantPublisherSchemaManagement ? [
      {
        name: publisherSchemaAssignmentName
        principalId: publisherPrincipalId
        principalType: publisherPrincipalType
        roleDefinitionIdOrName: serviceContributorRoleId
      }
    ] : [])
  }
}

output searchServiceId string = search.outputs.resourceId
output searchEndpoint string = 'https://${searchServiceName}.search.windows.net'
output runtimeReaderAssignmentId string = '${searchId}/providers/Microsoft.Authorization/roleAssignments/${readerAssignmentName}'
output publisherDataAssignmentId string = '${searchId}/providers/Microsoft.Authorization/roleAssignments/${publisherDataAssignmentName}'
output publisherSchemaAssignmentId string = grantPublisherSchemaManagement ? '${searchId}/providers/Microsoft.Authorization/roleAssignments/${publisherSchemaAssignmentName}' : ''
