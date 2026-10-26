// Log Analytics + workspace-based Application Insights + Action Group.
// Retention 90 days: monthly statistics need a full month plus margin; the yearly view (March-May peak vs.
// previous year) comes from the usage ledger in PostgreSQL and a monthly export to Blob, not from raw logs.
param location string
param prefix string
param tags object
param alertEmail string
param retentionInDays int = 90

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-log'
  location: location
  tags: tags
  properties: {
    retentionInDays: retentionInDays
    sku: { name: 'PerGB2018' }
    workspaceCapping: { dailyQuotaGb: 20 } // cost guard; an alert fires before the cap is reached
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appi'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    IngestionMode: 'LogAnalytics'
    DisableLocalAuth: true
  }
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${prefix}-ag-oncall'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'ts-oncall'
    enabled: true
    emailReceivers: [ { name: 'oncall', emailAddress: alertEmail, useCommonAlertSchema: true } ]
  }
}

output workspaceId string = logs.id
output workspaceCustomerId string = logs.properties.customerId
output appInsightsId string = appInsights.id
output appInsightsName string = appInsights.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output actionGroupId string = actionGroup.id
