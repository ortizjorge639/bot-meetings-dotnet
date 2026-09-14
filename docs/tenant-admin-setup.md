# Tenant and Teams administrator setup

This guide covers the control-plane steps that Azure deployment cannot safely complete without tenant administrators.

## Permission model used by this repository

The implementation authenticates to Microsoft Graph as the Entra application and calls the organizer-scoped online meeting transcript API. Configure the application permission below and grant tenant admin consent:

| Permission | Type | Why it is needed |
| --- | --- | --- |
| `OnlineMeetingTranscript.Read.All` | Application | List and download meeting transcripts without a signed-in user. |

Then configure a Teams application access policy containing the app ID and grant it only to users whose organized meetings the app may process. Policy changes can take up to 30 minutes to reach Graph.

## Organization-wide permission versus RSC

Microsoft Teams also supports `OnlineMeetingTranscript.Read.Chat` resource-specific consent for scheduled private-chat meetings where the app is installed. Channel meetings use separate team-level RSC permissions such as `ChannelMeetingTranscript.Read.Group`.

RSC is narrower, but this repository's current Graph provider does not invoke the RSC-specific authorization flow. Do not remove the organization-wide Graph permissions or the organizer application access policy unless the provider is redesigned and tested for RSC. Ad hoc calls and channel meetings also have different support boundaries. The quickstart supports scheduled private-chat meetings only.

## Entra administrator checklist

1. Create a single-tenant app registration.
2. Record its application/client ID and directory/tenant ID.
3. Add the Microsoft Graph **Application** permission listed above.
4. Review least privilege and grant admin consent.
5. Create a time-limited client credential for Azure Bot authentication.
6. Rotate the credential on a schedule. Prefer an Azure Key Vault reference in production.
7. Verify the enterprise application is enabled and not blocked by conditional access or workload identity policy.

The bootstrap script configures the permission by its immutable permission ID. Admin consent remains a visible administrative decision unless the authorized operator supplies `-GrantAdminConsent`.

## Teams administrator checklist

1. Install the MicrosoftTeams PowerShell module.
2. Create an application access policy containing the app ID.
3. Grant the policy to a small organizer pilot group, not globally.
4. Confirm meeting transcription is permitted for those organizers.
5. Confirm tenant Graph transcript access is enabled.
6. Confirm speaker attribution is enabled if speaker-aware answers are required.
7. Confirm custom app upload is permitted for test users, or have an admin publish the package to the organization catalog.
8. Configure app-centric management or app permission policies so only pilot users can install the app.

Graph transcript access and speaker attribution are independent controls. A tenant can permit transcript download while suppressing speaker identity.

## Azure Bot checklist

1. Use the same Entra application/client ID as the manifest bot ID.
2. Configure a **Single Tenant** bot type and the owning tenant ID.
3. Set the messaging endpoint to `https://<host>/api/messages`.
4. Enable the Microsoft Teams channel.
5. Ensure the endpoint has a valid public HTTPS certificate and is not protected by interactive App Service authentication.

## Official references

- [Microsoft Graph transcript overview](https://learn.microsoft.com/microsoftteams/platform/graph-api/meeting-transcripts/overview-transcripts)
- [Configure application access to online meetings](https://learn.microsoft.com/graph/cloud-communication-online-meeting-application-access-policy)
- [Get callTranscript permissions](https://learn.microsoft.com/graph/api/calltranscript-get)
- [Resource-specific consent for Teams apps](https://learn.microsoft.com/microsoftteams/platform/graph-api/rsc/resource-specific-consent)
- [Connect Azure Bot to Microsoft Teams](https://learn.microsoft.com/azure/bot-service/channel-connect-teams)
- [Manage consent to Teams app permissions](https://learn.microsoft.com/microsoftteams/manage-consent-app-permissions)
