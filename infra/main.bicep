// TechSolutions - B2B Computer Vision API on Azure.
// Validate:  az bicep build -f infra/main.bicep
// Preview:   az deployment group what-if -g <rg> -f infra/main.bicep -p infra/main.dev.bicepparam
// Prerequisites outside this template: Entra app registration for the API (app role TechSolutions.Submit),
// Entra group for PostgreSQL admins, Key Vault secrets (jira-user, jira-api-token, probe-subscription-key),
// GPU quota for the chosen VM family in the region.
targetScope = 'resourceGroup'

@allowed([ 'dev', 'prod' ])
param environmentName string
param location string = resourceGroup().location
@minLength(3)
@maxLength(8)
@description('Lowercase alphanumeric, globally unique per environment (storage/ACR names).')
param suffix string
param alertEmail string
param publisherEmail string
@description('Application ID URI of the API app registration, e.g. api://techsolutions-api')
param apiAudience string
param entraAdminGroupObjectId string
param entraAdminGroupName string
param jiraBaseUrl string
param imageTag string = 'latest'
param gpuMinNodes int = 1
param gpuMaxNodes int = 4
param gpuSpotMaxNodes int = 16
param postgresSku string = 'Standard_D4ds_v5'
param monthlyBudgetEur int = 4000
param budgetStartDate string = '${utcNow('yyyy-MM')}-01'
@description('Set by the pipeline during a canary release: current production revision of the API.')
param apiStableRevision string = ''
@description('Set by the pipeline during a canary release: % of traffic for the new revision.')
param canaryWeight int = 0
@description('First step of every pipeline run: deploy only network, monitoring and data (incl. ACR) so images can be pushed before the apps reference them.')
param bootstrapOnly bool = false

var prefix = 'techsol-${environmentName}'
var tags = { app: 'techsolutions', env: environmentName, costCenter: 'cv-api', managedBy: 'bicep' }
var entraAuthority = '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'

module network 'modules/network.bicep' = {
  name: 'network'
  params: { location: location, name: '${prefix}-vnet', tags: tags }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { location: location, prefix: prefix, tags: tags, alertEmail: alertEmail }
}

module data 'modules/data.bicep' = {
  name: 'data'
  params: {
    location: location
    prefix: prefix
    suffix: suffix
    tags: tags
    peSubnetId: network.outputs.peSubnetId
    blobZoneId: network.outputs.blobZoneId
    serviceBusZoneId: network.outputs.serviceBusZoneId
    keyVaultZoneId: network.outputs.keyVaultZoneId
    acrZoneId: network.outputs.acrZoneId
  }
}

module postgres 'modules/postgres.bicep' = if (!bootstrapOnly) {
  name: 'postgres'
  params: {
    location: location
    prefix: prefix
    tags: tags
    subnetId: network.outputs.postgresSubnetId
    privateDnsZoneId: network.outputs.postgresZoneId
    skuName: postgresSku
    entraAdminObjectId: entraAdminGroupObjectId
    entraAdminName: entraAdminGroupName
  }
}

module compute 'modules/compute.bicep' = if (!bootstrapOnly) {
  name: 'compute'
  params: {
    location: location
    prefix: prefix
    tags: tags
    vnetName: '${prefix}-vnet'
    acaSubnetId: network.outputs.acaSubnetId
    aksSubnetId: network.outputs.aksSubnetId
    workspaceName: last(split(monitoring.outputs.workspaceId, '/'))
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    serviceBusName: data.outputs.serviceBusName
    serviceBusFqdn: data.outputs.serviceBusFqdn
    storageAccountName: data.outputs.storageAccountName
    keyVaultName: data.outputs.keyVaultName
    acrName: data.outputs.acrName
    acrLoginServer: data.outputs.acrLoginServer
    imageTag: imageTag
    apiAudience: apiAudience
    entraAuthority: entraAuthority
    gpuMinNodes: gpuMinNodes
    gpuMaxNodes: gpuMaxNodes
    gpuSpotMaxNodes: gpuSpotMaxNodes
    appInsightsName: monitoring.outputs.appInsightsName
    apiStableRevision: apiStableRevision
    canaryWeight: canaryWeight
  }
}

module edge 'modules/edge.bicep' = if (!bootstrapOnly) {
  name: 'edge'
  params: {
    location: location
    prefix: prefix
    tags: tags
    publisherEmail: publisherEmail
    apimSubnetId: network.outputs.apimSubnetId
    apiBackendFqdn: compute!.outputs.apiFqdn
    apiAudience: apiAudience
    entraOpenIdConfigUrl: '${entraAuthority}/.well-known/openid-configuration'
    appInsightsId: monitoring.outputs.appInsightsId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
  }
}

module synthetic 'modules/synthetic.bicep' = if (!bootstrapOnly) {
  name: 'synthetic'
  params: {
    location: location
    prefix: prefix
    tags: tags
    environmentId: compute!.outputs.environmentId
    identityId: compute!.outputs.probeIdentityId
    identityClientId: compute!.outputs.probeIdentityClientId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    keyVaultUri: data.outputs.keyVaultUri
    acrLoginServer: data.outputs.acrLoginServer
    imageTag: imageTag
    apiBaseUrl: edge!.outputs.frontDoorEndpoint
    apiAudience: apiAudience
    jiraBaseUrl: jiraBaseUrl
    environmentName: environmentName
  }
}

module alerts 'modules/alerts.bicep' = if (!bootstrapOnly) {
  name: 'alerts'
  params: {
    location: location
    prefix: prefix
    tags: tags
    actionGroupId: monitoring.outputs.actionGroupId
    workspaceId: monitoring.outputs.workspaceId
    appInsightsId: monitoring.outputs.appInsightsId
    serviceBusId: data.outputs.serviceBusId
    postgresId: postgres!.outputs.serverId
    budgetAmount: monthlyBudgetEur
    budgetStartDate: budgetStartDate
    alertEmail: alertEmail
  }
}

output acrName string = data.outputs.acrName
output frontDoorEndpoint string = bootstrapOnly ? '' : edge!.outputs.frontDoorEndpoint
output apimName string = bootstrapOnly ? '' : edge!.outputs.apimName
output apiAppName string = bootstrapOnly ? '' : compute!.outputs.apiAppName
output workerAppNames array = bootstrapOnly ? [] : compute!.outputs.workerAppNames
output syntheticJobName string = bootstrapOnly ? '' : synthetic!.outputs.jobName
output aksName string = bootstrapOnly ? '' : compute!.outputs.aksName
output postgresFqdn string = bootstrapOnly ? '' : postgres!.outputs.fqdn
@description('Assign the API app role TechSolutions.Submit to this principal (Entra, outside ARM).')
output probePrincipalId string = bootstrapOnly ? '' : compute!.outputs.probeIdentityPrincipalId
output inferenceIdentityClientId string = bootstrapOnly ? '' : compute!.outputs.inferenceIdentityClientId
