using System.ComponentModel.DataAnnotations;

namespace BotMeetings.TranscriptQna;

public sealed class TranscriptAgentOptions
{
    public const string SectionName = "TranscriptAgent";

    [Required, Url]
    public string Endpoint { get; init; } = string.Empty;

    [Required]
    public string DeploymentName { get; init; } = string.Empty;

    [Range(1, 100)]
    public int MaximumContextChunks { get; init; } = 50;

    [Range(64, 4000)]
    public int MaximumQuestionCharacters { get; init; } = 1000;

    [Range(1, 32)]
    public int MaximumConcurrentAnswers { get; init; } = 2;

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan QueueWaitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromMinutes(2);
}