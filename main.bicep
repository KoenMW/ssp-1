@description('Name prefix for all resources')
param namePrefix string = 'weatherapp'

@description('Azure region to deploy resources')
param location string = resourceGroup().location

@description('Unsplash API Access Key')
@secure()
param unsplashAccessKey string

var storageName = toLower('${namePrefix}storage')
var functionAppName = '${namePrefix}-funcapp'
var planName = '${namePrefix}-plan'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
  }
}

resource imagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storage.name}/default/images'
  properties: { publicAccess: 'Blob' }
}

resource metadataContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storage.name}/default/metadata'
  properties: { publicAccess: 'Blob' }
}

resource startQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  name: '${storage.name}/default/start-queue'
}

resource imageQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  name: '${storage.name}/default/image-queue'
}

resource hostingPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    siteConfig: {
      appSettings: [
        { name: 'FUNCTIONS_WORKER_RUNTIME'; value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage'; value: storage.properties.primaryEndpoints.blob }
        { name: 'WEATHER_FEED_URL'; value: 'https://data.buienradar.nl/2.0/feed/json' }
        { name: 'IMAGE_FEED_URL'; value: 'https://api.unsplash.com/photos/random?query=weather&orientation=landscape' }
        { name: 'UNSPLASH_ACCESS_KEY'; value: unsplashAccessKey }
        { name: 'START_QUEUE_NAME'; value: 'start-queue' }
        { name: 'IMAGE_QUEUE_NAME'; value: 'image-queue' }
        { name: 'METADATA_CONTAINER'; value: 'metadata' }
        { name: 'IMAGES_CONTAINER'; value: 'images' }
        { name: 'MAX_STATIONS'; value: '3' }
      ]
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
    }
    httpsOnly: true
  }
}

output functionAppUrl string = 'https://${functionApp.name}.azurewebsites.net/api'
output storageAccountName string = storage.name
