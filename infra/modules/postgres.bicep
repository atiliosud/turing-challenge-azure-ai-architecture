// PostgreSQL Flexible Server + PostGIS for the 150M-parcel cadastre, job state, outbox and usage ledger.
// VNet-integrated (no public endpoint), zone-redundant HA, Entra ID authentication only.
param location string
param prefix string
param tags object
param subnetId string
param privateDnsZoneId string
param skuName string = 'Standard_D4ds_v5'
param storageSizeGB int = 512
param entraAdminObjectId string
param entraAdminName string

resource pg 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${prefix}-pg'
  location: location
  tags: tags
  sku: { name: skuName, tier: 'GeneralPurpose' }
  properties: {
    version: '16'
    storage: { storageSizeGB: storageSizeGB, autoGrow: 'Enabled' }
    backup: { backupRetentionDays: 14, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'ZoneRedundant' }
    network: { delegatedSubnetResourceId: subnetId, privateDnsZoneArmResourceId: privateDnsZoneId, publicNetworkAccess: 'Disabled' }
    authConfig: { activeDirectoryAuth: 'Enabled', passwordAuth: 'Disabled', tenantId: subscription().tenantId }
  }
}

resource admin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  parent: pg
  name: entraAdminObjectId
  properties: { principalType: 'Group', principalName: entraAdminName, tenantId: subscription().tenantId }
}

// PostGIS must be allow-listed before CREATE EXTENSION postgis; is run by the migration job.
resource extensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: pg
  name: 'azure.extensions'
  properties: { value: 'POSTGIS,PG_STAT_STATEMENTS', source: 'user-override' }
  dependsOn: [ admin ]
}

output serverId string = pg.id
output fqdn string = pg.properties.fullyQualifiedDomainName
