using System.Text.RegularExpressions;
using BotMeetings.LiveContext;
using BotMeetings.TranscriptDiagnostics;
using BotMeetings.TranscriptIngestion;
using Microsoft.Extensions.Options;

namespace BotMeetings.TranscriptQna;

public interface ITranscriptAnswerGenerator
{
    Task<string> GenerateAsync(string question, IReadOnlyList<GroundingSource> sources, CancellationToken cancellationToken);
}

public sealed record GroundingSource(string Citation, SourceChunk Chunk);
public sealed record TranscriptAnswerResult(string Message, IReadOnlyList<string> Citations);

public sealed partial class TranscriptQuestionAnsweringService(
    ISourceDocumentStore store,
    ILiveContextStore liveContextStore,
    ActiveMeetingStore activeMeetingStore,
    TranscriptContextSelector selector,
    ITranscriptAnswerGenerator generator,
    IOptions<TranscriptAgentOptions> options,
    IOptions<LiveContextOptions> liveContextOptions,
    ILogger<TranscriptQuestionAnsweringService> logger)
{
    private readonly SemaphoreSlim answerGate = new(options.Value.MaximumConcurrentAnswers);

    public async Task<TranscriptAnswerResult> AnswerAsync(
        string tenantId,
        string conversationId,
        string question,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        if (question.Length > options.Value.MaximumQuestionCharacters)
        {
            return new TranscriptAnswerResult(
                $"Please keep the question under {options.Value.MaximumQuestionCharacters} characters.",
                []);
        }

        var document = await GetDocumentAsync(tenantId, conversationId, cancellationToken);
        if (document is null)
        {
            return new TranscriptAnswerResult(
                "I don't have live or completed meeting context for this chat yet. I'll post a notice here when completed context is ready.",
                []);
        }

        var chunks = selector.Select(document, question, options.Value.MaximumContextChunks);
        if (chunks.Count == 0)
        {
            return new TranscriptAnswerResult("I couldn't find transcript material to answer that question.", []);
        }

        var sources = chunks.Select((chunk, index) => new GroundingSource($"S{index + 1}", chunk)).ToArray();
        if (!await answerGate.WaitAsync(options.Value.QueueWaitTimeout, cancellationToken))
        {
            return new TranscriptAnswerResult(
                "I'm handling the maximum number of questions right now. Please try again in a moment.",
                []);
        }

        string answer;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.AnswerTimeout);
            try
            {
                answer = await generator.GenerateAsync(question, sources, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new TranscriptAnswerResult(
                    "The transcript answer timed out. Please try a more specific question.",
                    []);
            }
        }
        finally
        {
            answerGate.Release();
        }
        var allowed = sources.Select(source => source.Citation).ToHashSet(StringComparer.Ordinal);
        var citations = CitationPattern().Matches(answer)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (citations.Length == 0 || citations.Any(citation => !allowed.Contains(citation)))
        {
            return new TranscriptAnswerResult(
                "I couldn't produce a sufficiently grounded answer from this meeting transcript. Try asking more specifically about a speaker, topic, or decision.",
                []);
        }

        var citedSources = sources.Where(source => citations.Contains(source.Citation, StringComparer.Ordinal));
        var sourceNotes = string.Join(
            "\n",
            citedSources.Select(source =>
                $"- [{source.Citation}] {FormatSpeakers(source.Chunk)} — " +
                $"{FormatTimestamp(source.Chunk.Start)}–{FormatTimestamp(source.Chunk.End)}"));
        return new TranscriptAnswerResult($"{answer.Trim()}\n\n**Transcript sources**\n{sourceNotes}", citations);
    }

    private async Task<SourceDocument?> GetDocumentAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (liveContextOptions.Value.Enabled &&
            activeMeetingStore.TryGet(conversationId, out var activeMeeting) &&
            activeMeeting is not null &&
            !string.IsNullOrWhiteSpace(activeMeeting.MeetingId) &&
            string.Equals(activeMeeting.TenantId, tenantId, StringComparison.Ordinal))
        {
            var liveContext = await liveContextStore.GetAsync(
                tenantId,
                activeMeeting.MeetingId,
                cancellationToken);
            if (liveContext?.Captions.Count > 0)
            {
                var queryAt = DateTimeOffset.UtcNow;
                var latest = liveContext.Captions.MaxBy(caption => caption.PersistedAt)!;
                logger.LogInformation(
                    "Using live context for meeting {MeetingId}; latest caption spoken {SpokenAt}, received {ReceivedAt}, persisted {PersistedAt}, queried {QueriedAt}.",
                    liveContext.MeetingId,
                    latest.SpokenAt,
                    latest.ReceivedAt,
                    latest.PersistedAt,
                    queryAt);
                return CreateSourceDocument(liveContext, queryAt);
            }
        }

        return await store.GetLatestCompletedAsync(tenantId, conversationId, cancellationToken);
    }

    private static SourceDocument CreateSourceDocument(
        LiveMeetingContext context,
        DateTimeOffset queryAt)
    {
        var firstTimestamp = context.Captions[0].SpokenAt;
        var chunks = context.Captions.Select((caption, index) =>
        {
            var start = caption.SpokenAt - firstTimestamp;
            return new SourceChunk(
                $"live-caption:{caption.Id}",
                index,
                start,
                start,
                [caption.SpeakerDisplayName],
                $"[{caption.SpokenAt:O}] {caption.SpeakerDisplayName}: {caption.SpokenText}");
        }).ToArray();
        return new SourceDocument(
            "1.0",
            $"live-context:{context.TenantId}:{context.MeetingId}",
            context.TenantId,
            $"meeting:{context.MeetingId}",
            "live-captions",
            context.MeetingId,
            $"live:{context.UpdatedAt:O}",
            null,
            queryAt,
            context.UpdatedAt,
            string.Empty,
            string.Join("\n", chunks.Select(chunk => chunk.Content)),
            [],
            chunks);
    }

    private static string FormatSpeakers(SourceChunk chunk) =>
        chunk.Speakers.Count == 0 ? "Unknown speaker" : string.Join(", ", chunk.Speakers);

    private static string FormatTimestamp(TimeSpan value) =>
        $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";

    [GeneratedRegex(@"\[(S\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationPattern();
}