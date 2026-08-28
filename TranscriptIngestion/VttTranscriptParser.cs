using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace BotMeetings.TranscriptIngestion;

public sealed partial class VttTranscriptParser
{
    public IReadOnlyList<TranscriptSegment> Parse(string vtt)
    {
        ArgumentNullException.ThrowIfNull(vtt);
        if (string.IsNullOrWhiteSpace(vtt)) return [];

        var normalized = vtt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<TranscriptSegment>();

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var timingIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0) continue;

            var timingMatch = TimingLineRegex().Match(lines[timingIndex]);
            if (!timingMatch.Success ||
                !TryParseTimestamp(timingMatch.Groups["start"].Value, out var start) ||
                !TryParseTimestamp(timingMatch.Groups["end"].Value, out var end))
            {
                continue;
            }

            var rawText = string.Join(" ", lines.Skip(timingIndex + 1));
            if (string.IsNullOrWhiteSpace(rawText)) continue;

            var speakerMatch = VoiceTagRegex().Match(rawText);
            var speaker = speakerMatch.Success
                ? WebUtility.HtmlDecode(speakerMatch.Groups["speaker"].Value.Trim())
                : null;
            var text = WebUtility.HtmlDecode(HtmlTagRegex().Replace(rawText, string.Empty)).Trim();
            if (text.Length > 0)
            {
                segments.Add(new TranscriptSegment(segments.Count, start, end, speaker, text));
            }
        }

        return segments;
    }

    private static bool TryParseTimestamp(string value, out TimeSpan timestamp)
    {
        var formats = value.Count(character => character == ':') == 2
            ? new[] { @"hh\:mm\:ss\.fff", @"h\:mm\:ss\.fff" }
            : new[] { @"mm\:ss\.fff", @"m\:ss\.fff" };
        return TimeSpan.TryParseExact(value, formats, CultureInfo.InvariantCulture, TimeSpanStyles.None, out timestamp);
    }

    [GeneratedRegex(@"^(?<start>(?:\d{1,2}:)?\d{1,2}:\d{2}\.\d{3})\s+-->\s+(?<end>(?:\d{1,2}:)?\d{1,2}:\d{2}\.\d{3})(?:\s+.*)?$")]
    private static partial Regex TimingLineRegex();

    [GeneratedRegex(@"<v(?:\.[^ >]+)*\s+(?<speaker>[^>]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex VoiceTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}