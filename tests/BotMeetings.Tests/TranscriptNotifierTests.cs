using BotMeetings.TranscriptIngestion;
using Microsoft.Extensions.Options;
using Microsoft.Teams.Cards;

namespace BotMeetings.Tests;

public sealed class TranscriptNotifierTests
{
    [Fact]
    public async Task Completed_notification_tells_users_when_QnA_is_ready()
    {
        var notifier = new TeamsTranscriptNotifier(Options.Create(new TranscriptIngestionOptions()));
        AdaptiveCard? sentCard = null;
        notifier.Initialize((_, card) =>
        {
            sentCard = card;
            return Task.CompletedTask;
        });
        var document = new SourceDocument(
            "1.0", "doc", "tenant-1", "meeting:meeting-1", "teams-transcript", "meeting-1",
            "transcript-1", "Planning", TestData.Request().MeetingEndedAt, TestData.Request().MeetingEndedAt,
            "hash", "[00:00:01.000] Jane: Approved.",
            [new TranscriptSegment(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Jane", "Approved.")],
            []);

        await notifier.NotifyCompletedAsync(TestData.Request(), document, CancellationToken.None);

        var card = Assert.IsType<AdaptiveCard>(sentCard);
        Assert.Contains((card.Body ?? []).OfType<TextBlock>(), block => block.Text?.Contains("ready for questions") == true);
    }
}