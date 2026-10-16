using BotMeetings.TranscriptIngestion;
using BotMeetings.TranscriptQna;
using BotMeetings.LiveContext;
using BotMeetings.TranscriptDiagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotMeetings.Tests;

public sealed class TranscriptQnaTests : IDisposable
{
    private readonly string temporaryPath = Path.Combine(Path.GetTempPath(), $"bot-meetings-qna-{Guid.NewGuid():N}");

    [Fact]
    public void Selector_prioritizes_question_terms_and_speaker_names()
    {
        var document = CreateDocument(
            new SourceChunk("chunk-0", 0, TimeSpan.Zero, TimeSpan.FromSeconds(4), ["Jane"], "Jane: Welcome everyone."),
            new SourceChunk("chunk-1", 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), ["Bob"], "Bob: The launch decision is Friday."));

        var selected = new TranscriptContextSelector().Select(document, "What launch decision did Bob make?", 1);

        Assert.Equal("chunk-1", Assert.Single(selected).Id);
    }

    [Fact]
    public void Selector_returns_no_context_when_large_transcript_has_no_relevant_terms()
    {
        var document = CreateDocument(
            new SourceChunk("chunk-0", 0, TimeSpan.Zero, TimeSpan.FromSeconds(4), ["Jane"], "Jane: Welcome everyone."),
            new SourceChunk("chunk-1", 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), ["Bob"], "Bob: Goodbye everyone."));

        var selected = new TranscriptContextSelector().Select(document, "quasar nebula", 1);

        Assert.Empty(selected);
    }

    [Fact]
    public void Selector_returns_no_context_when_small_transcript_has_no_relevant_terms()
    {
        var document = CreateDocument(
            new SourceChunk("chunk-0", 0, TimeSpan.Zero, TimeSpan.FromSeconds(4), ["Jane"], "Jane: Welcome everyone."));

        var selected = new TranscriptContextSelector().Select(document, "quasar nebula", 50);

        Assert.Empty(selected);
    }

    [Fact]
    public void Live_context_key_validation_accepts_only_the_configured_key()
    {
        Assert.True(LiveContextKeyValidator.IsValid("expected-key", "expected-key"));
        Assert.False(LiveContextKeyValidator.IsValid("wrong-key", "expected-key"));
        Assert.False(LiveContextKeyValidator.IsValid(null, "expected-key"));
    }

    [Fact]
    public async Task Service_returns_waiting_notice_without_completed_transcript()
    {
        var store = new StubSourceDocumentStore();
        var generator = new StubAnswerGenerator("unused");
        var service = CreateService(store, generator);

        var result = await service.AnswerAsync("tenant-a", "conversation-a", "What was decided?", CancellationToken.None);

        Assert.Contains("don't have live or completed meeting context", result.Message);
        Assert.Empty(result.Citations);
        Assert.Equal(0, generator.CallCount);
        Assert.Equal("tenant-a", store.RequestedTenantId);
        Assert.Equal("conversation-a", store.RequestedConversationId);
    }

    [Fact]
    public async Task Service_rejects_oversized_questions_before_loading_transcript()
    {
        var store = new StubSourceDocumentStore();
        var generator = new StubAnswerGenerator("unused");
        var service = CreateService(store, generator);

        var result = await service.AnswerAsync("tenant-a", "conversation-a", new string('x', 1001), CancellationToken.None);

        Assert.Contains("under 1000 characters", result.Message);
        Assert.Null(store.RequestedTenantId);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public async Task Service_returns_grounded_answer_with_speaker_and_timestamp_source_note()
    {
        var store = new StubSourceDocumentStore
        {
            Document = CreateDocument(
                new SourceChunk("chunk-0", 0, TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(70), ["Jane"], "Jane: Ship on Friday."))
        };
        var service = CreateService(store, new StubAnswerGenerator("Jane said to ship Friday [S1]."));

        var result = await service.AnswerAsync("tenant-a", "conversation-a", "When should we ship?", CancellationToken.None);

        Assert.Contains("Jane said to ship Friday [S1].", result.Message);
        Assert.Contains("[S1] Jane — 00:01:05–00:01:10", result.Message);
        Assert.Equal(["S1"], result.Citations);
    }

    [Fact]
    public async Task Service_uses_only_same_tenant_active_meeting_live_context()
    {
        var completedStore = new StubSourceDocumentStore();
        var activeMeetings = new ActiveMeetingStore();
        activeMeetings.Set(
            "conversation-a",
            new ActiveMeetingTarget("resource", "organizer", "tenant-a", "meeting-live"));
        var liveStore = new StubLiveContextStore
        {
            Context = new LiveMeetingContext(
                "1.0",
                "tenant-a",
                "meeting-live",
                DateTimeOffset.Parse("2026-10-16T15:00:00Z"),
                DateTimeOffset.Parse("2026-10-16T15:00:02Z"),
                [
                    new LiveCaption(
                        "caption-1",
                        "speaker-1",
                        "Jane",
                        "The launch date is Friday.",
                        DateTimeOffset.Parse("2026-10-16T15:00:00Z"),
                        DateTimeOffset.Parse("2026-10-16T15:00:01Z"),
                        DateTimeOffset.Parse("2026-10-16T15:00:02Z"))
                ])
        };
        var service = CreateService(
            completedStore,
            new StubAnswerGenerator("Jane said the launch date is Friday [S1]."),
            liveContextStore: liveStore,
            activeMeetingStore: activeMeetings);

        var result = await service.AnswerAsync(
            "tenant-a",
            "conversation-a",
            "What is the launch date?",
            CancellationToken.None);

        Assert.Contains("launch date is Friday", result.Message);
        Assert.Equal("tenant-a", liveStore.RequestedTenantId);
        Assert.Equal("meeting-live", liveStore.RequestedMeetingId);
        Assert.Null(completedStore.RequestedTenantId);
    }

    [Fact]
    public async Task Service_does_not_read_another_tenants_active_live_context()
    {
        var completedStore = new StubSourceDocumentStore();
        var activeMeetings = new ActiveMeetingStore();
        activeMeetings.Set(
            "conversation-a",
            new ActiveMeetingTarget("resource", "organizer", "tenant-b", "meeting-live"));
        var liveStore = new StubLiveContextStore();
        var service = CreateService(
            completedStore,
            new StubAnswerGenerator("unused"),
            liveContextStore: liveStore,
            activeMeetingStore: activeMeetings);

        await service.AnswerAsync("tenant-a", "conversation-a", "What happened?", CancellationToken.None);

        Assert.Null(liveStore.RequestedTenantId);
        Assert.Equal("tenant-a", completedStore.RequestedTenantId);
    }

    [Fact]
    public async Task Service_times_out_slow_model_answers()
    {
        var store = new StubSourceDocumentStore
        {
            Document = CreateDocument(
                new SourceChunk("chunk-0", 0, TimeSpan.Zero, TimeSpan.FromSeconds(2), ["Jane"], "Jane: Approved."))
        };
        var service = CreateService(
            store,
            new NeverCompletingAnswerGenerator(),
            answerTimeout: TimeSpan.FromMilliseconds(25));

        var result = await service.AnswerAsync("tenant-a", "conversation-a", "Was it approved?", CancellationToken.None);

        Assert.Contains("timed out", result.Message);
        Assert.Empty(result.Citations);
    }

    [Fact]
    public async Task Service_rejects_excess_work_instead_of_waiting_without_bound()
    {
        var store = new StubSourceDocumentStore
        {
            Document = CreateDocument(
                new SourceChunk("chunk-0", 0, TimeSpan.Zero, TimeSpan.FromSeconds(2), ["Jane"], "Jane: Approved."))
        };
        var generator = new BlockingAnswerGenerator();
        var service = CreateService(
            store,
            generator,
            maximumConcurrentAnswers: 1,
            queueWaitTimeout: TimeSpan.FromMilliseconds(25));
        var firstAnswer = service.AnswerAsync("tenant-a", "conversation-a", "Was it approved?", CancellationToken.None);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var secondAnswer = await service.AnswerAsync(
            "tenant-a",
            "conversation-a",
            "Was it approved?",
            CancellationToken.None);
        generator.Release.SetResult();
        await firstAnswer;

        Assert.Contains("maximum number of questions", secondAnswer.Message);
        Assert.Empty(secondAnswer.Citations);
    }

    [Theory]
    [InlineData("An answer without evidence.")]
    [InlineData("An answer with a fabricated citation [S99].")]
    public async Task Service_rejects_missing_or_unknown_citations(string generatedAnswer)
    {
        var store = new StubSourceDocumentStore
        {
            Document = CreateDocument(
                new SourceChunk("chunk-0", 0, TimeSpan.Zero, TimeSpan.FromSeconds(2), ["Jane"], "Jane: Approved."))
        };
        var service = CreateService(store, new StubAnswerGenerator(generatedAnswer));

        var result = await service.AnswerAsync("tenant-a", "conversation-a", "Was it approved?", CancellationToken.None);

        Assert.Contains("couldn't produce a sufficiently grounded answer", result.Message);
        Assert.Empty(result.Citations);
    }

    [Fact]
    public async Task File_store_returns_only_latest_completed_document_for_tenant_and_conversation()
    {
        var store = CreateFileStore();
        var older = CreateDocument(
            new SourceChunk("older-chunk", 0, TimeSpan.Zero, TimeSpan.FromSeconds(2), ["Jane"], "Older")) with
        {
            Id = "document-older",
            TenantId = "tenant-a",
            MeetingId = "meeting-older",
            MeetingEndedAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z")
        };
        var latest = older with
        {
            Id = "document-latest",
            MeetingId = "meeting-latest",
            MeetingEndedAt = DateTimeOffset.Parse("2026-08-02T10:00:00Z")
        };
        var otherTenant = older with { Id = "document-other", TenantId = "tenant-b", MeetingId = "meeting-other" };

        await PersistCompletedAsync(store, older, "conversation-a");
        await PersistCompletedAsync(store, latest, "conversation-a");
        await PersistCompletedAsync(store, otherTenant, "conversation-a");

        var actual = await store.GetLatestCompletedAsync("tenant-a", "conversation-a", CancellationToken.None);

        Assert.Equal(latest.Id, actual?.Id);
        Assert.Null(await store.GetLatestCompletedAsync("tenant-a", "conversation-b", CancellationToken.None));
    }

    [Fact]
    public async Task File_store_purges_expired_jobs_and_documents()
    {
        var store = CreateFileStore();
        var document = CreateDocument(
            new SourceChunk("chunk-0", 0, TimeSpan.Zero, TimeSpan.FromSeconds(2), ["Jane"], "Jane: Approved."));
        await PersistCompletedAsync(store, document, "conversation-a");

        var purged = await store.PurgeExpiredAsync(
            document.MeetingEndedAt.AddDays(1),
            CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.Null(await store.GetAsync(document.TenantId, document.MeetingId, CancellationToken.None));
        Assert.Null(await store.GetLatestCompletedAsync(document.TenantId, "conversation-a", CancellationToken.None));
    }

    [Fact]
    public async Task Live_context_store_deduplicates_and_isolates_tenant_and_meeting()
    {
        var store = new FileLiveContextStore(
            Options.Create(new LiveContextOptions
            {
                DataPath = Path.Combine(temporaryPath, "live"),
                MaximumCaptionsPerMeeting = 10
            }),
            new TestHostEnvironment());
        var receivedAt = DateTimeOffset.UtcNow;
        var request = new CreateLiveCaptionRequest
        {
            MeetingId = "meeting-a",
            SpeakerId = "speaker-a",
            SpeakerDisplayName = "Jane",
            SpokenText = "The launch date is Friday.",
            Timestamp = receivedAt.AddSeconds(-1),
            ResultType = "Final"
        };

        var first = await store.AddAsync("tenant-a", request, receivedAt, CancellationToken.None);
        var duplicate = await store.AddAsync("tenant-a", request, receivedAt.AddSeconds(1), CancellationToken.None);

        Assert.False(first.Duplicate);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(first.Caption.Id, duplicate.Caption.Id);
        Assert.Single((await store.GetAsync("tenant-a", "meeting-a", CancellationToken.None))!.Captions);
        Assert.Null(await store.GetAsync("tenant-a", "meeting-b", CancellationToken.None));
        Assert.Null(await store.GetAsync("tenant-b", "meeting-a", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryPath)) Directory.Delete(temporaryPath, true);
    }

    private TranscriptQuestionAnsweringService CreateService(
        ISourceDocumentStore store,
        ITranscriptAnswerGenerator generator,
        int maximumConcurrentAnswers = 2,
        TimeSpan? queueWaitTimeout = null,
        TimeSpan? answerTimeout = null,
        ILiveContextStore? liveContextStore = null,
        ActiveMeetingStore? activeMeetingStore = null) =>
        new(
            store,
            liveContextStore ?? new EmptyLiveContextStore(),
            activeMeetingStore ?? new ActiveMeetingStore(),
            new TranscriptContextSelector(),
            generator,
            Options.Create(new TranscriptAgentOptions
            {
                Endpoint = "https://example.openai.azure.com",
                DeploymentName = "test",
                MaximumContextChunks = 2,
                MaximumQuestionCharacters = 1000,
                MaximumConcurrentAnswers = maximumConcurrentAnswers,
                QueueWaitTimeout = queueWaitTimeout ?? TimeSpan.FromSeconds(5),
                AnswerTimeout = answerTimeout ?? TimeSpan.FromMinutes(2)
            }),
            Options.Create(new LiveContextOptions { Enabled = true }),
            NullLogger<TranscriptQuestionAnsweringService>.Instance);

    private FileTranscriptStore CreateFileStore() =>
        new(
            Options.Create(new TranscriptIngestionOptions { DataPath = temporaryPath }),
            new TestHostEnvironment());

    private static async Task PersistCompletedAsync(
        FileTranscriptStore store,
        SourceDocument document,
        string conversationId)
    {
        await store.UpsertAsync(document, CancellationToken.None);
        var request = TestData.Request() with
        {
            TenantId = document.TenantId,
            ConversationId = conversationId,
            MeetingId = document.MeetingId,
            MeetingEndedAt = document.MeetingEndedAt
        };
        await store.EnqueueAsync(request, CancellationToken.None);
        var job = await store.GetAsync(request.TenantId, request.MeetingId, CancellationToken.None);
        await store.UpdateAsync(job! with
        {
            Status = TranscriptIngestionStatus.Completed,
            DocumentId = document.Id,
            CompletedAt = document.MeetingEndedAt
        }, CancellationToken.None);
    }

    private static SourceDocument CreateDocument(params SourceChunk[] chunks) =>
        new(
            "1.0",
            "document-1",
            "tenant-a",
            "meeting:meeting-1",
            "teams-transcript",
            "meeting-1",
            "transcript-1",
            "Planning",
            DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-01T10:01:00Z"),
            "hash",
            string.Join("\n", chunks.Select(chunk => chunk.Content)),
            [],
            chunks);

    private sealed class StubAnswerGenerator(string answer) : ITranscriptAnswerGenerator
    {
        public int CallCount { get; private set; }

        public Task<string> GenerateAsync(
            string question,
            IReadOnlyList<GroundingSource> sources,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(answer);
        }
    }

    private sealed class EmptyLiveContextStore : ILiveContextStore
    {
        public Task<LiveCaptionWriteResult> AddAsync(
            string tenantId,
            CreateLiveCaptionRequest request,
            DateTimeOffset receivedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LiveMeetingContext?> GetAsync(
            string tenantId,
            string meetingId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LiveMeetingContext?>(null);
    }

    private sealed class StubLiveContextStore : ILiveContextStore
    {
        public LiveMeetingContext? Context { get; init; }
        public string? RequestedTenantId { get; private set; }
        public string? RequestedMeetingId { get; private set; }

        public Task<LiveCaptionWriteResult> AddAsync(
            string tenantId,
            CreateLiveCaptionRequest request,
            DateTimeOffset receivedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LiveMeetingContext?> GetAsync(
            string tenantId,
            string meetingId,
            CancellationToken cancellationToken)
        {
            RequestedTenantId = tenantId;
            RequestedMeetingId = meetingId;
            return Task.FromResult(Context);
        }
    }

    private sealed class NeverCompletingAnswerGenerator : ITranscriptAnswerGenerator
    {
        public async Task<string> GenerateAsync(
            string question,
            IReadOnlyList<GroundingSource> sources,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return string.Empty;
        }
    }

    private sealed class BlockingAnswerGenerator : ITranscriptAnswerGenerator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> GenerateAsync(
            string question,
            IReadOnlyList<GroundingSource> sources,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return "Jane approved it [S1].";
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "BotMeetings.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}