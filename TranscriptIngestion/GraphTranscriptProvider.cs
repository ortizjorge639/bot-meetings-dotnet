using Microsoft.Graph;

namespace BotMeetings.TranscriptIngestion;

public sealed class GraphTranscriptProvider(GraphServiceClient graphClient) : ITranscriptProvider
{
    public async Task<TranscriptArtifact?> GetLatestAsync(
        TranscriptIngestionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MeetingResourceId) ||
            string.IsNullOrWhiteSpace(request.OrganizerUserId))
        {
            return null;
        }

        var response = await graphClient.Users[request.OrganizerUserId]
            .OnlineMeetings[request.MeetingResourceId]
            .Transcripts
            .GetAsync(cancellationToken: cancellationToken);

        var transcript = response?.Value?
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .OrderByDescending(item => item.CreatedDateTime)
            .FirstOrDefault();

        if (transcript?.Id is null)
        {
            return null;
        }

        var content = await graphClient.Users[request.OrganizerUserId]
            .OnlineMeetings[request.MeetingResourceId]
            .Transcripts[transcript.Id]
            .Content
            .GetAsync(
                configuration => configuration.Headers.Add("Accept", "text/vtt"),
                cancellationToken);

        if (content is null)
        {
            return null;
        }

        using var reader = new StreamReader(content);
        var vtt = await reader.ReadToEndAsync(cancellationToken);
        return new TranscriptArtifact(transcript.Id, transcript.CreatedDateTime, vtt);
    }
}