using BotMeetings.TranscriptIngestion;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotMeetings.Tests;

public sealed class TranscriptIngestionTests : IDisposable
{
    private readonly string temporaryPath = Path.Combine(Path.GetTempPath(), $"bot-meetings-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Processor_retries_when_transcript_is_not_available()
    {
        var fixture = new ProcessorFixture();

        await fixture.Processor.ProcessAsync(fixture.PendingJob(), CancellationToken.None);

        var updated = Assert.Single(fixture.Store.Updates);
        Assert.Equal(TranscriptIngestionStatus.Pending, updated.Status);
        Assert.Equal(1, updated.Attempts);
        Assert.Equal(fixture.Clock.UtcNow.AddSeconds(5), updated.NextAttemptAt);
        Assert.Empty(fixture.Sink.Documents);
        Assert.Equal(0, fixture.Notifications.CompletedCount);
    }

    [Fact]
    public async Task Processor_persists_context_notifies_Teams_and_completes_job()
    {
        var fixture = new ProcessorFixture();
        fixture.Provider.Result = new TranscriptArtifact(
            "transcript-1",
            fixture.Clock.UtcNow,
            """
            WEBVTT

            00:01.000 --> 00:02.000
            <v Jane>Approved.</v>
            """);

        await fixture.Processor.ProcessAsync(fixture.PendingJob(), CancellationToken.None);

        var document = Assert.Single(fixture.Sink.Documents).Value;
        Assert.Equal("meeting:meeting-1", document.ScopeId);
        Assert.Contains("Jane: Approved.", document.Content);
        Assert.Equal(1, fixture.Notifications.CompletedCount);
        var updated = Assert.Single(fixture.Store.Updates);
        Assert.Equal(TranscriptIngestionStatus.Completed, updated.Status);
        Assert.Equal(document.Id, updated.DocumentId);
    }

    [Fact]
    public async Task Processor_notifies_unavailable_at_attempt_limit()
    {
        var fixture = new ProcessorFixture(maximumAttempts: 2);

        await fixture.Processor.ProcessAsync(
            fixture.PendingJob() with { Attempts = 2 },
            CancellationToken.None);

        var updated = Assert.Single(fixture.Store.Updates);
        Assert.Equal(TranscriptIngestionStatus.Unavailable, updated.Status);
        Assert.Equal(1, fixture.Notifications.UnavailableCount);
        Assert.Equal(0, fixture.Provider.CallCount);
    }

    [Theory]
    [InlineData(TranscriptIngestionStatus.Pending)]
    [InlineData(TranscriptIngestionStatus.Completed)]
    [InlineData(TranscriptIngestionStatus.Unavailable)]
    [InlineData(TranscriptIngestionStatus.Failed)]
    public async Task Store_deduplicates_repeated_meeting_end_events(TranscriptIngestionStatus status)
    {
        var store = CreateStore();
        var request = TestData.Request();
        await store.EnqueueAsync(request, CancellationToken.None);
        var pending = Assert.Single(await store.GetDueJobsAsync(DateTimeOffset.MaxValue, CancellationToken.None));
        await store.UpdateAsync(pending with { Status = status }, CancellationToken.None);

        await store.EnqueueAsync(request, CancellationToken.None);

        var stored = await store.GetAsync(request.TenantId, request.MeetingId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(status, stored.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryPath)) Directory.Delete(temporaryPath, true);
    }

    private FileTranscriptStore CreateStore() =>
        new(
            Options.Create(new TranscriptIngestionOptions { DataPath = temporaryPath }),
            new TestHostEnvironment());

    private sealed class ProcessorFixture
    {
        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 8, 5, 18, 0, 0, TimeSpan.Zero));
        public StubTranscriptProvider Provider { get; } = new();
        public RecordingStore Store { get; } = new();
        public RecordingSink Sink { get; } = new();
        public RecordingNotificationSink Notifications { get; } = new();
        public TranscriptIngestionProcessor Processor { get; }

        public ProcessorFixture(int maximumAttempts = 5)
        {
            var options = Options.Create(new TranscriptIngestionOptions
            {
                PollingInterval = TimeSpan.FromSeconds(5),
                MaximumWait = TimeSpan.FromMinutes(30),
                MaximumAttempts = maximumAttempts
            });
            Processor = new TranscriptIngestionProcessor(
                Provider,
                new VttTranscriptParser(),
                new SourceDocumentBuilder(options, Clock),
                Store,
                Sink,
                Notifications,
                options,
                Clock,
                NullLogger<TranscriptIngestionProcessor>.Instance);
        }

        public TranscriptIngestionJob PendingJob() =>
            new(TestData.Request(), TranscriptIngestionStatus.Pending, Clock.UtcNow, Clock.UtcNow, 0);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "BotMeetings.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}