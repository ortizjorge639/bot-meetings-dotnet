using System.Text.RegularExpressions;
using BotMeetings.TranscriptIngestion;

namespace BotMeetings.TranscriptQna;

public sealed partial class TranscriptContextSelector
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "an", "and", "are", "did", "do", "for", "from", "how", "i", "in", "is",
        "it", "me", "of", "on", "or", "our", "the", "this", "to", "was", "we", "what", "when",
        "where", "which", "who", "why", "with", "you"
    };

    public IReadOnlyList<SourceChunk> Select(SourceDocument document, string question, int maximumChunks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var terms = Tokens().Matches(question)
            .Select(match => match.Value)
            .Where(term => term.Length > 1 && !StopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var scored = document.Chunks
            .Select(chunk => new
            {
                Chunk = chunk,
                Score = terms.Sum(term => CountOccurrences(chunk.Content, term)) +
                    terms.Count(term => chunk.Speakers.Contains(term, StringComparer.OrdinalIgnoreCase)) * 2
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.Index)
            .ToArray();
        if (scored.Length > maximumChunks && scored.All(item => item.Score == 0)) return [];

        var ranked = scored
            .Take(maximumChunks)
            .Select(item => item.Chunk)
            .OrderBy(chunk => chunk.Index)
            .ToArray();

        return ranked;
    }

    private static int CountOccurrences(string content, string term) =>
        Regex.Count(content, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"[\p{L}\p{N}']+", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();
}