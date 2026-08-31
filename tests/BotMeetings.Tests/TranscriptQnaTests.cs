using BotMeetings.TranscriptIngestion;
using BotMeetings.TranscriptQna;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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
    public async Task Service_returns_waiting_notice_without_completed_transcript()
    {
        var store = new StubSourceDocumentStore();
        var generator = new StubAnswerGenerator("unused");
        var service = CreateService(store, generator);

        var result = await service.AnswerAsync("tenant-a", "conversation-a", "What was decided?", CancellationToken.None);

        Assert.Contains("don't have a completed transcript", result.Message);
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

    public void Dispose()
    {
        if (Directory.Exists(temporaryPath)) Directory.Delete(temporaryPath, true);
    }

    private TranscriptQuestionAnsweringService CreateService(
        ISourceDocumentStore store,
        ITranscriptAnswerGenerator generator) =>
        new(store, new TranscriptContextSelector(), generator, Options.Create(new TranscriptAgentOptions
        {
            Endpoint = "https://example.openai.azure.com",
            DeploymentName = "test",
            MaximumContextChunks = 2,
            MaximumQuestionCharacters = 1000,
            MaximumConcurrentAnswers = 2
        }));

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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "BotMeetings.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}