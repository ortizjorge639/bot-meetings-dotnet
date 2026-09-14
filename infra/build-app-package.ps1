[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Guid] $TeamsAppId,
    [Parameter(Mandatory)] [Guid] $BotAppId,
    [Parameter(Mandatory)] [Uri] $WebAppUrl,
    [Parameter(Mandatory)] [string] $DeveloperName,
    [Parameter(Mandatory)] [Uri] $DeveloperWebsite,
    [Parameter(Mandatory)] [Uri] $PrivacyUrl,
    [Parameter(Mandatory)] [Uri] $TermsUrl,
    [string] $OutputPath = (Join-Path $PSScriptRoot "..\appPackage\build\bot-meetings.zip")
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "..\appPackage\extracted"
$manifest = Get-Content (Join-Path $source "manifest.json") -Raw | ConvertFrom-Json -Depth 100
$hostName = $WebAppUrl.Host

$manifest.id = $TeamsAppId.ToString()
$manifest.bots[0].botId = $BotAppId.ToString()
$manifest.webApplicationInfo.id = $BotAppId.ToString()
$manifest.configurableTabs[0].configurationUrl = "https://$hostName/meeting"
$manifest.validDomains = @("*.botframework.com", $hostName)
$manifest.developer.name = $DeveloperName
$manifest.developer.websiteUrl = $DeveloperWebsite.AbsoluteUri
$manifest.developer.privacyUrl = $PrivacyUrl.AbsoluteUri
$manifest.developer.termsOfUseUrl = $TermsUrl.AbsoluteUri

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "bot-meetings-package-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item (Join-Path $source "*.png") $staging
    $manifest | ConvertTo-Json -Depth 100 -Compress | Set-Content (Join-Path $staging "manifest.json") -Encoding utf8NoBOM
    New-Item -ItemType Directory -Path (Split-Path $OutputPath) -Force | Out-Null
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $OutputPath -Force
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Created Teams app package: $OutputPath"