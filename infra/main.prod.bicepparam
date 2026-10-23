using 'main.bicep'

param environmentName = 'prod'
param suffix = 'ts01prd'
param alertEmail = 'oncall@example.com'
param publisherEmail = 'platform@example.com'
param apiAudience = 'api://techsolutions-api'
param entraAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'
param entraAdminGroupName = 'sg-techsol-pg-admins'
param jiraBaseUrl = 'https://example.atlassian.net'
// Valley (Jun-Feb): 1 warm T4. Peak season (Mar-May): raise gpuMinNodes/gpuMaxNodes in this file before March 1.
param gpuMinNodes = 1
param gpuMaxNodes = 4
param gpuSpotMaxNodes = 16
param postgresSku = 'Standard_D4ds_v5'
param monthlyBudgetEur = 4000
