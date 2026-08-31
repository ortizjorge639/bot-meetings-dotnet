namespace BotMeetings.TranscriptIngestion;

public interface ITranscriptProvider
{
    Task<TranscriptArtifact?> GetLatestAsync(
        TranscriptIngestionRequest request,
        CancellationToken cancellationToken);
}

public interface ITranscriptIngestionStore
{
    Task EnqueueAsync(TranscriptIngestionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TranscriptIngestionJob>> GetDueJobsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task UpdateAsync(TranscriptIngestionJob job, CancellationToken cancellationToken);
    Task<TranscriptIngestionJob?> GetAsync(string tenantId, string meetingId, CancellationToken cancellationToken);
}

public interface ISourceDocumentSink
{
    Task UpsertAsync(SourceDocument document, CancellationToken cancellationToken);
}

public interface ISourceDocumentStore
{
    Task<SourceDocument?> GetLatestCompletedAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken);
}

public interface ITranscriptNotificationSink
{
    Task NotifyCompletedAsync(
        TranscriptIngestionRequest request,
        SourceDocument document,
        CancellationToken cancellationToken);

    Task NotifyUnavailableAsync(
        TranscriptIngestionRequest request,
        CancellationToken cancellationToken);
}

public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}