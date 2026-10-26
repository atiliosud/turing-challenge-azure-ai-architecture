// VNet, subnets and private DNS zones. Every data-plane dependency is reached through private endpoints.
param location string
param name string
param tags object

var zones = [
  'privatelink.blob.${environment().suffixes.storage}'
  'privatelink.servicebus.windows.net'
  'privatelink.vaultcore.azure.net'
  'privatelink.azurecr.io'
  'private.postgres.database.azure.com'
]

resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [ '10.20.0.0/16' ] }
    subnets: [
      {
        name: 'snet-aca'
        properties: {
          addressPrefix: '10.20.0.0/23'
          delegations: [ { name: 'aca', properties: { serviceName: 'Microsoft.App/environments' } } ]
        }
      }
      { name: 'snet-aks', properties: { addressPrefix: '10.20.4.0/22' } }
      { name: 'snet-pe', properties: { addressPrefix: '10.20.8.0/24' } }
      {
        name: 'snet-postgres'
        properties: {
          addressPrefix: '10.20.9.0/24'
          delegations: [ { name: 'pg', properties: { serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers' } } ]
        }
      }
      {
        name: 'snet-apim'
        properties: {
          addressPrefix: '10.20.10.0/24'
          delegations: [ { name: 'apim', properties: { serviceName: 'Microsoft.Web/serverFarms' } } ]
        }
      }
    ]
  }
}

resource dnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in zones: {
  name: zone
  location: 'global'
  tags: tags
}]

resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in zones: {
  parent: dnsZones[i]
  name: '${name}-link'
  location: 'global'
  properties: { registrationEnabled: false, virtualNetwork: { id: vnet.id } }
}]

output vnetId string = vnet.id
output acaSubnetId string = vnet.properties.subnets[0].id
output aksSubnetId string = vnet.properties.subnets[1].id
output peSubnetId string = vnet.properties.subnets[2].id
output postgresSubnetId string = vnet.properties.subnets[3].id
output apimSubnetId string = vnet.properties.subnets[4].id
output blobZoneId string = dnsZones[0].id
output serviceBusZoneId string = dnsZones[1].id
output keyVaultZoneId string = dnsZones[2].id
output acrZoneId string = dnsZones[3].id
output postgresZoneId string = dnsZones[4].id
