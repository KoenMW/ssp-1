param location string = resourceGroup().location

var prefix = 'ssp2025'
var servicePlanName = '${prefix}sp'
var functionAppName = '${prefix}fa'
var storageAccountName = '${prefix}sta'

var startQueueName = 'start-queue'
var imageQueueName = 'image-queue'
var metadataContainerName = 'metadata'
var imagesContainerName = 'images'

// Server Farm / App Service Plan
resource servicePlan 'Microsoft.Web/serverfarms@2021-03-01' = {
  name: servicePlanName
  location: location
  sku: {
    tier: 'Consumption'
    name: 'Y1'
  }
  kind: 'elastic'
}

// Storage Account
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

// Blob containers (nested)
resource metadataContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  name: '${storageAccount.name}/default/${metadataContainerName}'
}

resource imagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  name: '${storageAccount.name}/default/${imagesContainerName}'
}

// Queues (nested)
resource startQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-06-01' = {
  name: '${storageAccount.name}/default/${startQueueName}'
}

resource imageQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-06-01' = {
  name: '${storageAccount.name}/default/${imageQueueName}'
}

// Function App
var storageAccountConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net'

resource functionApp 'Microsoft.Web/sites@2021-03-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: servicePlan.id
    siteConfig: {
      FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
      AzureWebJobsStorage: storageAccountConnectionString
      WEBSITE_CONTENTAZUREFILECONNECTIONSTRING: storageAccountConnectionString
      WEBSITE_CONTENTSHARE: toLower(functionAppName)
    }
  }
  dependsOn: [
    storageAccount
    servicePlan
  ]

  resource functionAppConfig 'config@2021-03-01' = {
    name: 'appsettings'
    properties: {
      FUNCTIONS_EXTENSION_VERSION: '~4'
      FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
      WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED: '1'
      AzureWebJobsStorage: storageAccountConnectionString
      WEBSITE_CONTENTAZUREFILECONNECTIONSTRING: storageAccountConnectionString
      WEBSITE_CONTENTSHARE: toLower(functionAppName)
      WEATHER_FEED_URL: 'https://data.buienradar.nl/2.0/feed/json'
      IMAGE_FEED_URL: 'https://api.unsplash.com/photos/random?query=weather&orientation=landscape'
      UNSPLASH_ACCESS_KEY: 'Hti1fMh-PqT5IqZHC4z0cuBGlK-HiJwbohoGhcWXTOU'
      START_QUEUE_NAME: startQueueName
      IMAGE_QUEUE_NAME: imageQueueName
      METADATA_CONTAINER: metadataContainerName
      IMAGES_CONTAINER: imagesContainerName
      MAX_STATIONS: '3'
    }
  }
}

// Role assignment for storage access
var storageBlobDataContributorRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)

resource functionStorageRole 'Microsoft.Authorization/roleAssignments@2020-04-01-preview' = {
  name: guid(resourceGroup().id, functionApp.name, storageAccount.name, 'roleAssignment')
  properties: {
    roleDefinitionId: storageBlobDataContributorRole
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
  dependsOn: [
    functionApp
    storageAccount
  ]
}

// Outputs
output functionAppName string = functionApp.name
output functionAppDefaultHostName string = functionApp.properties.defaultHostName
output storageAccountName string = storageAccount.name
output imagesContainerUrl string = 'https://${storageAccount.name}.blob.core.windows.net/${imagesContainerName}'
output startQueueName string = startQueueName
output imageQueueName string = imageQueueName
