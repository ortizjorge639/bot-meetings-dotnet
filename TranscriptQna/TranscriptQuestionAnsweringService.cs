using System.Text.RegularExpressions;
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
    TranscriptContextSelector selector,
    ITranscriptAnswerGenerator generator,
    IOptions<TranscriptAgentOptions> options)
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

        var document = await store.GetLatestCompletedAsync(tenantId, conversationId, cancellationToken);
        if (document is null)
        {
            return new TranscriptAnswerResult(
                "I don't have a completed transcript for this chat yet. I'll post a notice here when I'm ready for meeting questions.",
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

    private static string FormatSpeakers(SourceChunk chunk) =>
        chunk.Speakers.Count == 0 ? "Unknown speaker" : string.Join(", ", chunk.Speakers);

    private static string FormatTimestamp(TimeSpan value) =>
        $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";

    [GeneratedRegex(@"\[(S\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationPattern();
}