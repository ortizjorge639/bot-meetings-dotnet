[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AppId,
    [Parameter(Mandatory)] [string[]] $OrganizerIdentity,
    [string] $PolicyName = "BotMeetingsTranscriptAccess"
)

$ErrorActionPreference = "Stop"
if (-not (Get-Module -ListAvailable -Name MicrosoftTeams)) {
    throw "MicrosoftTeams PowerShell is required. Install it with: Install-Module MicrosoftTeams -Scope CurrentUser"
}

Import-Module MicrosoftTeams
Connect-MicrosoftTeams

$policy = Get-CsApplicationAccessPolicy -Identity $PolicyName -ErrorAction SilentlyContinue
if (-not $policy) {
    New-CsApplicationAccessPolicy `
        -Identity $PolicyName `
        -AppIds $AppId `
        -Description "Allows the meeting transcript bot to access artifacts for explicitly assigned organizers."
}
elseif ($policy.AppIds -notcontains $AppId) {
    Set-CsApplicationAccessPolicy -Identity $PolicyName -AppIds (@($policy.AppIds) + $AppId)
}

foreach ($organizer in $OrganizerIdentity) {
    Grant-CsApplicationAccessPolicy -PolicyName $PolicyName -Identity $organizer
    Write-Host "Granted $PolicyName to $organizer"
}

Write-Warning "Microsoft Graph can take up to 30 minutes to observe application access policy changes."