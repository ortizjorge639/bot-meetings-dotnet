using Microsoft.Extensions.Options;

namespace BotMeetings.TranscriptIngestion;

public sealed class TranscriptIngestionWorker(
    ITranscriptIngestionStore store,
    TranscriptIngestionProcessor processor,
    IOptions<TranscriptIngestionOptions> options,
    ISystemClock clock,
    ILogger<TranscriptIngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollingInterval);
        do
        {
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
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}