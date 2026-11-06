$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot '../infra/validate-pilot.ps1'
$target = @{
    AZURE_SUBSCRIPTION_ID = 'cbab996c-0a31-4b66-bdcd-7ead3cad1487'
    AZURE_TENANT_ID = '8caad56b-f716-4068-b468-3bdf8e4d357b'
    AZURE_RESOURCE_GROUP = 'bot-meeting-poc-rg'
    AZURE_WEBAPP_NAME = 'meeting-poc-438871-izha3l56k5kdy'
    AZURE_WEBAPP_URL = 'https://meeting-poc-438871-izha3l56k5kdy.azurewebsites.net'
    AZURE_COMMUNICATION_SERVICE_NAME = 'meeting-poc-438871-acs-izha3l56k5kdy'
}
foreach ($entry in $target.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }

function Reset-Fixture {
    $global:PilotTestAzureCalls = 0
    $global:PilotTestCliFails = $false
    $script:account = @{ id = $target.AZURE_SUBSCRIPTION_ID; tenantId = $target.AZURE_TENANT_ID; state = 'Enabled' }
    $root = "/subscriptions/$($target.AZURE_SUBSCRIPTION_ID)/resourceGroups/$($target.AZURE_RESOURCE_GROUP)/providers"
    $script:app = @{
        id = "$root/Microsoft.Web/sites/$($target.AZURE_WEBAPP_NAME)"
        defaultHostName = ([uri] $target.AZURE_WEBAPP_URL).Host
        location = 'West US 3'
        state = 'Running'
        identity = @{ type = 'SystemAssigned' }
    }
    $script:acs = @{
        id = "$root/Microsoft.Communication/communicationServices/$($target.AZURE_COMMUNICATION_SERVICE_NAME)"
        properties = @{ hostName = 'pilot.communication.azure.com' }
    }
    $script:settings = @{
        Teams__TenantId = $target.AZURE_TENANT_ID
        Teams__ClientId = 'c28b449a-cb54-4891-b26e-85d882990170'
        Teams__ClientSecret = 'synthetic-test-value'
        LiveContext__Enabled = 'true'
        LiveContext__AccessKey = 'x' * 48
        LiveContext__DataPath = '/home/data/bot-meetings/live-context'
        LiveContext__AcsConnectionString = 'endpoint=https://pilot.communication.azure.com/;accesskey=synthetic'
        TranscriptIngestion__DataPath = '/home/data/bot-meetings/transcript-ingestion'
        TranscriptAgent__Endpoint = 'https://meetingpoc438871izha3l56.openai.azure.com/'
        TranscriptAgent__DeploymentName = 'gpt-4.1-mini'
    }
    $global:PilotTestFixtures = @{
        account = $script:account
        app = $script:app
        acs = $script:acs
        settings = $script:settings
    }
    $global:PilotTestTokenStatus = 401
    $global:PilotTestCommit = 'a' * 40
    $global:PilotTestCommitSource = 'assembly'
    $global:PilotTestAssetBytes = [Text.Encoding]::UTF8.GetBytes('synthetic-published-content')
}

function az {
    $global:PilotTestAzureCalls++
    $global:LASTEXITCODE = [int] $global:PilotTestCliFails
    if ($global:PilotTestCliFails) { return }
    if ($args[0] -eq 'account') { return ($global:PilotTestFixtures.account | ConvertTo-Json) }
    if ($args[0] -eq 'resource') { return ($global:PilotTestFixtures.acs | ConvertTo-Json) }
    if ($args[0] -eq 'webapp' -and $args[1] -eq 'show') { return ($global:PilotTestFixtures.app | ConvertTo-Json) }
    if ($args[0] -eq 'webapp' -and $args[1] -eq 'config' -and $args[3] -eq 'list') {
        return (@($global:PilotTestFixtures.settings.GetEnumerator() | ForEach-Object {
            @{ name = $_.Key; value = $_.Value }
        }) | ConvertTo-Json)
    }
    throw 'The validator attempted an unexpected Azure command.'
}

function Invoke-RestMethod([string] $Uri, [int] $TimeoutSec) {
    switch (([uri] $Uri).AbsolutePath) {
        '/health/ready' { return @{ status = 'ready' } }
        '/health/live' { return @{ status = 'healthy' } }
        '/version' { return @{ commit = $global:PilotTestCommit; commitSource = $global:PilotTestCommitSource } }
        default { throw 'Unexpected HTTP request.' }
    }
}

function Invoke-WebRequest([string] $Uri, [int] $TimeoutSec, [switch] $SkipHttpErrorCheck) {
    switch (([uri] $Uri).AbsolutePath) {
        '/api/acs/token' { return @{ StatusCode = $global:PilotTestTokenStatus } }
        '/api/messages' { return @{ StatusCode = 405 } }
        '/acs-participant/index.html' { return @{ RawContentStream = [IO.MemoryStream]::new($global:PilotTestAssetBytes) } }
        default { throw 'Unexpected HTTP request.' }
    }
}

function Start-Sleep([int] $Seconds) {}

function Assert-Rejected([string] $Expected, [hashtable] $Parameters = @{}) {
    $failure = $null
    try { & $validator @Parameters } catch { $failure = $_.Exception.Message }
    if (-not $failure -or -not $failure.Contains($Expected)) {
        throw "Expected rejection '$Expected'; received '$failure'."
    }
}

Reset-Fixture
& $validator
$env:AZURE_SUBSCRIPTION_ID = 'unexpected'
Reset-Fixture
Assert-Rejected 'Unexpected deployment target'
if ($global:PilotTestAzureCalls -ne 0) { throw 'A mismatched target reached Azure.' }
$env:AZURE_SUBSCRIPTION_ID = $target.AZURE_SUBSCRIPTION_ID

foreach ($case in @('tenant', 'subscription', 'disabled', 'app', 'host', 'region', 'identity', 'acs', 'runtime', 'key', 'endpoint', 'model', 'http', 'cli')) {
    Reset-Fixture
    $expected = switch ($case) {
        'tenant' { $script:account.tenantId = 'wrong'; 'signed-in Azure context' }
        'subscription' { $script:account.id = 'wrong'; 'signed-in Azure context' }
        'disabled' { $script:account.state = 'Disabled'; 'signed-in Azure context' }
        'app' { $script:app.id = 'wrong'; 'pilot app resource' }
        'host' { $script:app.defaultHostName = 'wrong.example.com'; 'pilot app resource' }
        'region' { $script:app.location = 'Central US'; 'pilot app resource' }
        'identity' { $script:app.identity.type = 'None'; 'pilot app resource' }
        'acs' { $script:acs.id = 'wrong'; 'pilot ACS resource' }
        'runtime' { $script:settings.Teams__ClientId = 'wrong'; 'configuration mismatch' }
        'key' { $script:settings.LiveContext__AccessKey = 'short'; 'credentials are missing or invalid' }
        'endpoint' { $script:settings.LiveContext__AcsConnectionString = 'endpoint=https://wrong.example.com/'; 'ACS or Azure OpenAI endpoint' }
        'model' { $script:settings.TranscriptAgent__Endpoint = 'https://wrong.example.com/'; 'ACS or Azure OpenAI endpoint' }
        'http' { $global:PilotTestTokenStatus = 200; 'endpoint checks failed' }
        'cli' { $global:PilotTestCliFails = $true; 'Azure pilot resource validation failed' }
    }
    Assert-Rejected $expected
}

$artifact = Join-Path ([IO.Path]::GetTempPath()) "pilot-validation-$([guid]::NewGuid().ToString('N'))"
$webRoot = Join-Path $artifact 'wwwroot'
$captionRoot = Join-Path $webRoot 'acs-participant'
$index = Join-Path $captionRoot 'index.html'
$sidecars = @("${index}.br", "${index}.gz")
try {
    Reset-Fixture
    $null = [IO.Directory]::CreateDirectory($captionRoot)
    [IO.File]::WriteAllBytes($index, $global:PilotTestAssetBytes)
    $parameters = @{ ExpectedCommit = 'a' * 40; PublishedPath = $artifact }
    & $validator @parameters
    foreach ($sidecar in $sidecars) { [IO.File]::WriteAllBytes($sidecar, [byte[]] @(1, 2, 3)) }
    & $validator @parameters
    $global:PilotTestAssetBytes = [Text.Encoding]::UTF8.GetBytes('outdated-content')
    Assert-Rejected 'Deployed ACS artifact differs' $parameters
    Reset-Fixture
    $global:PilotTestCommitSource = 'environment'
    Assert-Rejected 'running binaries did not report' $parameters
    Reset-Fixture
    $global:PilotTestCommit = 'b' * 40
    Assert-Rejected 'running binaries did not report' $parameters
    Write-Host '21 pilot deployment guard and artifact checks passed.'
}
finally {
    foreach ($sidecar in $sidecars) { if (Test-Path -LiteralPath $sidecar) { Remove-Item -LiteralPath $sidecar } }
    Remove-Item -LiteralPath $index
    Remove-Item -LiteralPath $captionRoot
    Remove-Item -LiteralPath $webRoot
    Remove-Item -LiteralPath $artifact
}
