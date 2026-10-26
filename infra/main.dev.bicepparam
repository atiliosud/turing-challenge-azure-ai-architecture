using 'main.bicep'

param environmentName = 'dev'
param suffix = 'ts01dev'
param alertEmail = 'oncall@example.com'
param publisherEmail = 'platform@example.com'
param apiAudience = 'api://techsolutions-api'
param entraAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'
param entraAdminGroupName = 'sg-techsol-pg-admins'
param jiraBaseUrl = 'https://example.atlassian.net'
param gpuMinNodes = 0 // dev: GPU only when there is work
param gpuMaxNodes = 1
param gpuSpotMaxNodes = 2
param postgresSku = 'Standard_D2ds_v5'
param monthlyBudgetEur = 1500
