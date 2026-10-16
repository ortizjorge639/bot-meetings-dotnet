using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BotMeetings.LiveContext;

/// <summary>Payload for persisting a final live caption.</summary>
public sealed record CreateLiveCaptionRequest
{
    [Required, MaxLength(256)]
    public required string MeetingId { get; init; }

    [Required, MaxLength(256)]
    public required string SpeakerId { get; init; }

    [Required, MaxLength(200)]
    public required string SpeakerDisplayName { get; init; }

    [Required, MaxLength(4000)]
    public required string SpokenText { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    [Required, MaxLength(16)]
    public required string ResultType { get; init; }
}

/// <summary>Result of persisting a final live caption.</summary>
public sealed record LiveCaptionReceiptResponse(
    bool Accepted,
    bool Duplicate,
    string CaptionId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset PersistedAt);

/// <summary>ACS identity and short-lived VoIP token for the browser client.</summary>
public sealed record AcsTokenResponse(
    string UserId,
    string Token,
    DateTimeOffset ExpiresOn);

/// <summary>Payload for resolving a Teams join link to an active meeting.</summary>
public sealed record ResolveActiveMeetingRequest
{
    [Required, MaxLength(2048)]
    public required string MeetingLink { get; init; }
}

/// <summary>Internal identifier for an active meeting.</summary>
public sealed record ActiveMeetingResponse(string MeetingId);

public sealed record LiveCaption(
    string Id,
    string SpeakerId,
    string SpeakerDisplayName,
    string SpokenText,
    DateTimeOffset SpokenAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset PersistedAt);

public sealed record LiveMeetingContext(
    string SchemaVersion,
    string TenantId,
    string MeetingId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<LiveCaption> Captions);

public sealed record LiveCaptionWriteResult(LiveCaption Caption, bool Duplicate);

[JsonSerializable(typeof(LiveMeetingContext))]
internal sealed partial class LiveContextJsonSerializerContext : JsonSerializerContext;
