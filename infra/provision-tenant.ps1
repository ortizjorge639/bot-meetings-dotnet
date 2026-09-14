[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $NamePrefix,
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [string] $Location = "centralus",
    [string] $Subscription,
    [string] $TenantId,
    [string] $AppId,
    [switch] $GrantAdminConsent
)

$ErrorActionPreference = "Stop"

function Assert-LastCommand([string] $Operation) {
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed with exit code $LASTEXITCODE." }
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI is required. See https://learn.microsoft.com/cli/azure/install-azure-cli."
}

az account show --output none
Assert-LastCommand "Validating Azure authentication"
if ($Subscription) {
    az account set --subscription $Subscription
    Assert-LastCommand "Selecting the Azure subscription"
}

if (-not $TenantId) {
    $TenantId = az account show --query tenantId --output tsv
    Assert-LastCommand "Reading the tenant ID"
}

$displayName = "$NamePrefix Teams Bot"
if ($AppId) {
    $clientId = az ad app show --id $AppId --query appId --output tsv
    Assert-LastCommand "Reading the specified app registration"
}
else {
    $matchingApps = @(az ad app list --display-name $displayName --query "[].appId" --output tsv)
    Assert-LastCommand "Checking for the app registration"
    if ($matchingApps.Count -gt 1) {
        throw "More than one app registration is named '$displayName'. Re-run with -AppId to select one explicitly."
    }
    $clientId = $matchingApps | Select-Object -First 1
}

if (-not $clientId) {
    $clientId = az ad app create `
        --display-name $displayName `
        --sign-in-audience AzureADMyOrg `
        --query appId `
        --output tsv
    Assert-LastCommand "Creating the app registration"
}

$graphResourceAccess = @(
    @{
        resourceAppId = "00000003-0000-0000-c000-000000000000"
        resourceAccess = @(
            @{ id = "a4a80d8d-d283-4bd8-8504-555ec3870630"; type = "Role" }
        )
    }
)
$permissionFile = Join-Path ([System.IO.Path]::GetTempPath()) "$([Guid]::NewGuid().ToString('N')).json"
try {
    ConvertTo-Json -InputObject $graphResourceAccess -Depth 5 | Set-Content -Path $permissionFile -Encoding utf8NoBOM
    az ad app update --id $clientId --required-resource-accesses "@$permissionFile"
    Assert-LastCommand "Configuring Microsoft Graph application permissions"
}
finally {
    Remove-Item $permissionFile -Force -ErrorAction SilentlyContinue
}

$credential = az ad app credential reset `
    --id $clientId `
    --append `
    --display-name "bootstrap-$((Get-Date).ToUniversalTime().ToString('yyyyMMdd'))" `
    --years 1 `
    --query password `
    --output tsv
Assert-LastCommand "Creating the bootstrap client secret"

az group create --name $ResourceGroup --location $Location --output none
Assert-LastCommand "Creating the resource group"

try {
    az deployment group create `
        --resource-group $ResourceGroup `
        --template-file (Join-Path $PSScriptRoot "main.bicep") `
        --parameters `
            namePrefix=$NamePrefix `
            location=$Location `
            tenantId=$TenantId `
            botClientId=$clientId `
            botClientSecret=$credential `
        --output table
    Assert-LastCommand "Deploying Azure resources"
}
finally {
    $credential = $null
}

if ($GrantAdminConsent) {
    az ad app permission admin-consent --id $clientId
    Assert-LastCommand "Granting tenant-wide Microsoft Graph admin consent"
}
else {
    Write-Warning "Admin consent is still required for OnlineMeetingTranscript.Read.All."
    Write-Host "A Global Administrator or Privileged Role Administrator can review and grant consent in Microsoft Entra admin center."
}

Write-Host "App registration client ID: $clientId"
Write-Host "Next: run configure-teams-access.ps1 for each permitted meeting organizer, then build the Teams package."