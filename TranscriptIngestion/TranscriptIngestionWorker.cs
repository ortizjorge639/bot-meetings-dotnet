using Microsoft.Extensions.Options;

namespace BotMeetings.TranscriptIngestion;

public sealed class TranscriptIngestionWorker(
    ITranscriptIngestionStore store,
    TranscriptIngestionProcessor processor,
    IOptions<TranscriptIngestionOptions> options,
    ISystemClock clock,
    ITranscriptNotificationSink notificationSink,
    ILogger<TranscriptIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollingInterval);
        var nextPurgeAt = DateTimeOffset.MinValue;
        do
        {
            if (clock.UtcNow >= nextPurgeAt)
            {
                try
                {
                    var purged = await store.PurgeExpiredAsync(
                        clock.UtcNow - options.Value.RetentionPeriod,
                        stoppingToken);
                    if (purged > 0) logger.LogInformation("Purged {JobCount} expired transcript jobs and documents.", purged);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Transcript retention cleanup failed; the worker will retry later.");
                }

                nextPurgeAt = clock.UtcNow + options.Value.PurgeInterval;
            }

            var jobs = await store.GetDueJobsAsync(clock.UtcNow, stoppingToken);
            foreach (var job in jobs)
            {
                try
                {
                    await processor.ProcessAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Transcript ingestion failed for meeting {MeetingId}; the pending job will be retried.",
                        job.Request.MeetingId);
                    var attempts = job.Attempts + 1;
                    var terminal = attempts >= options.Value.MaximumAttempts;
                    if (terminal)
                    {
                        await notificationSink.NotifyUnavailableAsync(job.Request, stoppingToken);
                    }
                    await store.UpdateAsync(
                        job with
                        {
                            Status = terminal
                                ? TranscriptIngestionStatus.Failed
                                : TranscriptIngestionStatus.Pending,
                            Attempts = attempts,
                            NextAttemptAt = clock.UtcNow + options.Value.PollingInterval,
                            LastError = "Unexpected ingestion failure. See application logs for details.",
                            CompletedAt = terminal ? clock.UtcNow : null
                        },
                        stoppingToken);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}