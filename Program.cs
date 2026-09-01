// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.Identity;
using BotMeetings.TranscriptIngestion;
using BotMeetings.TranscriptQna;
using Microsoft.Graph;
using Microsoft.Teams.Plugins.AspNetCore.Extensions;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Activities;
using Microsoft.Teams.Apps.Activities.Events;
using Microsoft.Teams.Cards;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

var tenantId = GetRequiredConfigurationValue(builder.Configuration, "Teams:TenantId");
var clientId = GetRequiredConfigurationValue(builder.Configuration, "Teams:ClientId");
var clientSecret = GetRequiredConfigurationValue(builder.Configuration, "Teams:ClientSecret");

builder.AddTeams();
builder.Services
    .AddOptions<TranscriptIngestionOptions>()
    .Bind(builder.Configuration.GetSection(TranscriptIngestionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<ISystemClock, SystemClock>();
builder.Services.AddSingleton<VttTranscriptParser>();
builder.Services.AddSingleton<SourceDocumentBuilder>();
builder.Services.AddSingleton<FileTranscriptStore>();
builder.Services.AddSingleton<ITranscriptIngestionStore>(
    services => services.GetRequiredService<FileTranscriptStore>());
builder.Services.AddSingleton<ISourceDocumentSink>(
    services => services.GetRequiredService<FileTranscriptStore>());
builder.Services.AddSingleton<ISourceDocumentStore>(
    services => services.GetRequiredService<FileTranscriptStore>());
builder.Services.AddSingleton<TeamsTranscriptNotifier>();
builder.Services.AddSingleton<ITranscriptNotificationSink>(
    services => services.GetRequiredService<TeamsTranscriptNotifier>());

var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
builder.Services.AddSingleton(new GraphServiceClient(
    credential,
    ["https://graph.microsoft.com/.default"]));
builder.Services.AddSingleton<ITranscriptProvider, GraphTranscriptProvider>();
builder.Services.AddSingleton<TranscriptIngestionProcessor>();
builder.Services.AddHostedService<TranscriptIngestionWorker>();
builder.Services
    .AddOptions<TranscriptAgentOptions>()
    .Bind(builder.Configuration.GetSection(TranscriptAgentOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<TranscriptContextSelector>();
builder.Services.AddSingleton<ITranscriptAnswerGenerator, AgentFrameworkTranscriptAnswerGenerator>();
builder.Services.AddSingleton<TranscriptQuestionAnsweringService>();

var webApp = builder.Build();
var teamsApp = webApp.UseTeams(true);
var transcriptNotifier = webApp.Services.GetRequiredService<TeamsTranscriptNotifier>();
transcriptNotifier.Initialize(async (conversationId, card) =>
{
    await teamsApp.Send(conversationId, card);
});

static string GetRequiredConfigurationValue(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            $"Missing required configuration '{key}'. Set it in local configuration or with the '{key.Replace(":", "__")}' environment variable.");
    }

    return value;
}

static bool IsExpectedTenant(string? activityTenantId, string configuredTenantId) =>
    !string.IsNullOrWhiteSpace(activityTenantId) &&
    string.Equals(activityTenantId, configuredTenantId, StringComparison.OrdinalIgnoreCase);

// Register meeting participant join handler
teamsApp.OnMeetingJoin(async (context, cancellationToken) =>
{
    if (!IsExpectedTenant(context.Activity.Conversation.TenantId, tenantId)) return;
    var activity = context.Activity.Value;
    var participant = activity.Members?.FirstOrDefault();
    if (string.IsNullOrEmpty(participant?.User?.AadObjectId)) return;

    var member = participant.User.Name;
    var role = participant.Meeting?.Role ?? "a participant";

    var card = new AdaptiveCard
    {
        Schema = "http://adaptivecards.io/schemas/adaptive-card.json",
        Body = new List<CardElement>
        {
            new TextBlock($"{member} has joined the meeting as {role}.")
            {
                Wrap = true,
                Weight = TextWeight.Bolder
            }
        }
    };

    await context.Send(card);
});

// Register meeting start handler
teamsApp.OnMeetingStart(async (context, cancellationToken) =>
{
    if (!IsExpectedTenant(context.Activity.Conversation.TenantId, tenantId)) return;
    var activity = context.Activity.Value;

    var card = new AdaptiveCard
    {
        Schema = "http://adaptivecards.io/schemas/adaptive-card.json",
        Body = new List<CardElement>
        {
            new TextBlock("The meeting has started.")
            {
                Wrap = true,
                Weight = TextWeight.Bolder,
                Size = TextSize.Large
            },
            new TextBlock($"**Title:** {activity.Title}")
            {
                Wrap = true
            },
            new TextBlock($"**Start Time:** {activity.StartTime}")
            {
                Wrap = true
            }
        },
        Actions = new List<Microsoft.Teams.Cards.Action>
        {
            new OpenUrlAction(activity.JoinUrl)
            {
                Title = "Join Meeting"
            }
        }
    };

    await context.Send(card);
});

// Queue transcript work and return the meeting webhook without waiting for Graph publication.
teamsApp.OnMeetingEnd(async (context, cancellationToken) =>
{
    if (!IsExpectedTenant(context.Activity.Conversation.TenantId, tenantId)) return;
    var activity = context.Activity.Value;
    var meetingInfo = await context.Api.Meetings.GetByIdAsync(activity.Id, cancellationToken);
    var request = new TranscriptIngestionRequest(
        context.Activity.Conversation.TenantId ?? tenantId,
        context.Activity.Conversation.Id,
        activity.Id,
        meetingInfo?.Details?.MSGraphResourceId ?? string.Empty,
        meetingInfo?.Organizer?.AadObjectId ?? string.Empty,
        null,
        activity.EndTime);

    var store = webApp.Services.GetRequiredService<ITranscriptIngestionStore>();
    await store.EnqueueAsync(request, cancellationToken);
});

// Register meeting participant leave handler
teamsApp.OnMeetingLeave(async (context, cancellationToken) =>
{
    if (!IsExpectedTenant(context.Activity.Conversation.TenantId, tenantId)) return;
    var activity = context.Activity.Value;
    var member = activity.Members?.FirstOrDefault()?.User?.Name;
    if (string.IsNullOrWhiteSpace(member)) return;

    var card = new AdaptiveCard
    {
        Schema = "http://adaptivecards.io/schemas/adaptive-card.json",
        Body = new List<CardElement>
        {
            new TextBlock($"{member} has left the meeting.")
            {
                Wrap = true,
                Weight = TextWeight.Bolder
            }
        }
    };

    await context.Send(card);
});

// Answer only from the latest completed transcript scoped to this Teams meeting chat.
teamsApp.OnMessage(async (context, cancellationToken) =>
{
    if (!IsExpectedTenant(context.Activity.Conversation.TenantId, tenantId))
    {
        webApp.Logger.LogWarning("Rejected a message from an unexpected or missing tenant.");
        return;
    }

    var question = context.Activity.Text?.Trim();
    if (string.IsNullOrWhiteSpace(question)) return;

    await context.Typing(cancellationToken: cancellationToken);
    try
    {
        var service = webApp.Services.GetRequiredService<TranscriptQuestionAnsweringService>();
        var result = await service.AnswerAsync(
            context.Activity.Conversation.TenantId ?? tenantId,
            context.Activity.Conversation.Id,
            question,
            cancellationToken);
        await context.Send(result.Message, cancellationToken);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        webApp.Logger.LogError(exception, "Transcript Q&A failed for conversation {ConversationId}", context.Activity.Conversation.Id);
        await context.Send("I couldn't answer from the transcript right now. Please try again in a moment.", cancellationToken);
    }
});

// Starts the Teams bot application and listens for incoming requests
webApp.MapGet("/meeting", () => Results.Content(
    """
    <!doctype html>
    <html>
      <head><meta charset="utf-8"><title>Bot Meetings</title></head>
      <body><h1>Bot Meetings</h1><p>This app is connected to the meeting.</p></body>
    </html>
    """,
    "text/html"));

webApp.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
webApp.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
webApp.MapGet("/version", () => Results.Ok(new
{
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
    commit = Environment.GetEnvironmentVariable("BUILD_COMMIT") ?? "unknown"
}));

webApp.Run();
