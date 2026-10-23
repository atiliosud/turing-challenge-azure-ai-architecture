// CPU stages on Azure Container Apps (PaaS, KEDA built in, scale by queue depth).
// GPU inference on a small AKS cluster with a T4 node pool (KEDA add-on + cluster autoscaler, Spot pool for peaks).
param location string
param prefix string
param tags object
param vnetName string
param acaSubnetId string
param aksSubnetId string
param workspaceName string
param appInsightsConnectionString string
param serviceBusName string
param serviceBusFqdn string
param storageAccountName string
param keyVaultName string
param acrName string
param acrLoginServer string
param imageTag string
param apiAudience string
param entraAuthority string
param gpuVmSize string = 'Standard_NC4as_T4_v3'
@description('On-demand GPU nodes kept warm. 1 outside March-May; raised before the peak season.')
param gpuMinNodes int = 1
param gpuMaxNodes int = 4
param gpuSpotMaxNodes int = 16

// Built-in role definition IDs
var roles = {
  sbSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  sbReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  blobContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  kvSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  acrPull: '7f951dca-6bd8-4ba1-aec6-8f8bbf94fbd7'
  networkContributor: '4d97b98b-1d4f-4787-a291-c67834d212e7'
}

resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' existing = { name: vnetName }
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = { name: workspaceName }
resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: serviceBusName }
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageAccountName }
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = { name: acrName }

// ---------- Identities ----------
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = { name: '${prefix}-id-apps', location: location, tags: tags }
resource inferenceIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = { name: '${prefix}-id-inference', location: location, tags: tags }
resource aksIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = { name: '${prefix}-id-aks', location: location, tags: tags }

var dataPlane = [
  { identity: 'apps', role: roles.sbSender, scope: 'sb' }
  { identity: 'apps', role: roles.sbReceiver, scope: 'sb' }
  { identity: 'apps', role: roles.blobContributor, scope: 'st' }
  { identity: 'inference', role: roles.sbSender, scope: 'sb' }
  { identity: 'inference', role: roles.sbReceiver, scope: 'sb' }
  { identity: 'inference', role: roles.blobContributor, scope: 'st' }
]

resource sbRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in filter(dataPlane, x => x.scope == 'sb'): {
  name: guid(serviceBus.id, r.role, r.identity, prefix)
  scope: serviceBus
  properties: {
    principalId: r.identity == 'apps' ? appIdentity.properties.principalId : inferenceIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r.role)
    principalType: 'ServicePrincipal'
  }
}]

resource stRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in filter(dataPlane, x => x.scope == 'st'): {
  name: guid(storage.id, r.role, r.identity, prefix)
  scope: storage
  properties: {
    principalId: r.identity == 'apps' ? appIdentity.properties.principalId : inferenceIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r.role)
    principalType: 'ServicePrincipal'
  }
}]

resource kvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, roles.kvSecretsUser, appIdentity.id)
  scope: keyVault
  properties: { principalId: appIdentity.properties.principalId, roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.kvSecretsUser), principalType: 'ServicePrincipal' }
}

resource acrRoleApps 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, roles.acrPull, appIdentity.id)
  scope: acr
  properties: { principalId: appIdentity.properties.principalId, roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull), principalType: 'ServicePrincipal' }
}

resource aksNetworkRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vnet.id, roles.networkContributor, aksIdentity.id)
  scope: vnet
  properties: { principalId: aksIdentity.properties.principalId, roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.networkContributor), principalType: 'ServicePrincipal' }
}

// ---------- Container Apps ----------
resource env 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${prefix}-cae'
  location: location
  tags: tags
  properties: {
    vnetConfiguration: { infrastructureSubnetId: acaSubnetId, internal: true } // reachable only from the VNet (APIM)
    workloadProfiles: [ { name: 'Consumption', workloadProfileType: 'Consumption' } ]
    zoneRedundant: true
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
  }
}

// Internal environment needs its own private DNS zone so APIM can resolve the app FQDNs.
module envDns 'aca-dns.bicep' = {
  name: 'aca-dns'
  params: { zoneName: env.properties.defaultDomain, staticIp: env.properties.staticIp, vnetId: vnet.id, tags: tags }
}

var commonEnv = [
  { name: 'AZURE_CLIENT_ID', value: appIdentity.properties.clientId }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
  { name: 'ServiceBus__Namespace', value: serviceBusFqdn }
]
var registries = [ { server: acrLoginServer, identity: appIdentity.id } ]
var identity = { type: 'UserAssigned', userAssignedIdentities: { '${appIdentity.id}': {} } }

resource api 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${prefix}-api'
  location: location
  tags: tags
  identity: identity
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple' // canary: new revision gets a traffic slice before 100 %
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${acrLoginServer}/api:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(commonEnv, [
            { name: 'ServiceBus__Queue', value: 'preprocessing-requests' }
            { name: 'Auth__Authority', value: entraAuthority }
            { name: 'Auth__Audience', value: apiAudience }
          ])
          probes: [
            { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 } }
            { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 } }
          ]
        }
      ]
      // Admission is cheap (validate + enqueue); 2 replicas for availability, scale on concurrent requests.
      scale: { minReplicas: 2, maxReplicas: 10, rules: [ { name: 'http', http: { metadata: { concurrentRequests: '50' } } } ] }
    }
  }
  dependsOn: [ acrRoleApps ]
}

var workers = [
  { name: 'preprocessing', queue: 'preprocessing-requests', min: 1, max: 30, cpu: '0.5', memory: '1Gi' } // I/O bound (Google, PostGIS); max protects PostGIS and Google quota
  { name: 'postprocessing', queue: 'postprocessing-requests', min: 1, max: 20, cpu: '1.0', memory: '2Gi' } // CPU bound (masks -> polygons)
]

resource workerApps 'Microsoft.App/containerApps@2025-01-01' = [for w in workers: {
  name: '${prefix}-${w.name}'
  location: location
  tags: tags
  identity: identity
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: { activeRevisionsMode: 'Single', registries: registries }
    template: {
      containers: [
        {
          name: w.name
          image: '${acrLoginServer}/worker:${imageTag}'
          resources: { cpu: json(w.cpu), memory: w.memory }
          env: concat(commonEnv, [ { name: 'ServiceBus__Queue', value: w.queue }, { name: 'Worker__Stage', value: w.name } ])
        }
      ]
      scale: {
        minReplicas: w.min
        maxReplicas: w.max
        rules: [
          {
            name: 'queue-depth'
            custom: {
              type: 'azure-servicebus'
              metadata: { queueName: w.queue, namespace: serviceBusName, messageCount: '10' }
              identity: appIdentity.id
            }
          }
        ]
      }
    }
  }
  dependsOn: [ acrRoleApps, sbRoles ]
}]

// ---------- AKS for GPU inference ----------
resource aks 'Microsoft.ContainerService/managedClusters@2024-05-01' = {
  name: '${prefix}-aks'
  location: location
  tags: tags
  sku: { name: 'Base', tier: 'Standard' } // Standard tier: financially backed API server SLA
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${aksIdentity.id}': {} } }
  properties: {
    dnsPrefix: '${prefix}-aks'
    disableLocalAccounts: true
    aadProfile: { managed: true, enableAzureRBAC: true }
    apiServerAccessProfile: { enablePrivateCluster: true }
    oidcIssuerProfile: { enabled: true }
    securityProfile: { workloadIdentity: { enabled: true } }
    workloadAutoScalerProfile: { keda: { enabled: true } } // scale inference pods on inference-requests depth
    autoUpgradeProfile: { upgradeChannel: 'patch', nodeOSUpgradeChannel: 'NodeImage' }
    networkProfile: { networkPlugin: 'azure', networkPluginMode: 'overlay', podCidr: '192.168.0.0/16', serviceCidr: '172.16.0.0/16', dnsServiceIP: '172.16.0.10' }
    addonProfiles: { omsagent: { enabled: true, config: { logAnalyticsWorkspaceResourceID: logs.id } } }
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: 2
        vmSize: 'Standard_D2ds_v5'
        osSKU: 'AzureLinux'
        vnetSubnetID: aksSubnetId
        availabilityZones: [ '1', '2', '3' ]
        nodeTaints: [ 'CriticalAddonsOnly=true:NoSchedule' ]
      }
      {
        name: 'gpu'
        mode: 'User'
        vmSize: gpuVmSize
        osSKU: 'Ubuntu'
        vnetSubnetID: aksSubnetId
        enableAutoScaling: true
        count: gpuMinNodes
        minCount: gpuMinNodes
        maxCount: gpuMaxNodes
        nodeTaints: [ 'sku=gpu:NoSchedule' ]
        nodeLabels: { workload: 'inference', capacity: 'on-demand' }
      }
      {
        name: 'gpuspot'
        mode: 'User'
        vmSize: gpuVmSize
        osSKU: 'Ubuntu'
        vnetSubnetID: aksSubnetId
        enableAutoScaling: true
        count: 0
        minCount: 0
        maxCount: gpuSpotMaxNodes
        scaleSetPriority: 'Spot' // peak burst capacity; evictions only delay jobs (message returns to the queue)
        scaleSetEvictionPolicy: 'Delete'
        spotMaxPrice: -1
        nodeTaints: [ 'sku=gpu:NoSchedule', 'kubernetes.azure.com/scalesetpriority=spot:NoSchedule' ]
        nodeLabels: { workload: 'inference', capacity: 'spot' }
      }
    ]
  }
  dependsOn: [ aksNetworkRole ]
}

resource acrRoleKubelet 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, roles.acrPull, aks.id)
  scope: acr
  properties: { principalId: aks.properties.identityProfile.kubeletidentity.objectId, roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull), principalType: 'ServicePrincipal' }
}

// Inference pods authenticate to Service Bus / Blob via workload identity (no secrets in the cluster).
resource inferenceFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: inferenceIdentity
  name: 'aks-inference-worker'
  properties: {
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:inference:inference-worker'
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

output apiFqdn string = api.properties.configuration.ingress.fqdn
output apiAppName string = api.name
output workerAppNames array = [for (w, i) in workers: workerApps[i].name]
output environmentId string = env.id
output appIdentityId string = appIdentity.id
output appIdentityClientId string = appIdentity.properties.clientId
output aksName string = aks.name
output appIdentityPrincipalId string = appIdentity.properties.principalId
output inferenceIdentityClientId string = inferenceIdentity.properties.clientId
