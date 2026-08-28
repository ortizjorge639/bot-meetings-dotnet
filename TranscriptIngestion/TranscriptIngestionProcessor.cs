using Microsoft.Extensions.Options;
using Microsoft.Kiota.Abstractions;

namespace BotMeetings.TranscriptIngestion;

public sealed class TranscriptIngestionProcessor(
    ITranscriptProvider transcriptProvider,
    VttTranscriptParser parser,
    SourceDocumentBuilder documentBuilder,
    ITranscriptIngestionStore store,
    ISourceDocumentSink sink,
    ITranscriptNotificationSink notificationSink,
    IOptions<TranscriptIngestionOptions> options,
    ISystemClock clock,
    ILogger<TranscriptIngestionProcessor> logger)
{
    public async Task ProcessAsync(TranscriptIngestionJob job, CancellationToken cancellationToken)
    {
        if (job.Status != TranscriptIngestionStatus.Pending) return;

        var now = clock.UtcNow;
        if (now - job.CreatedAt >= options.Value.MaximumWait || job.Attempts >= options.Value.MaximumAttempts)
        {
            await MarkUnavailableAsync(
                job,
                "Transcript was not available within the configured polling window.",
                cancellationToken);
            return;
        }

        try
        {
            var artifact = await transcriptProvider.GetLatestAsync(job.Request, cancellationToken);
            if (artifact is null)
            {
                await ScheduleRetryAsync(job, "Transcript artifact is not available yet.", cancellationToken);
                return;
            }

            var segments = parser.Parse(artifact.VttContent);
            if (segments.Count == 0)
            {
                await ScheduleRetryAsync(job, "Transcript artifact did not contain any VTT cues.", cancellationToken);
                return;
            }

            var document = documentBuilder.Build(job.Request, artifact, segments);
            await sink.UpsertAsync(document, cancellationToken);
            await notificationSink.NotifyCompletedAsync(job.Request, document, cancellationToken);
            await store.UpdateAsync(
                job with
                {
                    Status = TranscriptIngestionStatus.Completed,
                    Attempts = job.Attempts + 1,
                    TranscriptId = artifact.Id,
                    DocumentId = document.Id,
                    LastError = null,
                    CompletedAt = clock.UtcNow
                },
                cancellationToken);

            logger.LogInformation(
                "Ingested transcript {TranscriptId} for meeting {MeetingId} as source document {DocumentId}.",
                artifact.Id,
                job.Request.MeetingId,
                document.Id);
        }
        catch (ApiException exception) when (IsRetryable(exception.ResponseStatusCode))
        {
            logger.LogWarning(exception, "Graph transcript retrieval for meeting {MeetingId} will be retried.", job.Request.MeetingId);
            await ScheduleRetryAsync(
                job,
                $"Microsoft Graph returned retryable status {exception.ResponseStatusCode}.",
                cancellationToken);
        }
        catch (ApiException exception)
        {
            await MarkFailedAsync(
                job,
                $"Microsoft Graph returned non-retryable status {exception.ResponseStatusCode}.",
                cancellationToken);
            logger.LogError(exception, "Graph transcript retrieval permanently failed for meeting {MeetingId}.", job.Request.MeetingId);
        }
        catch (InvalidDataException exception)
        {
            await MarkFailedAsync(job, exception.Message, cancellationToken);
            logger.LogError(exception, "Transcript normalization failed for meeting {MeetingId}.", job.Request.MeetingId);
        }
    }

    private async Task ScheduleRetryAsync(
        TranscriptIngestionJob job,
        string reason,
        CancellationToken cancellationToken)
    {
        var attempts = job.Attempts + 1;
        var now = clock.UtcNow;
        if (now - job.CreatedAt >= options.Value.MaximumWait || attempts >= options.Value.MaximumAttempts)
        {
            await MarkUnavailableAsync(job with { Attempts = attempts }, reason, cancellationToken);
            return;
        }

        await store.UpdateAsync(
            job with
            {
                Attempts = attempts,
                NextAttemptAt = now + options.Value.PollingInterval,
                LastError = reason
            },
            cancellationToken);
    }

    private async Task MarkUnavailableAsync(
        TranscriptIngestionJob job,
        string reason,
        CancellationToken cancellationToken)
    {
        await notificationSink.NotifyUnavailableAsync(job.Request, cancellationToken);
        await store.UpdateAsync(
            job with
            {
                Status = TranscriptIngestionStatus.Unavailable,
                LastError = reason,
                CompletedAt = clock.UtcNow
            },
            cancellationToken);
    }

    private async Task MarkFailedAsync(
        TranscriptIngestionJob job,
        string reason,
        CancellationToken cancellationToken)
    {
        await notificationSink.NotifyUnavailableAsync(job.Request, cancellationToken);
        await store.UpdateAsync(
            job with
            {
                Status = TranscriptIngestionStatus.Failed,
                Attempts = job.Attempts + 1,
                LastError = reason,
                CompletedAt = clock.UtcNow
            },
            cancellationToken);
    }

    private static bool IsRetryable(int statusCode) =>
        statusCode is 404 or 408 or 409 or 429 || statusCode >= 500;
}