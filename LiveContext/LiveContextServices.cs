using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Communication;
using Azure.Communication.Identity;
using Microsoft.Extensions.Options;

namespace BotMeetings.LiveContext;

public interface ILiveContextStore
{
    Task<LiveCaptionWriteResult> AddAsync(
        string tenantId,
        CreateLiveCaptionRequest request,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken);

    Task<LiveMeetingContext?> GetAsync(
        string tenantId,
        string meetingId,
        CancellationToken cancellationToken);
}

public sealed class FileLiveContextStore : ILiveContextStore
{
    private readonly string contextsPath;
    private readonly int maximumCaptionsPerMeeting;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileLiveContextStore(IOptions<LiveContextOptions> options, IHostEnvironment environment)
    {
        var rootPath = Path.IsPathRooted(options.Value.DataPath)
            ? options.Value.DataPath
            : Path.Combine(environment.ContentRootPath, options.Value.DataPath);
        contextsPath = Path.Combine(rootPath, "meetings");
        maximumCaptionsPerMeeting = options.Value.MaximumCaptionsPerMeeting;
    }

    public async Task<LiveCaptionWriteResult> AddAsync(
        string tenantId,
        CreateLiveCaptionRequest request,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var meetingId = request.MeetingId.Trim();
        var speakerId = request.SpeakerId.Trim();
        var displayName = request.SpeakerDisplayName.Trim();
        var spokenText = request.SpokenText.Trim();
        var spokenAt = request.Timestamp.ToUniversalTime();
        var captionId = Hash($"{tenantId}\n{meetingId}\n{speakerId}\n{spokenAt:O}\n{spokenText}");
        var path = GetPath(tenantId, meetingId);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var context = await ReadAsync(path, cancellationToken);
            var existing = context?.Captions.FirstOrDefault(caption =>
                string.Equals(caption.Id, captionId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return new LiveCaptionWriteResult(existing, true);
            }

            var captions = context?.Captions.ToList() ?? [];
            if (captions.Count >= maximumCaptionsPerMeeting)
            {
                throw new LiveContextCapacityException();
            }

            var persistedAt = DateTimeOffset.UtcNow;
            var caption = new LiveCaption(
                captionId,
                speakerId,
                displayName,
                spokenText,
                spokenAt,
                receivedAt,
                persistedAt);
            captions.Add(caption);
            captions.Sort(static (left, right) =>
            {
                var timestampComparison = left.SpokenAt.CompareTo(right.SpokenAt);
                return timestampComparison != 0
                    ? timestampComparison
                    : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
            });

            var updated = new LiveMeetingContext(
                "1.0",
                tenantId,
                meetingId,
                context?.CreatedAt ?? receivedAt,
                persistedAt,
                captions);
            await WriteAtomicallyAsync(path, updated, cancellationToken);
            return new LiveCaptionWriteResult(caption, false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LiveMeetingContext?> GetAsync(
        string tenantId,
        string meetingId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(GetPath(tenantId, meetingId), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetPath(string tenantId, string meetingId) =>
        Path.Combine(contextsPath, $"{Hash($"{tenantId}|{meetingId}")}.json");

    private static async Task<LiveMeetingContext?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(
            stream,
            LiveContextJsonSerializerContext.Default.LiveMeetingContext,
            cancellationToken);
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        LiveMeetingContext context,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The live-context data path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    context,
                    LiveContextJsonSerializerContext.Default.LiveMeetingContext,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class LiveContextCapacityException : Exception;

public interface IAcsTokenService
{
    Task<AcsTokenResponse> CreateAsync(CancellationToken cancellationToken);
}

public sealed class AcsTokenService(IOptions<LiveContextOptions> options) : IAcsTokenService
{
    public async Task<AcsTokenResponse> CreateAsync(CancellationToken cancellationToken)
    {
        var client = new CommunicationIdentityClient(options.Value.AcsConnectionString);
        var result = await client.CreateUserAndTokenAsync(
            [CommunicationTokenScope.VoIP],
            cancellationToken);
        return new AcsTokenResponse(
            result.Value.User.Id,
            result.Value.AccessToken.Token,
            result.Value.AccessToken.ExpiresOn);
    }
}

public static class LiveContextKeyValidator
{
    public static bool IsValid(string? suppliedKey, string configuredKey)
    {
        if (string.IsNullOrEmpty(suppliedKey) || string.IsNullOrEmpty(configuredKey)) return false;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, configuredHash);
    }
}
