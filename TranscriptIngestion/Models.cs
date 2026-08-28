using System.Text.Json.Serialization;

namespace BotMeetings.TranscriptIngestion;

public enum TranscriptIngestionStatus
{
    Pending,
    Completed,
    Unavailable,
    Failed
}

public sealed record TranscriptIngestionRequest(
    string TenantId,
    string ConversationId,
    string MeetingId,
    string MeetingResourceId,
    string OrganizerUserId,
    string? MeetingTitle,
    DateTimeOffset MeetingEndedAt);

public sealed record TranscriptIngestionJob(
    TranscriptIngestionRequest Request,
    TranscriptIngestionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    int Attempts,
    string? TranscriptId = null,
    string? DocumentId = null,
    string? LastError = null,
    DateTimeOffset? CompletedAt = null);

public sealed record TranscriptArtifact(string Id, DateTimeOffset? CreatedAt, string VttContent);
public sealed record TranscriptSegment(int Index, TimeSpan Start, TimeSpan End, string? Speaker, string Text);
public sealed record SourceChunk(
    string Id,
    int Index,
    TimeSpan Start,
    TimeSpan End,
    IReadOnlyList<string> Speakers,
    string Content);

public sealed record SourceDocument(
    string SchemaVersion,
    string Id,
    string TenantId,
    string ScopeId,
    string SourceType,
    string MeetingId,
    string TranscriptId,
    string? Title,
    DateTimeOffset MeetingEndedAt,
    DateTimeOffset IngestedAt,
    string ContentHash,
    string Content,
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<SourceChunk> Chunks);

[JsonSerializable(typeof(TranscriptIngestionJob))]
[JsonSerializable(typeof(SourceDocument))]
internal sealed partial class TranscriptJsonSerializerContext : JsonSerializerContext;