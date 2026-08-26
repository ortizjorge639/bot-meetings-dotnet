# Bot Meetings - .NET (C#)

This sample demonstrates a bot for Microsoft Teams that handles real-time meeting events (start, end, participant join/leave) and retrieves meeting transcripts via Microsoft Graph.

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
   ```

   Bash:
   ```bash
   export Teams__TenantId="<your-tenant-id>"
   export Teams__ClientId="<your-app-registration-client-id>"
   export Teams__ClientSecret="<your-client-secret>"
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

Save the settings and restart the App Service. The application validates all
three settings during startup and reports the missing setting by name.

The app registration requires the Microsoft Graph application permissions
`OnlineMeetings.Read.All` and `OnlineMeetingTranscript.Read.All`, with tenant
administrator consent. Transcript retrieval is also subject to the tenant's
Teams meeting transcript API access settings and application access policy.

## Source

This standalone project is based on the
[Microsoft Teams Bot Meetings sample](https://github.com/OfficeDev/Microsoft-Teams-Samples/tree/main/samples/TeamsSDK/bot-meetings/dotnet/bot-meetings)
and retains its MIT license.
