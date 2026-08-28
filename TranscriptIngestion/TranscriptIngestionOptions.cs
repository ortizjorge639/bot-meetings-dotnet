using System.ComponentModel.DataAnnotations;

namespace BotMeetings.TranscriptIngestion;

public sealed class TranscriptIngestionOptions
{
    public const string SectionName = "TranscriptIngestion";

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(15);

    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan MaximumWait { get; init; } = TimeSpan.FromMinutes(5);

    [Range(1, 10000)]
    public int MaximumAttempts { get; init; } = 20;

    [Range(256, 100000)]
    public int MaximumChunkCharacters { get; init; } = 4000;

    [Range(1000, 25000)]
    public int MaximumCardTranscriptCharacters { get; init; } = 20000;

    [Required]
    public string DataPath { get; init; } = "App_Data/transcript-ingestion";
}