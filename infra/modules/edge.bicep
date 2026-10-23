// Edge: Front Door Premium + WAF (L7, bot protection, rate limit per IP; L3/L4 DDoS protection is built into
// Front Door) in front of API Management Standard v2 (auth, products, rate limits, quotas, usage metrics).
param location string
param prefix string
param tags object
param publisherEmail string
param publisherName string = 'TechSolutions'
param apimSubnetId string
param apiBackendFqdn string
param apiAudience string
param entraOpenIdConfigUrl string
param appInsightsId string
param appInsightsConnectionString string

var products = [
  { name: 'bronze', title: 'Bronze', approval: true }
  { name: 'silver', title: 'Silver', approval: true }
  { name: 'gold', title: 'Gold', approval: true }
  { name: 'synthetic', title: 'Synthetic probe (internal)', approval: true }
]
var productPolicies = {
  bronze: loadTextContent('../../apim/policies/product-bronze.xml')
  silver: loadTextContent('../../apim/policies/product-silver.xml')
  gold: loadTextContent('../../apim/policies/product-gold.xml')
  synthetic: loadTextContent('../../apim/policies/product-synthetic.xml')
}

// ---------- Front Door + WAF ----------
resource waf 'Microsoft.Network/FrontDoorWebApplicationFirewallPolicies@2024-02-01' = {
  name: '${replace(prefix, '-', '')}waf'
  location: 'global'
  tags: tags
  sku: { name: 'Premium_AzureFrontDoor' }
  properties: {
    policySettings: { enabledState: 'Enabled', mode: 'Prevention', requestBodyCheck: 'Enabled' }
    customRules: {
      rules: [
        {
          name: 'RateLimitPerIp'
          priority: 100
          enabledState: 'Enabled'
          ruleType: 'RateLimitRule'
          rateLimitDurationInMinutes: 1
          rateLimitThreshold: 6000 // coarse flood guard; per-customer limits are enforced by APIM
          matchConditions: [ { matchVariable: 'RequestUri', operator: 'Contains', matchValue: [ '/' ] } ]
          action: 'Block'
        }
      ]
    }
    managedRules: {
      managedRuleSets: [
        { ruleSetType: 'Microsoft_DefaultRuleSet', ruleSetVersion: '2.1', ruleSetAction: 'Block' }
        { ruleSetType: 'Microsoft_BotManagerRuleSet', ruleSetVersion: '1.1' }
      ]
    }
  }
}

resource afd 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: '${prefix}-afd'
  location: 'global'
  tags: tags
  sku: { name: 'Premium_AzureFrontDoor' }
}

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: afd
  name: '${prefix}-api'
  location: 'global'
  properties: { enabledState: 'Enabled' }
}

// ---------- API Management ----------
resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: '${prefix}-apim'
  location: location
  tags: tags
  sku: { name: 'StandardV2', capacity: 1 }
  identity: { type: 'SystemAssigned' }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
    virtualNetworkType: 'External' // v2 outbound VNet integration: reaches the internal Container Apps environment
    virtualNetworkConfiguration: { subnetResourceId: apimSubnetId }
  }
}

resource nvFrontDoorId 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'frontdoor-id'
  properties: { displayName: 'frontdoor-id', value: afd.properties.frontDoorId, secret: false }
}
resource nvOpenId 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'entra-openid-config-url'
  properties: { displayName: 'entra-openid-config-url', value: entraOpenIdConfigUrl, secret: false }
}
resource nvAudience 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'api-audience'
  properties: { displayName: 'api-audience', value: apiAudience, secret: false }
}

resource appInsightsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsightsId, apim.id, 'metrics-publisher')
  scope: appInsightsComponent
  properties: {
    principalId: apim.identity.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb') // Monitoring Metrics Publisher
    principalType: 'ServicePrincipal'
  }
}
resource appInsightsComponent 'Microsoft.Insights/components@2020-02-02' existing = { name: last(split(appInsightsId, '/')) }

resource logger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: apim
  name: 'appinsights'
  properties: {
    loggerType: 'applicationInsights'
    resourceId: appInsightsId
    credentials: { connectionString: appInsightsConnectionString, identityClientId: 'SystemAssigned' }
  }
  dependsOn: [ appInsightsPublisher ]
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'evaluations'
  properties: {
    displayName: 'TechSolutions Evaluations'
    path: ''
    protocols: [ 'https' ]
    serviceUrl: 'https://${apiBackendFqdn}'
    subscriptionRequired: true
    apiRevision: '1'
  }
}

resource operations 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for op in [
  { name: 'submit-evaluation', method: 'POST', url: '/v1/evaluations', params: [] }
  { name: 'get-evaluation', method: 'GET', url: '/v1/evaluations/{jobId}', params: [ 'jobId' ] }
  { name: 'get-evaluation-result', method: 'GET', url: '/v1/evaluations/{jobId}/result', params: [ 'jobId' ] }
  { name: 'get-usage', method: 'GET', url: '/v1/usage', params: [] }
]: {
  parent: api
  name: op.name
  properties: {
    displayName: op.name
    method: op.method
    urlTemplate: op.url
    templateParameters: [for p in op.params: { name: p, type: 'string', required: true }]
  }
}]

resource apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  properties: { format: 'rawxml', value: loadTextContent('../../apim/policies/api-policy.xml') }
  dependsOn: [ nvFrontDoorId, nvOpenId, nvAudience ]
}

resource apiDiagnostics 'Microsoft.ApiManagement/service/apis/diagnostics@2024-05-01' = {
  parent: api
  name: 'applicationinsights'
  properties: {
    loggerId: logger.id
    alwaysLog: 'allErrors'
    sampling: { samplingType: 'fixed', percentage: 100 }
    metrics: true // required by emit-metric
    httpCorrelationProtocol: 'W3C'
    verbosity: 'information'
  }
}

resource apimProducts 'Microsoft.ApiManagement/service/products@2024-05-01' = [for p in products: {
  parent: apim
  name: p.name
  properties: { displayName: p.title, state: 'published', subscriptionRequired: true, approvalRequired: p.approval, subscriptionsLimit: 10 }
}]

resource productApis 'Microsoft.ApiManagement/service/products/apis@2024-05-01' = [for (p, i) in products: {
  parent: apimProducts[i]
  name: api.name
}]

resource productPolicy 'Microsoft.ApiManagement/service/products/policies@2024-05-01' = [for (p, i) in products: {
  parent: apimProducts[i]
  name: 'policy'
  properties: { format: 'rawxml', value: productPolicies[p.name] }
}]

// ---------- Front Door routing to APIM ----------
resource originGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: afd
  name: 'apim'
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3 }
    healthProbeSettings: { probePath: '/status-0123456789abcdef', probeRequestType: 'GET', probeProtocol: 'Https', probeIntervalInSeconds: 60 }
  }
}

resource origin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: originGroup
  name: 'apim-gateway'
  properties: {
    hostName: replace(apim.properties.gatewayUrl, 'https://', '')
    originHostHeader: replace(apim.properties.gatewayUrl, 'https://', '')
    httpsPort: 443
    priority: 1
    weight: 1000
    enforceCertificateNameCheck: true
  }
}

resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'api'
  properties: {
    originGroup: { id: originGroup.id }
    supportedProtocols: [ 'Https' ]
    patternsToMatch: [ '/*' ]
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Disabled'
    linkToDefaultDomain: 'Enabled'
  }
  dependsOn: [ origin ]
}

resource securityPolicy 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = {
  parent: afd
  name: 'waf'
  properties: {
    parameters: {
      type: 'WebApplicationFirewall'
      wafPolicy: { id: waf.id }
      associations: [ { domains: [ { id: endpoint.id } ], patternsToMatch: [ '/*' ] } ]
    }
  }
}

output apimName string = apim.name
output apimId string = apim.id
output frontDoorEndpoint string = 'https://${endpoint.properties.hostName}'
output frontDoorProfileName string = afd.name
