using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BotMeetings.TranscriptIngestion;

public sealed class FileTranscriptStore : ITranscriptIngestionStore, ISourceDocumentSink, ISourceDocumentStore
{
    private readonly string jobsPath;
    private readonly string documentsPath;
    private readonly SemaphoreSlim gate = new(1, 1);

    public FileTranscriptStore(IOptions<TranscriptIngestionOptions> options, IHostEnvironment environment)
    {
        var rootPath = Path.IsPathRooted(options.Value.DataPath)
            ? options.Value.DataPath
            : Path.Combine(environment.ContentRootPath, options.Value.DataPath);
        jobsPath = Path.Combine(rootPath, "jobs");
        documentsPath = Path.Combine(rootPath, "documents");
    }

    public async Task EnqueueAsync(TranscriptIngestionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = GetJobPath(request.TenantId, request.MeetingId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            await WriteJsonAtomicallyAsync(
                path,
                new TranscriptIngestionJob(request, TranscriptIngestionStatus.Pending, now, now, 0),
                TranscriptJsonSerializerContext.Default.TranscriptIngestionJob,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<TranscriptIngestionJob>> GetDueJobsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(jobsPath)) return [];

        var jobs = new List<TranscriptIngestionJob>();
        foreach (var path in Directory.EnumerateFiles(jobsPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = await ReadJobAsync(path, cancellationToken);
            if (job.Status == TranscriptIngestionStatus.Pending && job.NextAttemptAt <= now) jobs.Add(job);
        }

        return jobs;
    }

    public async Task UpdateAsync(TranscriptIngestionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await WriteJsonAtomicallyAsync(
                GetJobPath(job.Request.TenantId, job.Request.MeetingId),
                job,
                TranscriptJsonSerializerContext.Default.TranscriptIngestionJob,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TranscriptIngestionJob?> GetAsync(
        string tenantId,
        string meetingId,
        CancellationToken cancellationToken)
    {
        var path = GetJobPath(tenantId, meetingId);
        return File.Exists(path) ? await ReadJobAsync(path, cancellationToken) : null;
    }

    public async Task UpsertAsync(SourceDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await WriteJsonAtomicallyAsync(
                Path.Combine(documentsPath, $"{Hash(document.Id)}.json"),
                document,
                TranscriptJsonSerializerContext.Default.SourceDocument,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SourceDocument?> GetLatestCompletedAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(jobsPath)) return null;

        TranscriptIngestionJob? latest = null;
        foreach (var path in Directory.EnumerateFiles(jobsPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = await ReadJobAsync(path, cancellationToken);
            if (job.Status != TranscriptIngestionStatus.Completed ||
                string.IsNullOrWhiteSpace(job.DocumentId) ||
                !string.Equals(job.Request.TenantId, tenantId, StringComparison.Ordinal) ||
                !string.Equals(job.Request.ConversationId, conversationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (latest is null || job.Request.MeetingEndedAt > latest.Request.MeetingEndedAt) latest = job;
        }

        if (latest?.DocumentId is not { } documentId) return null;
        var documentPath = Path.Combine(documentsPath, $"{Hash(documentId)}.json");
        if (!File.Exists(documentPath)) return null;

        await using var stream = File.OpenRead(documentPath);
        return await JsonSerializer.DeserializeAsync(
            stream,
            TranscriptJsonSerializerContext.Default.SourceDocument,
            cancellationToken);
    }

    private string GetJobPath(string tenantId, string meetingId) =>
        Path.Combine(jobsPath, $"{Hash($"{tenantId}|{meetingId}")}.json");

    private static async Task<TranscriptIngestionJob> ReadJobAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(
                stream,
                TranscriptJsonSerializerContext.Default.TranscriptIngestionJob,
                cancellationToken)
            ?? throw new InvalidDataException($"The transcript ingestion job at '{path}' is empty.");
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"No parent directory exists for '{path}'.");
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
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}