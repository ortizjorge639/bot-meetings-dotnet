using Microsoft.Extensions.Options;
using Microsoft.Teams.Cards;

namespace BotMeetings.TranscriptIngestion;

public sealed class TeamsTranscriptNotifier(IOptions<TranscriptIngestionOptions> options) : ITranscriptNotificationSink
{
    private Func<string, AdaptiveCard, Task>? sender;

    public void Initialize(Func<string, AdaptiveCard, Task> send) =>
        sender = send ?? throw new ArgumentNullException(nameof(send));

    public Task NotifyCompletedAsync(
        TranscriptIngestionRequest request,
        SourceDocument document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transcript = string.Join(
            "\n",
            document.Segments.Select(segment => string.IsNullOrWhiteSpace(segment.Speaker)
                ? segment.Text
                : $"{segment.Speaker}: {segment.Text}"));
        var truncated = transcript.Length > options.Value.MaximumCardTranscriptCharacters;
        if (truncated) transcript = transcript[..options.Value.MaximumCardTranscriptCharacters];

        var body = CreateHeader(request);
        body.Add(new TextBlock(transcript) { Wrap = true });
        if (truncated)
        {
            body.Add(new TextBlock("Transcript truncated in Teams; the complete transcript is retained for agent context.")
            {
                Wrap = true,
                IsSubtle = true
            });
        }
        body.Add(new TextBlock("I'm ready for questions about this meeting. Ask me in this meeting chat and I'll answer from the transcript with speaker and timestamp citations.")
        {
            Wrap = true,
            Weight = TextWeight.Bolder
        });

        return SendAsync(request.ConversationId, new AdaptiveCard
        {
            Schema = "http://adaptivecards.io/schemas/adaptive-card.json",
            Body = body
        });
    }

    public Task NotifyUnavailableAsync(
        TranscriptIngestionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = CreateHeader(request);
        body.Add(new TextBlock("Transcript not available for this meeting.") { Wrap = true });
        return SendAsync(request.ConversationId, new AdaptiveCard
        {
            Schema = "http://adaptivecards.io/schemas/adaptive-card.json",
            Body = body
        });
    }

    private static List<CardElement> CreateHeader(TranscriptIngestionRequest request) =>
    [
        new TextBlock("The meeting has ended.")
        {
            Wrap = true,
            Weight = TextWeight.Bolder,
            Size = TextSize.Large
        },
        new TextBlock($"**End Time:** {request.MeetingEndedAt}") { Wrap = true },
        new TextBlock("**Transcript:**")
        {
            Wrap = true,
            Weight = TextWeight.Bolder
        }
    ];

    private Task SendAsync(string conversationId, AdaptiveCard card)
    {
        var send = sender ?? throw new InvalidOperationException("The Teams transcript notifier is not initialized.");
        return send(conversationId, card);
    }
}