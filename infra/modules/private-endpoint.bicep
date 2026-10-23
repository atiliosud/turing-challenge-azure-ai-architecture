param location string
param name string
param subnetId string
param targetId string
param groupId string
param dnsZoneId string
param tags object

resource pe 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: { id: subnetId }
    privateLinkServiceConnections: [ { name: name, properties: { privateLinkServiceId: targetId, groupIds: [ groupId ] } } ]
  }
}

resource dns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: pe
  name: 'default'
  properties: { privateDnsZoneConfigs: [ { name: groupId, properties: { privateDnsZoneId: dnsZoneId } } ] }
}
