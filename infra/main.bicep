// Baseline only. Requires networking, private DNS, Entra configuration, alerts and GPU integration before production.
// Deploy with: az deployment group what-if -g <rg> -f infra/main.bicep -p location=westeurope
param location string = resourceGroup().location
param prefix string = 'techsol'
@description('Globally unique suffix; set explicitly per environment.')
@minLength(3)
@maxLength(12)
param suffix string

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: '${prefix}${suffix}st'
  location: location
  sku: { name: 'Standard_ZRS' }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Disabled'
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: '${prefix}-${suffix}-sb'
  location: location
  sku: { name: 'Premium', tier: 'Premium', capacity: 1 }
  properties: { disableLocalAuth: true, publicNetworkAccess: 'Disabled' }
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'inference-requests'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    defaultMessageTimeToLive: 'P7D'
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-${suffix}-logs'
  location: location
  properties: { retentionInDays: 30, sku: { name: 'PerGB2018' } }
}

output serviceBusNamespace string = serviceBus.name
output serviceBusFullyQualifiedNamespace string = '${serviceBus.name}.servicebus.windows.net'
output storageAccountName string = storage.name
output logAnalyticsWorkspaceId string = logs.id
