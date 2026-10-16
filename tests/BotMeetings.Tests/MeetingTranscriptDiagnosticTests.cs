using BotMeetings.TranscriptDiagnostics;
using BotMeetings.LiveContext;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotMeetings.Tests;

public sealed class MeetingTranscriptDiagnosticTests : IDisposable
{
    private readonly string temporaryPath =
        Path.Combine(Path.GetTempPath(), $"bot-meetings-active-{Guid.NewGuid():N}");

    [Fact]
    public async Task Check_reports_when_no_active_meeting_was_captured()
    {
        var service = CreateService(new TranscriptDiagnosticResult(200, 0, null, DateTimeOffset.UtcNow));

        var message = await service.CheckAsync("conversation-1", CancellationToken.None);

        Assert.Contains("No active meeting was captured", message);
    }

    [Fact]
    public async Task Check_reports_an_empty_Graph_collection()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero);
        var store = new ActiveMeetingStore();
        store.Set("conversation-1", new ActiveMeetingTarget("meeting-1", "organizer-1"));
        var service = CreateService(
            new TranscriptDiagnosticResult(200, 0, null, checkedAt),
            store);

        var message = await service.CheckAsync("conversation-1", CancellationToken.None);

        Assert.Contains("200 OK with an empty collection", message);
        Assert.Contains("No transcript context is available", message);
    }

    [Fact]
    public async Task Check_reports_available_content_without_returning_it()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero);
        var store = new ActiveMeetingStore();
        store.Set("conversation-1", new ActiveMeetingTarget("meeting-1", "organizer-1"));
        var service = CreateService(
            new TranscriptDiagnosticResult(200, 1, 1234, checkedAt),
            store);

        var message = await service.CheckAsync("conversation-1", CancellationToken.None);

        Assert.Contains("1 transcript resource", message);
        Assert.Contains("1,234 characters", message);
        Assert.Contains("No transcript text was posted", message);
    }

    [Fact]
    public async Task Check_rejects_live_only_target_without_calling_Graph()
    {
        var store = new ActiveMeetingStore();
        store.Set(
            "conversation-1",
            new ActiveMeetingTarget(
                string.Empty,
                string.Empty,
                "tenant-a",
                "meeting-1",
                "https://teams.microsoft.com/l/meetup-join/example"));
        var client = new StubDiagnosticClient(
            new TranscriptDiagnosticResult(200, 1, 100, DateTimeOffset.UtcNow));
        var service = new MeetingTranscriptDiagnosticService(
            store,
            client,
            NullLogger<MeetingTranscriptDiagnosticService>.Instance);

        var message = await service.CheckAsync("conversation-1", CancellationToken.None);

        Assert.Contains("Graph transcript identifiers are not available", message);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void Active_meeting_resolver_normalizes_equivalent_join_links()
    {
        var store = new ActiveMeetingStore();
        store.Set(
            "conversation-1",
            new ActiveMeetingTarget(
                "resource-1",
                "organizer-1",
                "tenant-a",
                "meeting-1",
                "https://TEAMS.microsoft.com/l/meetup-join/example/?b=2&a=1#ignored"));

        var resolved = store.TryResolve(
            "tenant-a",
            "  https://teams.microsoft.com/l/meetup-join/example?a=1&b=2  ",
            out var target);

        Assert.True(resolved);
        Assert.Equal("meeting-1", target?.MeetingId);
    }

    [Fact]
    public void Active_meeting_resolver_does_not_cross_tenant_boundaries()
    {
        const string joinUrl = "https://teams.microsoft.com/l/meetup-join/example";
        var store = new ActiveMeetingStore();
        store.Set(
            "conversation-1",
            new ActiveMeetingTarget("resource-1", "organizer-1", "tenant-a", "meeting-1", joinUrl));

        Assert.False(store.TryResolve("tenant-b", joinUrl, out var target));
        Assert.Null(target);
        Assert.False(store.TryResolve("tenant-a", "http://teams.microsoft.com/l/meetup-join/example", out _));
        Assert.True(store.IsActive("tenant-a", "meeting-1"));
        Assert.False(store.IsActive("tenant-b", "meeting-1"));
        Assert.False(store.IsActive("tenant-a", "meeting-2"));
    }

    [Fact]
    public void Active_meeting_store_persists_restores_and_removes_state()
    {
        var store = CreatePersistentStore(24);
        store.Set(
            "conversation-1",
            new ActiveMeetingTarget(
                "resource-1",
                "organizer-1",
                "tenant-a",
                "meeting-1",
                "https://teams.microsoft.com/l/meetup-join/example",
                DateTimeOffset.UtcNow));

        var restored = CreatePersistentStore(24);
        Assert.True(restored.IsActive("tenant-a", "meeting-1"));
        Assert.True(restored.TryResolve(
            "tenant-a",
            "https://teams.microsoft.com/l/meetup-join/example",
            out var target));
        Assert.Equal("meeting-1", target?.MeetingId);

        Assert.True(restored.Remove("conversation-1", "meeting-1"));

        Assert.False(CreatePersistentStore(24).IsActive("tenant-a", "meeting-1"));
    }

    [Fact]
    public void Active_meeting_store_does_not_restore_expired_state()
    {
        var store = CreatePersistentStore(1);
        store.Set(
            "conversation-1",
            new ActiveMeetingTarget(
                "resource-1",
                "organizer-1",
                "tenant-a",
                "meeting-1",
                "https://teams.microsoft.com/l/meetup-join/example",
                DateTimeOffset.UtcNow.AddHours(-2)));

        Assert.False(CreatePersistentStore(1).IsActive("tenant-a", "meeting-1"));
    }

    [Fact]
    public void Active_meeting_store_prunes_expired_state_during_runtime_lookups()
    {
        var capturedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var byConversation = CreatePersistentStore(1, "by-conversation");
        byConversation.Set(
            "conversation-1",
            CreateTarget("meeting-1", capturedAt));
        var byJoinLink = CreatePersistentStore(1, "by-link");
        byJoinLink.Set(
            "conversation-2",
            CreateTarget("meeting-2", capturedAt));
        var byMeeting = CreatePersistentStore(1, "by-meeting");
        byMeeting.Set(
            "conversation-3",
            CreateTarget("meeting-3", capturedAt));

        Assert.False(byConversation.TryGet("conversation-1", out var conversationTarget));
        Assert.Null(conversationTarget);
        Assert.False(byJoinLink.TryResolve(
            "tenant-a",
            "https://teams.microsoft.com/l/meetup-join/example",
            out var linkTarget));
        Assert.Null(linkTarget);
        Assert.False(byMeeting.IsActive("tenant-a", "meeting-3"));

        Assert.False(CreatePersistentStore(1, "by-conversation").IsActive("tenant-a", "meeting-1"));
        Assert.False(CreatePersistentStore(1, "by-link").IsActive("tenant-a", "meeting-2"));
        Assert.False(CreatePersistentStore(1, "by-meeting").IsActive("tenant-a", "meeting-3"));
    }

    [Fact]
    public void Delayed_meeting_end_cannot_remove_a_newer_conversation_occurrence()
    {
        var store = CreatePersistentStore(24);
        store.Set("conversation-1", CreateTarget("meeting-a", DateTimeOffset.UtcNow.AddMinutes(-1)));
        store.Set("conversation-1", CreateTarget("meeting-b", DateTimeOffset.UtcNow));

        Assert.False(store.Remove("conversation-1", "meeting-a"));
        Assert.True(store.IsActive("tenant-a", "meeting-b"));
        Assert.True(CreatePersistentStore(24).IsActive("tenant-a", "meeting-b"));
    }

    [Fact]
    public void Active_meeting_store_surfaces_corrupt_persistence()
    {
        Directory.CreateDirectory(temporaryPath);
        File.WriteAllText(Path.Combine(temporaryPath, "active-meetings.json"), "{not-json");

        Assert.Throws<InvalidDataException>(() => CreatePersistentStore(24));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryPath)) Directory.Delete(temporaryPath, true);
    }

    private ActiveMeetingStore CreatePersistentStore(int maximumAgeHours, string? suffix = null) =>
        new(
            Options.Create(new LiveContextOptions
            {
                Enabled = true,
                DataPath = suffix is null ? temporaryPath : Path.Combine(temporaryPath, suffix),
                MaximumCaptionAgeHours = maximumAgeHours
            }),
            new TestHostEnvironment());

    private static ActiveMeetingTarget CreateTarget(
        string meetingId,
        DateTimeOffset capturedAt) =>
        new(
            "resource-1",
            "organizer-1",
            "tenant-a",
            meetingId,
            "https://teams.microsoft.com/l/meetup-join/example",
            capturedAt);

    private static MeetingTranscriptDiagnosticService CreateService(
        TranscriptDiagnosticResult result,
        ActiveMeetingStore? store = null) =>
        new(
            store ?? new ActiveMeetingStore(),
            new StubDiagnosticClient(result),
            NullLogger<MeetingTranscriptDiagnosticService>.Instance);

    private sealed class StubDiagnosticClient(TranscriptDiagnosticResult result) : ITranscriptDiagnosticClient
    {
        public int CallCount { get; private set; }

        public Task<TranscriptDiagnosticResult> CheckAsync(
            ActiveMeetingTarget target,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "BotMeetings.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
