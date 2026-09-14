targetScope = 'resourceGroup'

@description('Globally unique base name used for Azure resources.')
@minLength(3)
@maxLength(24)
param namePrefix string

@description('Azure region for regional resources.')
param location string = resourceGroup().location

@description('Microsoft Entra tenant ID that owns the bot app registration.')
param tenantId string

@description('Application (client) ID of the bot app registration.')
param botClientId string

@secure()
@description('Client secret used by the Teams bot runtime. Rotate it after bootstrap and use Key Vault for production.')
param botClientSecret string

@description('Azure OpenAI model deployment name.')
param modelDeploymentName string = 'gpt-4.1-mini'

@description('Azure OpenAI model version available in the selected region.')
param modelVersion string = '2025-04-14'

@description('Model deployment capacity in thousands of tokens per minute.')
@minValue(1)
param modelCapacity int = 50

@description('Transcript retention for the single-instance preview file store.')
param transcriptRetention string = '30.00:00:00'

var normalizedPrefix = toLower(replace(namePrefix, '-', ''))
var planName = '${namePrefix}-plan'
var webAppName = '${namePrefix}-${uniqueString(subscription().id, resourceGroup().id)}'
var openAIName = take('${normalizedPrefix}${uniqueString(subscription().id, resourceGroup().id)}', 24)
var botName = '${namePrefix}-bot'

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource openAI 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openAIName
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAIName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAI
  name: modelDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1-mini'
      version: modelVersion
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'Teams__TenantId'
          value: tenantId
        }
        {
          name: 'Teams__ClientId'
          value: botClientId
        }
        {
          name: 'Teams__ClientSecret'
          value: botClientSecret
        }
        {
          name: 'TranscriptIngestion__DataPath'
          value: '/home/data/bot-meetings/transcript-ingestion'
        }
        {
          name: 'TranscriptIngestion__RetentionPeriod'
          value: transcriptRetention
        }
        {
          name: 'TranscriptAgent__Endpoint'
          value: openAI.properties.endpoint
        }
        {
          name: 'TranscriptAgent__DeploymentName'
          value: modelDeployment.name
        }
        {
          name: 'BUILD_COMMIT'
          value: 'infrastructure-only'
        }
      ]
      healthCheckPath: '/health/ready'
    }
  }
}

resource openAIUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAI.id, webApp.id, 'Cognitive Services OpenAI User')
  scope: openAI
  properties: {
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    )
  }
}

resource bot 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botName
  location: 'global'
  kind: 'azurebot'
  sku: {
    name: 'F0'
  }
  properties: {
    displayName: botName
    endpoint: 'https://${webApp.properties.defaultHostName}/api/messages'
    msaAppId: botClientId
    msaAppTenantId: tenantId
    msaAppType: 'SingleTenant'
  }
}

resource teamsChannel 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: 'global'
  properties: {
    channelName: 'MsTeamsChannel'
    properties: {
      isEnabled: true
    }
  }
}

output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output botName string = bot.name
output openAIAccountName string = openAI.name
output botMessagingEndpoint string = bot.properties.endpoint