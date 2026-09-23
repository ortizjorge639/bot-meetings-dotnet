using BotMeetings.TranscriptDiagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotMeetings.Tests;

public sealed class MeetingTranscriptDiagnosticTests
{
    [Fact]
    public async Task Check_reports_when_no_active_meeting_was_captured()
    {
        var service = CreateService(new TranscriptDiagnosticResult(200, 0, null, DateTimeOffset.UtcNow));

        var message = await service.CheckAsync("conversation-1", CancellationToken.None);

        Assert.Contains("No active meeting was captured", message);
    }

    [Fact]
    public async Task Check_reports_an_empty_Graph_collection()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero);
        var store = new ActiveMeetingStore();
        store.Set("conversation-1", new ActiveMeetingTarget("meeting-1", "organizer-1"));
        var service = CreateService(
            new TranscriptDiagnosticResult(200, 0, null, checkedAt),
            store);

        var message = await service.CheckAsync("conversation-1", CancellationToken.None);

        Assert.Contains("200 OK with an empty collection", message);
        Assert.Contains("No transcript context is available", message);
    }

    [Fact]
    public async Task Check_reports_available_content_without_returning_it()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero);
        var store = new ActiveMeetingStore();
        store.Set("conversation-1", new ActiveMeetingTarget("meeting-1", "organizer-1"));
        var service = CreateService(
            new TranscriptDiagnosticResult(200, 1, 1234, checkedAt),
            store);

        var message = await service.CheckAsync("conversation-1", CancellationToken.None);

        Assert.Contains("1 transcript resource", message);
        Assert.Contains("1,234 characters", message);
        Assert.Contains("No transcript text was posted", message);
    }

    private static MeetingTranscriptDiagnosticService CreateService(
        TranscriptDiagnosticResult result,
        ActiveMeetingStore? store = null) =>
        new(
            store ?? new ActiveMeetingStore(),
            new StubDiagnosticClient(result),
            NullLogger<MeetingTranscriptDiagnosticService>.Instance);

    private sealed class StubDiagnosticClient(TranscriptDiagnosticResult result) : ITranscriptDiagnosticClient
    {
        public Task<TranscriptDiagnosticResult> CheckAsync(
            ActiveMeetingTarget target,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
