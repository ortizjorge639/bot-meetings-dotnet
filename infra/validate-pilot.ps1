param(
    [string] $ExpectedCommit,
    [string] $PublishedPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Get-AzureJson([string[]] $Arguments) {
    $result = & az @Arguments --output json
    if ($LASTEXITCODE -ne 0) { throw 'Azure pilot resource validation failed. No deployment is permitted.' }
    return ($result | ConvertFrom-Json)
}

$target = @{
    AZURE_SUBSCRIPTION_ID = 'cbab996c-0a31-4b66-bdcd-7ead3cad1487'
    AZURE_TENANT_ID = '8caad56b-f716-4068-b468-3bdf8e4d357b'
    AZURE_RESOURCE_GROUP = 'bot-meeting-poc-rg'
    AZURE_WEBAPP_NAME = 'meeting-poc-438871-izha3l56k5kdy'
    AZURE_WEBAPP_URL = 'https://meeting-poc-438871-izha3l56k5kdy.azurewebsites.net'
    AZURE_COMMUNICATION_SERVICE_NAME = 'meeting-poc-438871-acs-izha3l56k5kdy'
}
foreach ($entry in $target.GetEnumerator()) {
    if ([Environment]::GetEnvironmentVariable($entry.Key) -cne $entry.Value) {
        throw "Unexpected deployment target: $($entry.Key)."
    }
}

$account = Get-AzureJson -Arguments @('account', 'show')
if ($account.id -ne $target.AZURE_SUBSCRIPTION_ID -or
    $account.tenantId -ne $target.AZURE_TENANT_ID -or $account.state -ne 'Enabled') {
    throw 'The signed-in Azure context does not match the enabled pilot subscription and tenant.'
}

$scope = @('--subscription', $target.AZURE_SUBSCRIPTION_ID, '--resource-group', $target.AZURE_RESOURCE_GROUP)
$resourceRoot = "/subscriptions/$($target.AZURE_SUBSCRIPTION_ID)/resourceGroups/$($target.AZURE_RESOURCE_GROUP)/providers"
$app = Get-AzureJson -Arguments (@('webapp', 'show', '--name', $target.AZURE_WEBAPP_NAME) + $scope)
if ($app.id -ne "$resourceRoot/Microsoft.Web/sites/$($target.AZURE_WEBAPP_NAME)" -or
    $app.defaultHostName -ne ([uri] $target.AZURE_WEBAPP_URL).Host -or
    $app.state -ne 'Running' -or $app.location -replace '\s', '' -ne 'westus3' -or
    $app.identity.type -notmatch 'SystemAssigned') {
    throw 'The pilot app resource, hostname, region, state, or managed identity did not match.'
}

$acs = Get-AzureJson -Arguments (@(
    'resource', 'show', '--resource-type', 'Microsoft.Communication/communicationServices',
    '--name', $target.AZURE_COMMUNICATION_SERVICE_NAME
) + $scope)
if ($acs.id -ne "$resourceRoot/Microsoft.Communication/communicationServices/$($target.AZURE_COMMUNICATION_SERVICE_NAME)" -or
    [string]::IsNullOrWhiteSpace($acs.properties.hostName)) {
    throw 'The existing pilot ACS resource did not match.'
}

$settings = @{}
$values = Get-AzureJson -Arguments (@('webapp', 'config', 'appsettings', 'list', '--name', $target.AZURE_WEBAPP_NAME) + $scope)
foreach ($setting in $values) { $settings[$setting.name] = $setting.value }
$expectedSettings = @{
    Teams__TenantId = $target.AZURE_TENANT_ID
    Teams__ClientId = 'c28b449a-cb54-4891-b26e-85d882990170'
    LiveContext__Enabled = 'true'
    LiveContext__DataPath = '/home/data/bot-meetings/live-context'
    TranscriptIngestion__DataPath = '/home/data/bot-meetings/transcript-ingestion'
    TranscriptAgent__DeploymentName = 'gpt-4.1-mini'
}
foreach ($entry in $expectedSettings.GetEnumerator()) {
    if ($settings[$entry.Key] -ne $entry.Value) { throw "Pilot configuration mismatch: $($entry.Key)." }
}
if ([string]::IsNullOrWhiteSpace($settings.Teams__ClientSecret) -or
    ([string] $settings.LiveContext__AccessKey).Length -lt 32) {
    throw 'The existing pilot credentials are missing or invalid. They will not be replaced.'
}
$acsEndpoint = @(([string] $settings.LiveContext__AcsConnectionString).Split(';') |
    Where-Object { $_ -match '^endpoint=' })
$endpoint = $null
$modelEndpoint = $null
if ($acsEndpoint.Count -ne 1 -or
    -not [uri]::TryCreate($acsEndpoint[0].Substring(9), [UriKind]::Absolute, [ref] $endpoint) -or
    $endpoint.Scheme -ne 'https' -or $endpoint.Host -ne $acs.properties.hostName -or
    -not [uri]::TryCreate($settings.TranscriptAgent__Endpoint, [UriKind]::Absolute, [ref] $modelEndpoint) -or
    $modelEndpoint.Scheme -ne 'https' -or $modelEndpoint.Host -ne 'meetingpoc438871izha3l56.openai.azure.com') {
    throw 'The existing ACS or Azure OpenAI endpoint does not match the pilot.'
}
$settings.Clear()
$values = $null

if ($ExpectedCommit) {
    if ($ExpectedCommit -notmatch '^[0-9a-f]{40}$' -or -not (Test-Path -LiteralPath $PublishedPath -PathType Container)) {
        throw 'Deployment verification requires an exact commit and the published artifact directory.'
    }
    $confirmed = $false
    for ($attempt = 1; $attempt -le 24; $attempt++) {
        try {
            $version = Invoke-RestMethod "$($target.AZURE_WEBAPP_URL)/version" -TimeoutSec 30
            if ($version.commit -ceq $ExpectedCommit -and $version.commitSource -ceq 'assembly') {
                $confirmed = $true
                break
            }
        }
        catch {
            Write-Host "The deployed version is not reachable yet (attempt $attempt/24)."
        }
        if ($attempt -lt 24) { Start-Sleep -Seconds 5 }
    }
    if (-not $confirmed) { throw 'The running binaries did not report the expected embedded commit.' }

    $webRoot = Join-Path $PublishedPath 'wwwroot'
    $captionRoot = Join-Path $webRoot 'acs-participant'
    $files = @(Get-ChildItem -LiteralPath $captionRoot -File -Recurse)
    if ($files.Count -eq 0) { throw 'The published ACS artifact is missing.' }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath((Resolve-Path $webRoot).Path, $file.FullName).Replace('\', '/')
        $response = Invoke-WebRequest "$($target.AZURE_WEBAPP_URL)/$relative" -TimeoutSec 60
        $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($response.RawContentStream))
        if ($actualHash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
            throw "Deployed ACS artifact differs: $relative."
        }
    }
}

$ready = Invoke-RestMethod "$($target.AZURE_WEBAPP_URL)/health/ready" -TimeoutSec 30
$live = Invoke-RestMethod "$($target.AZURE_WEBAPP_URL)/health/live" -TimeoutSec 30
$token = Invoke-WebRequest "$($target.AZURE_WEBAPP_URL)/api/acs/token" -SkipHttpErrorCheck -TimeoutSec 30
$messages = Invoke-WebRequest "$($target.AZURE_WEBAPP_URL)/api/messages" -SkipHttpErrorCheck -TimeoutSec 30
if ($ready.status -ne 'ready' -or $live.status -ne 'healthy' -or $token.StatusCode -ne 401 -or $messages.StatusCode -ne 405) {
    throw 'Pilot health or unauthenticated endpoint checks failed.'
}
Write-Host "Validated existing pilot: $($app.id). No resources, settings, credentials, or permissions were changed."
