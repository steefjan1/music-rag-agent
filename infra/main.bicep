@description('Location for all resources.')
param location string = resourceGroup().location

@description('Base name used for all resources.')
param baseName string = 'musicrag'

@description('Azure OpenAI chat deployment name.')
param chatDeploymentName string = 'gpt-4o'

@description('Azure OpenAI embeddings deployment name.')
param embeddingsDeploymentName string = 'text-embedding-ada-002'

// --- Storage ---
resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: '${replace(baseName, '-', '')}sa'
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${storage.name}/default/deploymentpackage'
  properties: { publicAccess: 'None' }
}

resource bandDataContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${storage.name}/default/band-data'
  properties: { publicAccess: 'None' }
}

// --- Log Analytics + Application Insights ---
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${baseName}-law'
  location: location
  properties: { sku: { name: 'PerGB2018' } }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// --- Azure OpenAI ---
resource openAI 'Microsoft.CognitiveServices/accounts@2024-04-01-preview' = {
  name: '${baseName}-oai'
  location: location
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: '${baseName}-oai'
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAI
  name: chatDeploymentName
  sku: { name: 'GlobalStandard', capacity: 10 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-4o', version: '2024-11-20' }
  }
}

resource embeddingsDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-04-01-preview' = {
  parent: openAI
  name: embeddingsDeploymentName
  sku: { name: 'Standard', capacity: 50 }
  properties: {
    model: { format: 'OpenAI', name: 'text-embedding-ada-002', version: '2' }
  }
  dependsOn: [chatDeployment]
}

// --- Azure AI Search ---
resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: '${baseName}-search'
  location: location
  sku: { name: 'basic' }
  properties: {
    replicaCount: 1
    partitionCount: 1
    publicNetworkAccess: 'enabled'
    semanticSearch: 'standard'
  }
}

// --- Flex Consumption function app ---
resource hostingPlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: '${baseName}-plan'
  location: location
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  kind: 'functionapp'
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${baseName}-func'
  location: location
  kind: 'functionapp'
  identity: { type: 'SystemAssigned' }
  tags: { 'azd-service-name': 'api' }
  dependsOn: [deploymentContainer, bandDataContainer]
  properties: {
    serverFarmId: hostingPlan.id
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'STORAGE_CONNECTION__accountName', value: storage.name }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'AZURE_OPENAI_ENDPOINT', value: openAI.properties.endpoint }
        { name: 'OPENAI_CHAT_DEPLOYMENT', value: chatDeploymentName }
        { name: 'OPENAI_EMBEDDINGS_DEPLOYMENT', value: embeddingsDeploymentName }
        { name: 'EMBEDDING_DIMENSIONS', value: '1536' }
        { name: 'AZURE_SEARCH_ENDPOINT', value: 'https://${search.name}.search.windows.net' }
        { name: 'AZURE_STORAGE_ACCOUNT_NAME', value: storage.name }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}deploymentpackage'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: { maximumInstanceCount: 20, instanceMemoryMB: 2048 }
      runtime: { name: 'dotnet-isolated', version: '8.0' }
    }
  }
}

// --- RBAC ---
var storageBlobOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
resource storageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, storageBlobOwner)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobOwner)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

var cogServicesUser = 'a97b65f3-24c7-4388-baec-2e87135dc908'
resource openAIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAI.id, functionApp.id, cogServicesUser)
  scope: openAI
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cogServicesUser)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

var searchIndexContributor = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
resource searchRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, functionApp.id, searchIndexContributor)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexContributor)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output openAIEndpoint string = openAI.properties.endpoint
output storageAccountName string = storage.name
