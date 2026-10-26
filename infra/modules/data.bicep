// Storage (results + images), Service Bus (one queue per stage), Key Vault and ACR. All private, Entra-only auth.
param location string
param prefix string
@minLength(3)
@maxLength(8)
param suffix string
param tags object
param peSubnetId string
param blobZoneId string
param serviceBusZoneId string
param keyVaultZoneId string
param acrZoneId string
@description('Days a result is kept before deletion; to be agreed per customer contract.')
param resultRetentionDays int = 365

var stages = [ 'preprocessing-requests', 'inference-requests', 'postprocessing-requests' ]

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: toLower('${take(replace(prefix, '-', ''), 10)}${suffix}st')
  location: location
  tags: tags
  sku: { name: 'Standard_ZRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // only Entra ID; downloads use user-delegation SAS of short duration
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'AzureServices' }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 14 }
    containerDeleteRetentionPolicy: { enabled: true, days: 14 }
    isVersioningEnabled: false // results are write-once; versioning would only multiply cost
  }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for c in [ 'results', 'images', 'preprocessing', 'exports' ]: {
  parent: blobService
  name: c
  properties: { publicAccess: 'None' }
}]

resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'results-tiering'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: [ 'blockBlob' ], prefixMatch: [ 'results/', 'images/' ] }
            actions: {
              baseBlob: {
                tierToCool: { daysAfterModificationGreaterThan: 30 }
                tierToCold: { daysAfterModificationGreaterThan: 90 }
                delete: { daysAfterModificationGreaterThan: resultRetentionDays }
              }
            }
          }
        }
        {
          name: 'intermediate-artifacts'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: [ 'blockBlob' ], prefixMatch: [ 'preprocessing/' ] }
            actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 7 } } }
          }
        }
      ]
    }
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: '${prefix}-sb'
  location: location
  tags: tags
  sku: { name: 'Premium', tier: 'Premium', capacity: 1 } // Premium: private endpoints, isolation, predictable latency
  properties: { disableLocalAuth: true, publicNetworkAccess: 'Disabled', minimumTlsVersion: '1.2', zoneRedundant: true }
}

resource queues 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = [for q in stages: {
  parent: serviceBus
  name: q
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    maxDeliveryCount: 5
    lockDuration: 'PT2M' // the worker renews the lock while a job runs
    defaultMessageTimeToLive: 'P2D'
    deadLetteringOnMessageExpiration: true
  }
}]

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${take(prefix, 12)}-${suffix}-kv'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'AzureServices' }
  }
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: toLower('${take(replace(prefix, '-', ''), 10)}${suffix}acr')
  location: location
  tags: tags
  sku: { name: 'Premium' } // Premium: private endpoint and zone redundancy
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Disabled'
    networkRuleBypassOptions: 'AzureServices' // lets ACR Tasks (az acr build) push from the pipeline
    zoneRedundancy: 'Enabled'
  }
}

module peBlob 'private-endpoint.bicep' = {
  name: 'pe-blob'
  params: { location: location, name: '${storage.name}-blob-pe', subnetId: peSubnetId, targetId: storage.id, groupId: 'blob', dnsZoneId: blobZoneId, tags: tags }
}
module peSb 'private-endpoint.bicep' = {
  name: 'pe-sb'
  params: { location: location, name: '${serviceBus.name}-pe', subnetId: peSubnetId, targetId: serviceBus.id, groupId: 'namespace', dnsZoneId: serviceBusZoneId, tags: tags }
}
module peKv 'private-endpoint.bicep' = {
  name: 'pe-kv'
  params: { location: location, name: '${keyVault.name}-pe', subnetId: peSubnetId, targetId: keyVault.id, groupId: 'vault', dnsZoneId: keyVaultZoneId, tags: tags }
}
module peAcr 'private-endpoint.bicep' = {
  name: 'pe-acr'
  params: { location: location, name: '${acr.name}-pe', subnetId: peSubnetId, targetId: acr.id, groupId: 'registry', dnsZoneId: acrZoneId, tags: tags }
}

output storageAccountName string = storage.name
output storageAccountId string = storage.id
output serviceBusName string = serviceBus.name
output serviceBusId string = serviceBus.id
output serviceBusFqdn string = '${serviceBus.name}.servicebus.windows.net'
output keyVaultName string = keyVault.name
output keyVaultId string = keyVault.id
output keyVaultUri string = keyVault.properties.vaultUri
output acrName string = acr.name
output acrId string = acr.id
output acrLoginServer string = acr.properties.loginServer
