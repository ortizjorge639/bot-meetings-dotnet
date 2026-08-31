[CmdletBinding()]
param(
    [string] $ResourceGroup = "bot-meeting-rg",
    [string] $Location = "centralus",
    [string] $WebAppName = "bot-meeting-plan",
    [string] $OpenAIAccountName = "bot-meeting-transcript-ai",
    [string] $DeploymentName = "gpt-4.1-mini",
    [string] $ModelVersion = "2025-04-14",
    [int] $Capacity = 50,
    [string] $Subscription
)

$ErrorActionPreference = "Stop"

function Assert-AzSucceeded([string] $Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI is required."
}

if ($Subscription) {
    az account set --subscription $Subscription
    Assert-AzSucceeded "Selecting the Azure subscription"
}

az account show --output none
Assert-AzSucceeded "Validating Azure authentication"

az provider register --namespace Microsoft.CognitiveServices --wait
Assert-AzSucceeded "Registering Microsoft.CognitiveServices"

az cognitiveservices account show `
    --name $OpenAIAccountName `
    --resource-group $ResourceGroup `
    --output none 2>$null

if ($LASTEXITCODE -ne 0) {
    az cognitiveservices account create `
        --name $OpenAIAccountName `
        --resource-group $ResourceGroup `
        --location $Location `
        --kind OpenAI `
        --sku S0 `
        --custom-domain $OpenAIAccountName `
        --yes `
        --output none
    Assert-AzSucceeded "Creating the Azure OpenAI account"
}

$accountId = az cognitiveservices account show `
    --name $OpenAIAccountName `
    --resource-group $ResourceGroup `
    --query id `
    --output tsv
Assert-AzSucceeded "Reading the Azure OpenAI resource ID"

az resource update `
    --ids $accountId `
    --set properties.disableLocalAuth=true `
    --output none
Assert-AzSucceeded "Enforcing keyless Azure OpenAI authentication"

az cognitiveservices account deployment create `
    --name $OpenAIAccountName `
    --resource-group $ResourceGroup `
    --deployment-name $DeploymentName `
    --model-format OpenAI `
    --model-name gpt-4.1-mini `
    --model-version $ModelVersion `
    --sku-name GlobalStandard `
    --sku-capacity $Capacity `
    --output none
Assert-AzSucceeded "Creating or updating the model deployment"

$principalId = az webapp identity assign `
    --name $WebAppName `
    --resource-group $ResourceGroup `
    --query principalId `
    --output tsv
Assert-AzSucceeded "Enabling the App Service managed identity"

$assignment = az role assignment list `
    --assignee-object-id $principalId `
    --scope $accountId `
    --query "[?roleDefinitionName=='Cognitive Services OpenAI User'].id | [0]" `
    --output tsv
Assert-AzSucceeded "Checking the model access role"

if (-not $assignment) {
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role "Cognitive Services OpenAI User" `
        --scope $accountId `
        --output none
    Assert-AzSucceeded "Granting model access to the App Service"
}

$endpoint = az cognitiveservices account show `
    --name $OpenAIAccountName `
    --resource-group $ResourceGroup `
    --query properties.endpoint `
    --output tsv
Assert-AzSucceeded "Reading the Azure OpenAI endpoint"

az webapp config appsettings set `
    --name $WebAppName `
    --resource-group $ResourceGroup `
    --settings `
        "TranscriptAgent__Endpoint=$endpoint" `
        "TranscriptAgent__DeploymentName=$DeploymentName" `
        "TranscriptAgent__MaximumContextChunks=50" `
        "TranscriptAgent__MaximumQuestionCharacters=1000" `
        "TranscriptAgent__MaximumConcurrentAnswers=2" `
    --output none
Assert-AzSucceeded "Configuring the App Service"

Write-Host "Transcript Q&A infrastructure is ready."
Write-Host "Azure OpenAI account: $OpenAIAccountName"
Write-Host "Model deployment: $DeploymentName (GlobalStandard capacity $Capacity)"
Write-Host "App Service: $WebAppName (system-assigned managed identity)"