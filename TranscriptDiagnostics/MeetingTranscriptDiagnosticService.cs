using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotMeetings.LiveContext;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Extensions.Options;

namespace BotMeetings.TranscriptDiagnostics;

public sealed record ActiveMeetingTarget(
    string MeetingResourceId,
    string OrganizerUserId,
    string? TenantId = null,
    string? MeetingId = null,
    string? JoinUrl = null,
    DateTimeOffset? CapturedAt = null);

public sealed record TranscriptDiagnosticResult(
    int StatusCode,
    int TranscriptCount,
    int? ContentCharacters,
    DateTimeOffset CheckedAt);

public interface ITranscriptDiagnosticClient
{
    Task<TranscriptDiagnosticResult> CheckAsync(
        ActiveMeetingTarget target,
        CancellationToken cancellationToken);
}

public sealed class ActiveMeetingStore
{
    private readonly ConcurrentDictionary<string, ActiveMeetingTarget> meetings = new(StringComparer.Ordinal);
    private readonly object persistenceGate = new();
    private readonly string? persistencePath;
    private readonly TimeSpan? restoreMaximumAge;

    public ActiveMeetingStore()
    {
    }

    public ActiveMeetingStore(
        IOptions<LiveContextOptions> options,
        IHostEnvironment environment)
    {
        if (!options.Value.Enabled) return;

        var rootPath = Path.IsPathRooted(options.Value.DataPath)
            ? options.Value.DataPath
            : Path.Combine(environment.ContentRootPath, options.Value.DataPath);
        persistencePath = Path.Combine(rootPath, "active-meetings.json");
        restoreMaximumAge = TimeSpan.FromHours(options.Value.MaximumCaptionAgeHours);
        Restore(DateTimeOffset.UtcNow);
    }

    public void Set(string conversationId, ActiveMeetingTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(target);
        var timestampedTarget = target with { CapturedAt = target.CapturedAt ?? DateTimeOffset.UtcNow };
        lock (persistenceGate)
        {
            var hadPrevious = meetings.TryGetValue(conversationId, out var previous);
            meetings[conversationId] = timestampedTarget;
            try
            {
                Persist();
            }
            catch
            {
                if (hadPrevious)
                {
                    meetings[conversationId] = previous!;
                }
                else
                {
                    meetings.TryRemove(conversationId, out _);
                }

                throw;
            }
        }
    }

    public bool TryGet(string conversationId, out ActiveMeetingTarget? target)
    {
        lock (persistenceGate)
        {
            PruneExpired(DateTimeOffset.UtcNow);
            return meetings.TryGetValue(conversationId, out target);
        }
    }

    public bool TryResolve(
        string tenantId,
        string joinUrl,
        out ActiveMeetingTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(tenantId) ||
            !TryNormalizeJoinUrl(joinUrl, out var normalizedJoinUrl))
        {
            return false;
        }

        lock (persistenceGate)
        {
            PruneExpired(DateTimeOffset.UtcNow);
            foreach (var candidate in meetings.Values)
            {
                if (!string.Equals(candidate.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(candidate.MeetingId) ||
                    !TryNormalizeJoinUrl(candidate.JoinUrl, out var candidateJoinUrl) ||
                    !string.Equals(candidateJoinUrl, normalizedJoinUrl, StringComparison.Ordinal))
                {
                    continue;
                }

                target = candidate;
                return true;
            }
        }

        return false;
    }

    public bool IsActive(string tenantId, string meetingId) =>
        TryGetActive(tenantId, meetingId, out _);

    public bool TryGetActive(
        string tenantId,
        string meetingId,
        out ActiveMeetingTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(meetingId))
        {
            return false;
        }

        lock (persistenceGate)
        {
            PruneExpired(DateTimeOffset.UtcNow);
            foreach (var candidate in meetings.Values)
            {
                if (string.Equals(candidate.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.MeetingId, meetingId, StringComparison.Ordinal))
                {
                    target = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    public bool Remove(string conversationId, string expectedMeetingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMeetingId);
        lock (persistenceGate)
        {
            PruneExpired(DateTimeOffset.UtcNow);
            if (!meetings.TryGetValue(conversationId, out var current) ||
                !string.Equals(current.MeetingId, expectedMeetingId, StringComparison.Ordinal) ||
                !meetings.TryRemove(conversationId, out var removed))
            {
                return false;
            }

            try
            {
                Persist();
            }
            catch
            {
                meetings[conversationId] = removed;
                throw;
            }

            return true;
        }
    }

    private void Restore(DateTimeOffset now)
    {
        if (persistencePath is null || !File.Exists(persistencePath)) return;

        PersistedActiveMeetingState state;
        try
        {
            using var stream = File.OpenRead(persistencePath);
            state = JsonSerializer.Deserialize(
                    stream,
                    ActiveMeetingJsonSerializerContext.Default.PersistedActiveMeetingState)
                ?? throw new InvalidDataException("The persisted active-meeting state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The persisted active-meeting state at '{persistencePath}' is corrupt.",
                exception);
        }

        if (!string.Equals(state.SchemaVersion, "1.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The persisted active-meeting state at '{persistencePath}' has an unsupported schema.");
        }

        if (state.Meetings is null)
        {
            throw new InvalidDataException(
                $"The persisted active-meeting state at '{persistencePath}' has no meeting collection.");
        }

        var cutoff = now - restoreMaximumAge!.Value;
        var expired = false;
        foreach (var entry in state.Meetings)
        {
            if (entry is null ||
                entry.Target is null ||
                string.IsNullOrWhiteSpace(entry.ConversationId) ||
                string.IsNullOrWhiteSpace(entry.Target.TenantId) ||
                string.IsNullOrWhiteSpace(entry.Target.MeetingId) ||
                string.IsNullOrWhiteSpace(entry.Target.JoinUrl) ||
                entry.Target.CapturedAt is null)
            {
                throw new InvalidDataException(
                    $"The persisted active-meeting state at '{persistencePath}' contains an invalid entry.");
            }

            if (entry.Target.CapturedAt < cutoff)
            {
                expired = true;
                continue;
            }

            if (!meetings.TryAdd(entry.ConversationId, entry.Target))
            {
                throw new InvalidDataException(
                    $"The persisted active-meeting state at '{persistencePath}' contains duplicate conversations.");
            }
        }

        if (expired) Persist();
    }

    private void PruneExpired(DateTimeOffset now)
    {
        if (restoreMaximumAge is null) return;

        var cutoff = now - restoreMaximumAge.Value;
        var expired = meetings
            .Where(pair => pair.Value.CapturedAt is null || pair.Value.CapturedAt < cutoff)
            .ToArray();
        if (expired.Length == 0) return;

        foreach (var pair in expired)
        {
            meetings.TryRemove(pair.Key, out _);
        }

        try
        {
            Persist();
        }
        catch
        {
            foreach (var pair in expired)
            {
                meetings[pair.Key] = pair.Value;
            }

            throw;
        }
    }

    private void Persist()
    {
        if (persistencePath is null) return;

        var directory = Path.GetDirectoryName(persistencePath)
            ?? throw new InvalidOperationException("The active-meeting persistence path has no parent directory.");
        Directory.CreateDirectory(directory);
        var state = new PersistedActiveMeetingState(
            "1.0",
            meetings
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new PersistedActiveMeetingEntry(pair.Key, pair.Value))
                .ToArray());
        var temporaryPath = $"{persistencePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    state,
                    ActiveMeetingJsonSerializerContext.Default.PersistedActiveMeetingState);
                stream.Flush(true);
            }

            File.Move(temporaryPath, persistencePath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool TryNormalizeJoinUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 2048 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Host = uri.IdnHost.ToLowerInvariant()
        };
        if (builder.Path.Length > 1)
        {
            builder.Path = builder.Path.TrimEnd('/');
        }

        var queryParts = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(part => part, StringComparer.Ordinal)
            .ToArray();
        builder.Query = string.Join('&', queryParts);
        normalized = builder.Uri.AbsoluteUri;
        return true;
    }
}

internal sealed record PersistedActiveMeetingEntry(
    string ConversationId,
    ActiveMeetingTarget Target);

internal sealed record PersistedActiveMeetingState(
    string SchemaVersion,
    IReadOnlyList<PersistedActiveMeetingEntry> Meetings);

[JsonSerializable(typeof(PersistedActiveMeetingState))]
internal sealed partial class ActiveMeetingJsonSerializerContext : JsonSerializerContext;

public sealed class GraphTranscriptDiagnosticClient(GraphServiceClient graphClient) : ITranscriptDiagnosticClient
{
    public async Task<TranscriptDiagnosticResult> CheckAsync(
        ActiveMeetingTarget target,
        CancellationToken cancellationToken)
    {
        var response = await graphClient.Users[target.OrganizerUserId]
            .OnlineMeetings[target.MeetingResourceId]
            .Transcripts
            .GetAsync(cancellationToken: cancellationToken);
        var transcripts = response?.Value?
            .Where(transcript => !string.IsNullOrWhiteSpace(transcript.Id))
            .OrderByDescending(transcript => transcript.CreatedDateTime)
            .ToArray() ?? [];

        if (transcripts.Length == 0)
        {
            return new TranscriptDiagnosticResult(200, 0, null, DateTimeOffset.UtcNow);
        }

        var latest = transcripts[0];
        var content = await graphClient.Users[target.OrganizerUserId]
            .OnlineMeetings[target.MeetingResourceId]
            .Transcripts[latest.Id]
            .Content
            .GetAsync(
                configuration => configuration.Headers.Add("Accept", "text/vtt"),
                cancellationToken);

        if (content is null)
        {
            return new TranscriptDiagnosticResult(200, transcripts.Length, null, DateTimeOffset.UtcNow);
        }

        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new TranscriptDiagnosticResult(200, transcripts.Length, text.Length, DateTimeOffset.UtcNow);
    }
}

public sealed class MeetingTranscriptDiagnosticService(
    ActiveMeetingStore store,
    ITranscriptDiagnosticClient client,
    ILogger<MeetingTranscriptDiagnosticService> logger)
{
    public async Task<string> CheckAsync(string conversationId, CancellationToken cancellationToken)
    {
        if (!store.TryGet(conversationId, out var target) || target is null)
        {
            return "No active meeting was captured for this chat. Start a new meeting, wait for the bot's meeting-start card, and try `diagnose transcript` again.";
        }

        if (string.IsNullOrWhiteSpace(target.MeetingResourceId) ||
            string.IsNullOrWhiteSpace(target.OrganizerUserId))
        {
            return "The active meeting has live context, but Graph transcript identifiers are not available yet. The Graph transcript diagnostic was not run.";
        }

        try
        {
            var result = await client.CheckAsync(target, cancellationToken);
            if (result.TranscriptCount == 0)
            {
                return $"Graph check at {result.CheckedAt:O}: `GET /transcripts` returned 200 OK with an empty collection. No transcript context is available through Graph right now.";
            }

            if (result.ContentCharacters is null)
            {
                return $"Graph check at {result.CheckedAt:O}: Graph listed {result.TranscriptCount} transcript resource(s), but the latest transcript content response was empty.";
            }

            return $"Graph check at {result.CheckedAt:O}: Graph listed {result.TranscriptCount} transcript resource(s), and the latest VTT content request returned {result.ContentCharacters.Value:N0} characters. No transcript text was posted to the chat.";
        }
        catch (ApiException exception)
        {
            logger.LogWarning(
                exception,
                "Mid-meeting transcript diagnostic returned Graph status {StatusCode} for conversation {ConversationId}.",
                exception.ResponseStatusCode,
                conversationId);
            return $"Graph check returned HTTP {exception.ResponseStatusCode}. No transcript context was supplied to the agent. See App Service logs for the correlated diagnostic error.";
        }
    }
}
