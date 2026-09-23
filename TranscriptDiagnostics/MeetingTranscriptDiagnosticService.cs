using System.Collections.Concurrent;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;

namespace BotMeetings.TranscriptDiagnostics;

public sealed record ActiveMeetingTarget(string MeetingResourceId, string OrganizerUserId);

public sealed record TranscriptDiagnosticResult(
    int StatusCode,
    int TranscriptCount,
    int? ContentCharacters,
    DateTimeOffset CheckedAt);

public interface ITranscriptDiagnosticClient
{
    Task<TranscriptDiagnosticResult> CheckAsync(
        ActiveMeetingTarget target,
        CancellationToken cancellationToken);
}

public sealed class ActiveMeetingStore
{
    private readonly ConcurrentDictionary<string, ActiveMeetingTarget> meetings = new(StringComparer.Ordinal);

    public void Set(string conversationId, ActiveMeetingTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(target);
        meetings[conversationId] = target;
    }

    public bool TryGet(string conversationId, out ActiveMeetingTarget? target) =>
        meetings.TryGetValue(conversationId, out target);
}

public sealed class GraphTranscriptDiagnosticClient(GraphServiceClient graphClient) : ITranscriptDiagnosticClient
{
    public async Task<TranscriptDiagnosticResult> CheckAsync(
        ActiveMeetingTarget target,
        CancellationToken cancellationToken)
    {
        var response = await graphClient.Users[target.OrganizerUserId]
            .OnlineMeetings[target.MeetingResourceId]
            .Transcripts
            .GetAsync(cancellationToken: cancellationToken);
        var transcripts = response?.Value?
            .Where(transcript => !string.IsNullOrWhiteSpace(transcript.Id))
            .OrderByDescending(transcript => transcript.CreatedDateTime)
            .ToArray() ?? [];

        if (transcripts.Length == 0)
        {
            return new TranscriptDiagnosticResult(200, 0, null, DateTimeOffset.UtcNow);
        }

        var latest = transcripts[0];
        var content = await graphClient.Users[target.OrganizerUserId]
            .OnlineMeetings[target.MeetingResourceId]
            .Transcripts[latest.Id]
            .Content
            .GetAsync(
                configuration => configuration.Headers.Add("Accept", "text/vtt"),
                cancellationToken);

        if (content is null)
        {
            return new TranscriptDiagnosticResult(200, transcripts.Length, null, DateTimeOffset.UtcNow);
        }

        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new TranscriptDiagnosticResult(200, transcripts.Length, text.Length, DateTimeOffset.UtcNow);
    }
}

public sealed class MeetingTranscriptDiagnosticService(
    ActiveMeetingStore store,
    ITranscriptDiagnosticClient client,
    ILogger<MeetingTranscriptDiagnosticService> logger)
{
    public async Task<string> CheckAsync(string conversationId, CancellationToken cancellationToken)
    {
        if (!store.TryGet(conversationId, out var target) || target is null)
        {
            return "No active meeting was captured for this chat. Start a new meeting, wait for the bot's meeting-start card, and try `diagnose transcript` again.";
        }

        try
        {
            var result = await client.CheckAsync(target, cancellationToken);
            if (result.TranscriptCount == 0)
            {
                return $"Graph check at {result.CheckedAt:O}: `GET /transcripts` returned 200 OK with an empty collection. No transcript context is available through Graph right now.";
            }

            if (result.ContentCharacters is null)
            {
                return $"Graph check at {result.CheckedAt:O}: Graph listed {result.TranscriptCount} transcript resource(s), but the latest transcript content response was empty.";
            }

            return $"Graph check at {result.CheckedAt:O}: Graph listed {result.TranscriptCount} transcript resource(s), and the latest VTT content request returned {result.ContentCharacters.Value:N0} characters. No transcript text was posted to the chat.";
        }
        catch (ApiException exception)
        {
            logger.LogWarning(
                exception,
                "Mid-meeting transcript diagnostic returned Graph status {StatusCode} for conversation {ConversationId}.",
                exception.ResponseStatusCode,
                conversationId);
            return $"Graph check returned HTTP {exception.ResponseStatusCode}. No transcript context was supplied to the agent. See App Service logs for the correlated diagnostic error.";
        }
    }
}
