<#
Deploy SSP-1 Function App and infrastructure
#>

param(
    [string]$ResourceGroupName = 'ssp_1',
    [string]$Location = 'westeurope',  # must match existing resource group
    [string]$NamePrefix = 'sspfunc',
    [string]$ProjectPath = '.'
)

function AbortIfError { param([int]$LastExitCode, [string]$Message) if ($LastExitCode -ne 0) { Write-Error $Message; exit $LastExitCode } }

# Check resource group exists
$rgExists = az group exists --name $ResourceGroupName
if (-not $rgExists) {
    az group create --name $ResourceGroupName --location $Location | Out-Null
}
else {
    Write-Host "Resource group $ResourceGroupName already exists in location $(az group show --name $ResourceGroupName --query location -o tsv)"
}

# Deploy Bicep template
$deploymentName = "ssp-deploy-$([guid]::NewGuid().ToString().Substring(0,8))"
$deployResult = az deployment group create `
    --resource-group $ResourceGroupName `
    --name $deploymentName `
    --template-file "$ProjectPath\main.bicep" `
    --parameters location=$Location `
    --query properties.outputs -o json
AbortIfError $LASTEXITCODE "Bicep deployment failed"

$outputs = $deployResult | ConvertFrom-Json
$functionAppName = $outputs.functionAppName.value
$storageAccountName = $outputs.storageAccountName.value

# Get storage connection string
$storageConnectionString = az storage account show-connection-string --name $storageAccountName --resource-group $ResourceGroupName -o tsv
AbortIfError $LASTEXITCODE "Failed to get storage connection string"

# Ensure queues and containers exist
az storage queue create --name start-queue --connection-string $storageConnectionString | Out-Null
az storage queue create --name image-queue --connection-string $storageConnectionString | Out-Null
az storage container create --name metadata --account-name $storageAccountName --public-access blob | Out-Null
az storage container create --name images --account-name $storageAccountName --public-access blob | Out-Null

# Build & publish function
$publishFolder = Join-Path $ProjectPath 'publish'
if (Test-Path $publishFolder) { Remove-Item $publishFolder -Recurse -Force }
dotnet publish $ProjectPath -c Release -o $publishFolder
AbortIfError $LASTEXITCODE "dotnet publish failed"

# Zip publish output
$zipFile = Join-Path $ProjectPath 'functionapp.zip'
if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
Compress-Archive -Path (Join-Path $publishFolder '*') -DestinationPath $zipFile
AbortIfError $LASTEXITCODE "Failed to zip publish folder"

# Deploy zip to Function App
az functionapp deployment source config-zip --resource-group $ResourceGroupName --name $functionAppName --src $zipFile
AbortIfError $LASTEXITCODE "Function app deployment failed"

# Update app settings from local.settings.json (except AzureWebJobsStorage)
$localSettingsPath = Join-Path $ProjectPath 'local.settings.json'
if (Test-Path $localSettingsPath) {
    $localSettings = Get-Content $localSettingsPath | ConvertFrom-Json
    foreach ($kv in $localSettings.Values.GetEnumerator()) {
        if ($kv.Key -ne "AzureWebJobsStorage") {
            az functionapp config appsettings set --name $functionAppName --resource-group $ResourceGroupName --settings "$($kv.Key)=$($kv.Value)" | Out-Null
        }
    }
}

# Override AzureWebJobsStorage
az functionapp config appsettings set --name $functionAppName --resource-group $ResourceGroupName --settings "AzureWebJobsStorage=$storageConnectionString" | Out-Null

# Output info
$defaultHostName = az functionapp show --name $functionAppName --resource-group $ResourceGroupName --query defaultHostName -o tsv
Write-Host "Deployment complete!"
Write-Host "Function base URL: https://$defaultHostName/api"
Write-Host "StartRequestFunction: https://$defaultHostName/api/StartRequestFunction"
Write-Host "GetProcessFunction: https://$defaultHostName/api/GetProcessFunction?processId=<id>"
Write-Host "Queues: start-queue, image-queue"
Write-Host "Containers: metadata, images"
