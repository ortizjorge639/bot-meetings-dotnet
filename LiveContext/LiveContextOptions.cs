using System.ComponentModel.DataAnnotations;

namespace BotMeetings.LiveContext;

public sealed class LiveContextOptions
{
    public const string SectionName = "LiveContext";

    public bool Enabled { get; init; }

    public string AccessKey { get; init; } = string.Empty;

    public string AcsConnectionString { get; init; } = string.Empty;

    [Required]
    public string DataPath { get; init; } = "/home/data/bot-meetings/live-context";

    [Range(1, 10000)]
    public int MaximumCaptionsPerMeeting { get; init; } = 5000;

    [Range(1, 168)]
    public int MaximumCaptionAgeHours { get; init; } = 24;
}
