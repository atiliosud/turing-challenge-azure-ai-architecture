// Azure Monitor alerts: degradation of any stage pages the on-call Action Group.
// Thresholds are starting points; they are tuned after the load test and the first peak season.
param location string
param prefix string
param tags object
param actionGroupId string
param workspaceId string
param appInsightsId string
param serviceBusId string
param postgresId string
param budgetAmount int
param budgetStartDate string
param alertEmail string

var ag = [ { actionGroupId: actionGroupId } ]

resource dlq 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-sb-deadletter'
  location: 'global'
  tags: tags
  properties: {
    description: 'Messages in any dead-letter queue: a job failed all retries and needs reconciliation.'
    severity: 2
    enabled: true
    scopes: [ serviceBusId ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [ {
        criterionType: 'StaticThresholdCriterion', name: 'dlq', metricName: 'DeadletteredMessages', metricNamespace: 'Microsoft.ServiceBus/namespaces'
        timeAggregation: 'Maximum', operator: 'GreaterThan', threshold: 0
        dimensions: [ { name: 'EntityName', operator: 'Include', values: [ '*' ] } ]
      } ]
    }
    actions: ag
  }
}

resource backlog 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-sb-inference-backlog'
  location: 'global'
  tags: tags
  properties: {
    description: 'Inference backlog keeps growing: GPU capacity or quota is not keeping up with the agreed completion time.'
    severity: 2
    enabled: true
    scopes: [ serviceBusId ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [ {
        criterionType: 'StaticThresholdCriterion', name: 'backlog', metricName: 'ActiveMessages', metricNamespace: 'Microsoft.ServiceBus/namespaces'
        timeAggregation: 'Average', operator: 'GreaterThan', threshold: 5000
        dimensions: [ { name: 'EntityName', operator: 'Include', values: [ 'inference-requests' ] } ]
      } ]
    }
    actions: ag
  }
}

resource pgCpu 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-pg-cpu'
  location: 'global'
  tags: tags
  properties: {
    description: 'PostGIS CPU sustained above 80 %: slow parcel lookups will slow every job.'
    severity: 3
    enabled: true
    scopes: [ postgresId ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [ { criterionType: 'StaticThresholdCriterion', name: 'cpu', metricName: 'cpu_percent', metricNamespace: 'Microsoft.DBforPostgreSQL/flexibleServers', timeAggregation: 'Average', operator: 'GreaterThan', threshold: 80 } ]
    }
    actions: ag
  }
}

var logAlerts = [
  {
    name: 'synthetic-e2e-failed'
    severity: 1
    description: 'Synthetic E2E probe failed (wrong result, no response or timeout). The probe job also opens/updates the Jira issue.'
    scope: workspaceId
    query: 'ContainerAppConsoleLogs_CL | where ContainerJobName_s endswith "-synthetic" | where Log_s has "SyntheticProbe result=Fail"'
    threshold: 1
    window: 'PT15M'
  }
  {
    name: 'synthetic-e2e-missing'
    severity: 2
    description: 'No synthetic probe result in 20 minutes: the monitor itself is down.'
    scope: workspaceId
    query: 'ContainerAppConsoleLogs_CL | where ContainerJobName_s endswith "-synthetic" | where Log_s has "SyntheticProbe result=" | summarize n = count() | where n == 0'
    threshold: 0
    window: 'PT20M'
  }
  {
    name: 'api-5xx-rate'
    severity: 2
    description: 'API 5xx rate above 2 % with at least 50 requests in 5 minutes.'
    scope: appInsightsId
    query: 'requests | summarize total = count(), failed = countif(toint(resultCode) >= 500) | where total >= 50 and todouble(failed) / total > 0.02'
    threshold: 0
    window: 'PT5M'
  }
  {
    name: 'admission-latency-p95'
    severity: 3
    description: 'POST /v1/evaluations p95 above 500 ms (admission must stay fast; heavy work is async).'
    scope: appInsightsId
    query: 'requests | where name has "POST" | summarize p95 = percentile(duration, 95), n = count() | where n >= 50 and p95 > 500'
    threshold: 0
    window: 'PT10M'
  }
]

resource scheduled 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for a in logAlerts: {
  name: '${prefix}-${a.name}'
  location: location
  tags: tags
  properties: {
    description: a.description
    severity: a.severity
    enabled: true
    scopes: [ a.scope ]
    evaluationFrequency: 'PT5M'
    windowSize: a.window
    criteria: {
      allOf: [ {
        query: a.query
        timeAggregation: 'Count'
        operator: 'GreaterThan'
        threshold: a.threshold
        failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
      } ]
    }
    autoMitigate: true
    actions: { actionGroups: [ actionGroupId ] }
  }
}]

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: '${prefix}-monthly-budget'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual70: { enabled: true, operator: 'GreaterThan', threshold: 70, contactGroups: [ actionGroupId ], contactEmails: [ alertEmail ], thresholdType: 'Actual' }
      actual90: { enabled: true, operator: 'GreaterThan', threshold: 90, contactGroups: [ actionGroupId ], contactEmails: [ alertEmail ], thresholdType: 'Actual' }
      forecast100: { enabled: true, operator: 'GreaterThan', threshold: 100, contactGroups: [ actionGroupId ], contactEmails: [ alertEmail ], thresholdType: 'Forecasted' }
    }
  }
}
