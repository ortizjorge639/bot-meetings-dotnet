using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace BotMeetings.TranscriptIngestion;

public sealed class SourceDocumentBuilder(IOptions<TranscriptIngestionOptions> options, ISystemClock clock)
{
    private const string LineSeparator = "\n";

    public SourceDocument Build(
        TranscriptIngestionRequest request,
        TranscriptArtifact artifact,
        IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(segments);

        var documentId = $"teams-transcript:{Hash($"{request.TenantId}|{request.MeetingId}|{artifact.Id}")}";
        var content = string.Join(LineSeparator, segments.Select(FormatSegment));
        var chunks = BuildChunks(documentId, segments, options.Value.MaximumChunkCharacters);

        return new SourceDocument(
            "1.0",
            documentId,
            request.TenantId,
            $"meeting:{request.MeetingId}",
            "teams-transcript",
            request.MeetingId,
            artifact.Id,
            request.MeetingTitle,
            request.MeetingEndedAt,
            clock.UtcNow,
            Hash(content),
            content,
            segments,
            chunks);
    }

    private static IReadOnlyList<SourceChunk> BuildChunks(
        string documentId,
        IReadOnlyList<TranscriptSegment> segments,
        int maximumCharacters)
    {
        var chunks = new List<SourceChunk>();
        var current = new List<TranscriptSegment>();
        var currentLength = 0;

        foreach (var segment in segments)
        {
            var formatted = FormatSegment(segment);
            if (current.Count > 0 && currentLength + LineSeparator.Length + formatted.Length > maximumCharacters)
            {
                chunks.Add(CreateChunk(documentId, chunks.Count, current));
                current = [];
                currentLength = 0;
            }

            current.Add(segment);
            currentLength += (currentLength == 0 ? 0 : LineSeparator.Length) + formatted.Length;
        }

        if (current.Count > 0) chunks.Add(CreateChunk(documentId, chunks.Count, current));
        return chunks;
    }

    private static SourceChunk CreateChunk(string documentId, int index, IReadOnlyList<TranscriptSegment> segments)
    {
        var content = string.Join(LineSeparator, segments.Select(FormatSegment));
        var speakers = segments
            .Select(segment => segment.Speaker)
            .Where(speaker => !string.IsNullOrWhiteSpace(speaker))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new SourceChunk(
            $"{documentId}:chunk:{index:D4}",
            index,
            segments[0].Start,
            segments[^1].End,
            speakers,
            content);
    }

    private static string FormatSegment(TranscriptSegment segment) =>
        $"[{(int)segment.Start.TotalHours:D2}:{segment.Start.Minutes:D2}:{segment.Start.Seconds:D2}.{segment.Start.Milliseconds:D3}] " +
        $"{(string.IsNullOrWhiteSpace(segment.Speaker) ? string.Empty : $"{segment.Speaker}: ")}{segment.Text}";

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}