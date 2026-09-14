using BotMeetings.TranscriptIngestion;

namespace BotMeetings.Tests;

internal static class TestData
{
    public static TranscriptIngestionRequest Request() =>
        new(
            "tenant-1",
            "conversation-1",
            "meeting-1",
            "graph-meeting-1",
            "organizer-1",
            "Planning",
            new DateTimeOffset(2026, 8, 5, 17, 0, 0, TimeSpan.Zero));
}

internal sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

internal sealed class StubTranscriptProvider : ITranscriptProvider
{
    public TranscriptArtifact? Result { get; set; }
    public int CallCount { get; private set; }

    public Task<TranscriptArtifact?> GetLatestAsync(
        TranscriptIngestionRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Result);
    }
}

internal sealed class RecordingStore : ITranscriptIngestionStore
{
    public List<TranscriptIngestionJob> Updates { get; } = [];

    public Task EnqueueAsync(TranscriptIngestionRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<TranscriptIngestionJob>> GetDueJobsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranscriptIngestionJob>>([]);
    public Task UpdateAsync(TranscriptIngestionJob job, CancellationToken cancellationToken)
    {
        Updates.Add(job);
        return Task.CompletedTask;
    }
    public Task<TranscriptIngestionJob?> GetAsync(string tenantId, string meetingId, CancellationToken cancellationToken) =>
        Task.FromResult<TranscriptIngestionJob?>(null);
    public Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        Task.FromResult(0);
}

internal sealed class RecordingSink : ISourceDocumentSink
{
    public Dictionary<string, SourceDocument> Documents { get; } = [];
    public Task UpsertAsync(SourceDocument document, CancellationToken cancellationToken)
    {
        Documents[document.Id] = document;
        return Task.CompletedTask;
    }
}

internal sealed class StubSourceDocumentStore : ISourceDocumentStore
{
    public SourceDocument? Document { get; set; }
    public string? RequestedTenantId { get; private set; }
    public string? RequestedConversationId { get; private set; }

    public Task<SourceDocument?> GetLatestCompletedAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        RequestedTenantId = tenantId;
        RequestedConversationId = conversationId;
        return Task.FromResult(Document);
    }
}

internal sealed class RecordingNotificationSink : ITranscriptNotificationSink
{
    public int CompletedCount { get; private set; }
    public int UnavailableCount { get; private set; }

    public Task NotifyCompletedAsync(
        TranscriptIngestionRequest request,
        SourceDocument document,
        CancellationToken cancellationToken)
    {
        CompletedCount++;
        return Task.CompletedTask;
    }

    public Task NotifyUnavailableAsync(TranscriptIngestionRequest request, CancellationToken cancellationToken)
    {
        UnavailableCount++;
        return Task.CompletedTask;
    }
}