# Meeting Transcript Copilot for Microsoft Teams

This .NET 10 sample listens for Microsoft Teams meeting events, retrieves the meeting transcript from Microsoft Graph, and answers post-meeting questions with speaker- and timestamp-aware citations using Azure OpenAI and Microsoft Agent Framework.

> [!CAUTION]
> **Preview, not production-ready.** Transcript jobs and documents are JSON files on one App Service instance. The store has process-local locking and retention cleanup, but no distributed leases, transactions, encryption key isolation, legal hold, user deletion workflow, or multi-instance consistency. Keep App Service at one instance and use only non-sensitive test meetings until the production gaps are addressed.

## Documentation index

### Start here

| Goal | Guide |
| --- | --- |
| Understand the solution and deploy a preview | [Architecture](#architecture) and [15-minute developer quickstart](#15-minute-developer-quickstart) |
| Configure Entra, Microsoft Graph, Teams policies, and Azure Bot | [Tenant and Teams administrator setup](docs/tenant-admin-setup.md) |
| Build the Teams package and publish it to the organization catalog | [Organization publishing and end-to-end test plan](docs/publish-and-test.md) |
| Plan monitoring, retention, privacy, security, and production hardening | [Operations, privacy, and production readiness](docs/operations-and-production.md) |
| Review license and dependency notice responsibilities | [MIT License](LICENSE) and [Third-party notices](THIRD-PARTY-NOTICES.md) |

### README navigation

- [What is included](#what-is-included)
- [Architecture](#architecture)
- [15-minute developer quickstart](#15-minute-developer-quickstart)
- [Local development](#local-development)
- [Configuration](#configuration)
- [Security and data handling](#security-and-data-handling)
- [Troubleshooting](#troubleshooting)
- [Validation](#validation)
- [Licensing and attribution](#licensing-and-attribution)

## What is included

- Meeting start, end, join, and leave handlers.
- Asynchronous transcript polling with bounded retries.
- Tenant- and conversation-scoped transcript retrieval.
- Lexical context selection and cited transcript Q&A.
- Bounded model concurrency, queue wait, and answer timeout.
- Configurable local-file retention cleanup.
- Liveness, readiness, and build-version endpoints.
- Bicep and PowerShell for a repeatable new-tenant preview deployment.
- GitHub Actions validation and App Service deployment.

## Architecture

1. Teams sends meeting activities to `/api/messages` through Azure Bot Service.
2. The meeting-end handler records a transcript job and returns immediately.
3. A hosted worker polls Microsoft Graph for the organizer's meeting transcript.
4. Parsed VTT cues become speaker-aware chunks stored under the configured data path.
5. The bot posts a readiness card in the meeting chat.
6. Questions in that chat select relevant chunks and invoke Azure OpenAI.
7. Responses are accepted only when they contain known source labels; deterministic speaker and timestamp notes are appended.

The current Graph call is app-only and uses `/users/{organizerId}/onlineMeetings/{meetingId}/transcripts`. It therefore requires organization-wide Graph application permissions **and** a Teams application access policy for each allowed organizer. Manifest RSC consent is not a substitute for that authorization path.

## 15-minute developer quickstart

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
- An Azure subscription where Azure OpenAI is available
- A Microsoft 365 tenant with Teams
- Roles needed for the selected steps:
  - Azure Contributor and User Access Administrator, or equivalent scoped roles
  - Application Administrator to create the app registration
  - Global Administrator or Privileged Role Administrator to grant Graph admin consent
  - Teams Administrator to configure transcript access and application access policy

### 1. Provision an isolated preview environment

Sign in to the intended Azure tenant and subscription, then run:

```powershell
az login --tenant <tenant-guid>
./infra/provision-tenant.ps1 `
  -NamePrefix meeting-copilot-dev `
  -ResourceGroup meeting-copilot-dev-rg `
  -Location centralus `
  -Subscription <subscription-id>
```

The script creates or reuses a single-tenant Entra app, configures the least-privileged `OnlineMeetingTranscript.Read.All` permission, creates a one-year bootstrap secret, and deploys App Service, Azure Bot with the Teams channel, and a keyless Azure OpenAI account. It does **not** silently grant tenant admin consent unless `-GrantAdminConsent` is explicitly supplied by an authorized administrator.

On reruns, pass `-AppId <application-client-id>` to select the existing registration explicitly. Credential creation is additive so an unsuccessful deployment does not invalidate the previously deployed credential; remove superseded credentials only after the new deployment is validated.

### 2. Complete tenant administrator actions

1. In **Microsoft Entra admin center** > **App registrations** > the new app > **API permissions**, review and grant tenant-wide admin consent for:
   - `OnlineMeetingTranscript.Read.All` (Application)
2. Assign access only to test organizers:

```powershell
./infra/configure-teams-access.ps1 `
  -AppId <application-client-id> `
  -OrganizerIdentity organizer@contoso.com
```

1. In **Teams admin center**, confirm meeting transcription is allowed for the test users and that Graph transcript access and speaker attribution are enabled under the applicable meeting policies.
2. Wait up to 30 minutes for a new application access policy to affect Microsoft Graph.

See [Tenant and Teams administrator setup](docs/tenant-admin-setup.md) for the permission decision guide, policy details, and failure modes.

### 3. Publish the code

```powershell
dotnet restore --locked-mode
dotnet test tests/BotMeetings.Tests/BotMeetings.Tests.csproj -c Release --no-restore
dotnet publish BotMeetings.csproj -c Release --no-restore -o publish
```

Deploy the `publish` directory to the App Service created by Bicep. The supplied GitHub workflow does this for the repository's configured environment; update its environment variables and OIDC secrets before enabling it in another repository.

### 4. Build the Teams package

Use real, public HTTPS legal and support URLs owned by your organization:

```powershell
./infra/build-app-package.ps1 `
  -TeamsAppId <new-stable-teams-app-guid> `
  -BotAppId <application-client-id> `
  -WebAppUrl https://<app-name>.azurewebsites.net `
  -DeveloperName "Contoso" `
  -DeveloperWebsite https://contoso.com `
  -PrivacyUrl https://contoso.com/privacy `
  -TermsUrl https://contoso.com/terms
```

The package is written to `appPackage/build/bot-meetings.zip`. Keep both IDs stable when updating the app; changing the Teams app ID creates a different app.

### 5. Install and test

For development, in Teams select **Apps** > **Manage your apps** > **Upload an app** > **Upload a custom app**. Add it to a scheduled private meeting chat before the meeting starts.

1. Start and transcribe the meeting.
2. Have at least two people speak so attribution can be checked.
3. End the meeting and wait for the readiness message.
4. Ask a question whose answer is explicit in the transcript.
5. Verify the answer, source label, speaker, timestamp, and tenant/chat isolation.

For organization-wide distribution, follow [Publish and validate in your organization](docs/publish-and-test.md).

## Local development

Create a git-ignored `appsettings.Development.json` and enter local secrets directly. Do not commit or paste its contents into logs:

```json
{
  "Teams": {
    "TenantId": "<tenant-guid>",
    "ClientId": "<application-client-id>",
    "ClientSecret": "<secret-value>"
  },
  "TranscriptAgent": {
    "Endpoint": "https://<account>.openai.azure.com/",
    "DeploymentName": "gpt-4.1-mini"
  }
}
```

Authenticate to Azure for `DefaultAzureCredential`, then run `dotnet run`. Use a public HTTPS development tunnel and set the Azure Bot messaging endpoint to `<tunnel-url>/api/messages`.

## Configuration

| Setting | Default | Purpose |
| --- | ---: | --- |
| `Teams__TenantId` | required | Only this tenant is accepted. |
| `Teams__ClientId` | required | Entra application and Azure Bot app ID. |
| `Teams__ClientSecret` | required | Bot authentication secret. Store in Key Vault for production. |
| `TranscriptIngestion__DataPath` | local path | Use `/home/data/...` on single-instance App Service. |
| `TranscriptIngestion__MaximumWait` | 5 minutes | Maximum Graph publication polling window. |
| `TranscriptIngestion__MaximumAttempts` | 20 | Maximum ingestion attempts. |
| `TranscriptIngestion__RetentionPeriod` | 30 days | Deletes expired jobs and associated documents. |
| `TranscriptIngestion__PurgeInterval` | 6 hours | Cleanup frequency. |
| `TranscriptAgent__Endpoint` | required | Azure OpenAI endpoint. |
| `TranscriptAgent__DeploymentName` | `gpt-4.1-mini` | Model deployment name. |
| `TranscriptAgent__MaximumContextChunks` | 50 | Lexically ranked chunks sent to the model. |
| `TranscriptAgent__MaximumConcurrentAnswers` | 2 | In-process model concurrency. |
| `TranscriptAgent__QueueWaitTimeout` | 5 seconds | Admission wait before returning busy. |
| `TranscriptAgent__AnswerTimeout` | 2 minutes | Per-answer model deadline. |

Runtime probes:

- `/health/live`: process is serving HTTP.
- `/health/ready`: startup configuration succeeded. This is not a dependency probe.
- `/version`: assembly version and `BUILD_COMMIT` setting.

## Security and data handling

- Transcript text is treated as untrusted prompt data.
- Q&A lookup is scoped by tenant ID and Teams conversation ID.
- Azure OpenAI local keys are disabled by Bicep; App Service uses managed identity.
- The Teams bot still uses a bootstrap client secret. Production deployments should use Key Vault references, rotation alerts, and a tested rotation runbook.
- Model citations are syntactically constrained to selected chunks; they are not claim-by-claim factual verification.
- `GlobalStandard` model deployments can process data outside the resource region. Choose an approved deployment type and region based on organizational residency requirements.
- Define notice, consent, retention, deletion, eDiscovery, legal hold, and acceptable-use policy with privacy/legal/compliance owners before real use.

See [Operations, privacy, and production readiness](docs/operations-and-production.md).

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `403 No application access policy found` | The organizer lacks the Teams application access policy, or propagation is incomplete. |
| `403 GraphAccessToTranscriptsDisabled` | Tenant Graph transcript access is disabled. |
| Transcript has no speakers | Speaker attribution is disabled by tenant policy or the returned format lacks attribution. |
| No transcript after meeting end | Transcription was not started, meeting type is unsupported, organizer policy is missing, or publication exceeded the polling window. |
| Bot does not receive events | Azure Bot endpoint/channel, app IDs, app installation scope, or manifest permissions are incorrect. |
| Q&A says no relevant material | No question terms or speaker names matched the transcript chunks. Ask a more specific question. |
| Q&A is busy or times out | Concurrency, queue-wait, or model timeout limits were reached. Check App Service and Azure OpenAI metrics. |

## Validation

```powershell
dotnet restore --locked-mode
dotnet format BotMeetings.csproj --verify-no-changes --no-restore
dotnet test tests/BotMeetings.Tests/BotMeetings.Tests.csproj -c Release --no-restore
dotnet list BotMeetings.csproj package --vulnerable --include-transitive
az bicep build --file infra/main.bicep
```

## Licensing and attribution

The repository is licensed under the [MIT License](LICENSE) and is based on Microsoft's [Bot Meetings Teams SDK sample](https://github.com/OfficeDev/Microsoft-Teams-Samples/tree/main/samples/TeamsSDK/bot-meetings/dotnet/bot-meetings). NuGet and GitHub Actions dependencies retain their own licenses. Review [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and regenerate the dependency inventory before every release.

[Back to documentation index](#documentation-index)

