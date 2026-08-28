using BotMeetings.TranscriptIngestion;
using Microsoft.Extensions.Options;

namespace BotMeetings.Tests;

public sealed class TranscriptContentTests
{
    [Fact]
    public void Parser_preserves_speaker_timestamps_and_multiline_text()
    {
        const string vtt = """
            WEBVTT

            1
            00:00:01.250 --> 00:00:03.500
            <v Jane Doe>Hello &amp; welcome
            to the meeting.</v>
            """;

        var segment = Assert.Single(new VttTranscriptParser().Parse(vtt));

        Assert.Equal(TimeSpan.FromMilliseconds(1250), segment.Start);
        Assert.Equal("Jane Doe", segment.Speaker);
        Assert.Equal("Hello & welcome to the meeting.", segment.Text);
    }

    [Fact]
    public void Builder_creates_stable_scoped_chunks_for_agent_context()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 5, 18, 0, 0, TimeSpan.Zero));
        var builder = new SourceDocumentBuilder(
            Options.Create(new TranscriptIngestionOptions { MaximumChunkCharacters = 256 }),
            clock);
        var request = TestData.Request();
        var artifact = new TranscriptArtifact("transcript-1", clock.UtcNow, "unused");
        var segments = new[]
        {
            new TranscriptSegment(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Jane", "First decision."),
            new TranscriptSegment(1, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "Bob", "Second decision.")
        };

        var first = builder.Build(request, artifact, segments);
        var second = builder.Build(request, artifact, segments);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal($"meeting:{request.MeetingId}", first.ScopeId);
        var chunk = Assert.Single(first.Chunks);
        Assert.Equal(new[] { "Jane", "Bob" }, chunk.Speakers);
    }
}