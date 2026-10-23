// Synthetic E2E probe as a Container Apps Job on cron. It enters through Front Door like a real customer,
// so it validates WAF, APIM, auth, queues, GPU and post-processing in one go.
// Exit code 1 = failed execution -> Azure Monitor alert; the job itself opens/updates the Jira issue.
param location string
param prefix string
param tags object
param environmentId string
param identityId string
param identityClientId string
param keyVaultUri string
param acrLoginServer string
param imageTag string
param apiBaseUrl string
param apiAudience string
param jiraBaseUrl string
param jiraProjectKey string = 'OPS'
param environmentName string
param cronExpression string = '*/5 * * * *'

resource syntheticJob 'Microsoft.App/jobs@2025-01-01' = {
  name: '${prefix}-synthetic'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      scheduleTriggerConfig: { cronExpression: cronExpression, parallelism: 1, replicaCompletionCount: 1 }
      replicaTimeout: 300
      replicaRetryLimit: 0
      registries: [ { server: acrLoginServer, identity: identityId } ]
      secrets: [
        { name: 'jira-token', keyVaultUrl: '${keyVaultUri}secrets/jira-api-token', identity: identityId }
        { name: 'jira-user', keyVaultUrl: '${keyVaultUri}secrets/jira-user', identity: identityId }
        { name: 'probe-subscription-key', keyVaultUrl: '${keyVaultUri}secrets/probe-subscription-key', identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'probe'
          image: '${acrLoginServer}/synthetic-probe:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID', value: identityClientId }
            { name: 'Probe__ApiBaseUrl', value: apiBaseUrl }
            { name: 'Probe__ApiScope', value: '${apiAudience}/.default' }
            { name: 'Probe__SubscriptionKey', secretRef: 'probe-subscription-key' }
            { name: 'Jira__BaseUrl', value: jiraBaseUrl }
            { name: 'Jira__ProjectKey', value: jiraProjectKey }
            { name: 'Jira__Environment', value: environmentName }
            { name: 'Jira__User', secretRef: 'jira-user' }
            { name: 'Jira__ApiToken', secretRef: 'jira-token' }
          ]
        }
      ]
    }
  }
}

output jobName string = syntheticJob.name
output jobId string = syntheticJob.id
