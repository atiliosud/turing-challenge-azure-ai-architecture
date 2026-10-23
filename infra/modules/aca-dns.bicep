// Private DNS for an internal Container Apps environment (wildcard -> environment static IP).
param zoneName string
param staticIp string
param vnetId string
param tags object

resource zone 'Microsoft.Network/privateDnsZones@2020-06-01' = { name: zoneName, location: 'global', tags: tags }

resource link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: zone
  name: 'cae-link'
  location: 'global'
  properties: { registrationEnabled: false, virtualNetwork: { id: vnetId } }
}

resource wildcard 'Microsoft.Network/privateDnsZones/A@2020-06-01' = {
  parent: zone
  name: '*'
  properties: { ttl: 300, aRecords: [ { ipv4Address: staticIp } ] }
}
