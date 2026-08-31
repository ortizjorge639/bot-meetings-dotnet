# Bot Meetings - .NET (C#)

This sample demonstrates a bot for Microsoft Teams that handles real-time meeting events (start, end, participant join/leave), retrieves meeting transcripts via Microsoft Graph, and answers post-meeting questions from the transcript.

Meeting-end webhooks enqueue transcript retrieval and return immediately. A hosted worker polls Microsoft Graph, retries delayed transcript publication, posts the transcript card proactively to the original meeting conversation, and stores an idempotent, chunked source document for agent context. When processing completes, the bot posts a clear readiness notice. Questions asked in that meeting chat are answered by a Microsoft Agent Framework agent using only the latest completed transcript, with validated source labels plus deterministic speaker and timestamp notes.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Run the sample

1. Navigate to this directory:
   ```bash
   cd dotnet/bot-meetings
   ```

2. Configure the Microsoft Entra app used by the Teams bot. .NET maps double
   underscores in environment variable names to configuration sections.

   PowerShell:
   ```powershell
   $env:Teams__TenantId = "<your-tenant-id>"
   $env:Teams__ClientId = "<your-app-registration-client-id>"
   $env:Teams__ClientSecret = "<your-client-secret>"
   $env:TranscriptAgent__Endpoint = "https://<your-openai-resource>.openai.azure.com/"
   $env:TranscriptAgent__DeploymentName = "gpt-4.1-mini"
   ```

   Bash:
   ```bash
   export Teams__TenantId="<your-tenant-id>"
   export Teams__ClientId="<your-app-registration-client-id>"
   export Teams__ClientSecret="<your-client-secret>"
   export TranscriptAgent__Endpoint="https://<your-openai-resource>.openai.azure.com/"
   export TranscriptAgent__DeploymentName="gpt-4.1-mini"
   ```

   Keep credentials out of `appsettings.json` and source control. The bot ID in
   the Teams app manifest must match `Teams__ClientId`.

3. Restore dependencies and run:
   ```bash
   dotnet run
   ```

The bot will start listening on `http://localhost:3978`.

Once the bot is running, follow the
[Microsoft Teams bot meetings sample setup](https://github.com/OfficeDev/Microsoft-Teams-Samples/tree/main/samples/TeamsSDK/bot-meetings)
to provision your app and side-load it into Teams using the
[Teams Developer CLI](https://microsoft.github.io/teams-sdk/cli/).

## Configure Azure App Service

Add these application settings under **Settings** > **Environment variables**:

| Name | Value |
|------|-------|
| `Teams__TenantId` | Microsoft Entra tenant ID |
| `Teams__ClientId` | App registration application (client) ID and bot ID |
| `Teams__ClientSecret` | App registration client secret value |
| `TranscriptIngestion__DataPath` | `/home/data/bot-meetings/transcript-ingestion` |
| `TranscriptAgent__Endpoint` | Azure OpenAI account endpoint |
| `TranscriptAgent__DeploymentName` | `gpt-4.1-mini` |
| `TranscriptAgent__MaximumContextChunks` | `50` |
| `TranscriptAgent__MaximumQuestionCharacters` | `1000` |
| `TranscriptAgent__MaximumConcurrentAnswers` | `2` |

Save the settings and restart the App Service. The application validates all
three settings during startup and reports the missing setting by name.

The app registration requires the Microsoft Graph application permissions
`OnlineMeetings.Read.All` and `OnlineMeetingTranscript.Read.All`, with tenant
administrator consent. Transcript retrieval is also subject to the tenant's
Teams meeting transcript API access settings and application access policy.

The ingestion path must be outside `/home/site/wwwroot` so jobs and source
documents survive App Service deployments. Jobs are keyed by tenant and meeting
ID to suppress duplicate meeting-end events. By default, the worker polls every
15 seconds for up to five minutes. Transcript cards are capped at 20,000
characters while the complete transcript remains in the stored source document.

## Post-meeting transcript Q&A

Ask the bot a question in the same meeting chat after it posts the readiness notice. The bot:

- isolates retrieval by tenant and Teams conversation;
- uses the latest completed meeting transcript in that conversation;
- includes up to 50 transcript chunks so typical meetings are grounded end to end;
- bounds question length and concurrent model calls to control cost and load;
- treats transcript content as untrusted data and refuses unsupported answers;
- accepts only model responses with known source labels; and
- expands each source label into speaker names and transcript timestamps.

This phase deliberately reuses the existing App Service and durable transcript files. It adds no database, vector store, Azure AI Search service, or separate agent host. For larger multi-meeting corpora, semantic search can be introduced later without changing the Teams experience.

### Provision the minimal Azure delta

The idempotent PowerShell script creates one keyless Azure OpenAI account with one `gpt-4.1-mini` Global Standard deployment, enables the existing App Service system-assigned identity, grants only `Cognitive Services OpenAI User`, and sets non-secret app settings:

```powershell
.\infra\provision-transcript-agent.ps1
```

Capacity controls throughput quota for this pay-per-token deployment; it is not provisioned throughput. The default capacity of 50 supports whole-meeting prompts while retaining the single-resource architecture. `GlobalStandard` may process prompts outside the resource region; use `DataZoneStandard` where your organization's meeting-data residency requirements call for it.

## Test

```bash
dotnet test tests/BotMeetings.Tests/BotMeetings.Tests.csproj -c Release
```

## Source

This standalone project is based on the
[Microsoft Teams Bot Meetings sample](https://github.com/OfficeDev/Microsoft-Teams-Samples/tree/main/samples/TeamsSDK/bot-meetings/dotnet/bot-meetings)
and retains its MIT license.
