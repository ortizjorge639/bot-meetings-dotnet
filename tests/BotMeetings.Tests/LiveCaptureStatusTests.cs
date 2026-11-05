using BotMeetings.LiveContext;
using BotMeetings.TranscriptDiagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace BotMeetings.Tests;

public sealed class LiveCaptureStatusTests
{
    [Fact]
    public void Heartbeat_age_is_separate_from_caption_age_and_scoped_by_tenant_and_meeting()
    {
        var service = new LiveCaptureStatusService();
        var now = DateTimeOffset.UtcNow;
        service.Update("tenant-a", new CaptureHeartbeatRequest("meeting-a", "active", 2, 1), now);

        Assert.Contains("current", service.Describe("tenant-a", "meeting-a", now.AddSeconds(45)));
        Assert.Contains("pending: 2; failed: 1", service.Describe("tenant-a", "meeting-a", now));
        Assert.Contains("stale; capture state unknown", service.Describe("tenant-a", "meeting-a", now.AddSeconds(46)));
        Assert.Contains("unknown", service.Describe("tenant-b", "meeting-a", now));
        Assert.Contains("unknown", service.Describe("tenant-a", "meeting-b", now));
    }

    [Fact]
    public void Restart_does_not_claim_that_a_browser_is_connected()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new LiveCaptureStatusService();
        service.Update("tenant-a", new CaptureHeartbeatRequest("meeting-a", "active", 0, 0), now);

        Assert.Contains("no browser heartbeat", new LiveCaptureStatusService().Describe("tenant-a", "meeting-a", now));
    }

    [Fact]
    public void Heartbeat_updates_remove_old_meetings()
    {
        var service = new LiveCaptureStatusService();
        var now = DateTimeOffset.UtcNow;
        service.Update("tenant-a", new CaptureHeartbeatRequest("meeting-a", "active", 0, 0), now);
        service.Update("tenant-a", new CaptureHeartbeatRequest("meeting-b", "active", 0, 0), now.AddMinutes(3));

        Assert.Contains("no browser heartbeat", service.Describe("tenant-a", "meeting-a", now.AddMinutes(3)));
        Assert.Contains("current", service.Describe("tenant-a", "meeting-b", now.AddMinutes(3)));
    }

    [Theory]
    [InlineData(true, false, "meeting-a", "active", 0, HttpStatusCode.Unauthorized)]
    [InlineData(false, true, "meeting-a", "active", 0, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, true, "meeting-b", "active", 0, HttpStatusCode.NotFound)]
    [InlineData(true, true, "meeting-ended", "active", 0, HttpStatusCode.NotFound)]
    [InlineData(true, true, "meeting-a", "invalid", 0, HttpStatusCode.BadRequest)]
    [InlineData(true, true, "meeting-a", "active", 101, HttpStatusCode.BadRequest)]
    [InlineData(true, true, "meeting-a", "active", 1, HttpStatusCode.NoContent)]
    public async Task Endpoint_requires_key_enabled_feature_active_tenant_meeting_and_valid_state(
        bool enabled, bool authorized, string meetingId, string state, int pending, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Options.Create(new LiveContextOptions
        {
            Enabled = enabled,
            AccessKey = new string('x', 32),
            DataPath = Path.GetTempPath()
        }));
        var meetings = new ActiveMeetingStore();
        meetings.Set("chat-a", new ActiveMeetingTarget("resource", "organizer", "tenant-a", "meeting-a"));
        meetings.Set("chat-b", new ActiveMeetingTarget("resource", "organizer", "tenant-b", "meeting-b"));
        meetings.Set("chat-ended", new ActiveMeetingTarget("resource", "organizer", "tenant-a", "meeting-ended"));
        meetings.Remove("chat-ended", "meeting-ended");
        builder.Services.AddSingleton(meetings);
        builder.Services.AddSingleton<LiveCaptureStatusService>();
        builder.Services.AddSingleton<IAcsTokenService, AcsTokenService>();
        builder.Services.AddSingleton<ILiveContextStore, FileLiveContextStore>();
        await using var app = builder.Build();
        app.MapLiveContextEndpoints("tenant-a");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        if (authorized) client.DefaultRequestHeaders.Add("X-Live-Context-Key", new string('x', 32));

        var response = await client.PostAsJsonAsync(
            "/api/live-context/status", new CaptureHeartbeatRequest(meetingId, state, pending, 0));

        Assert.Equal(expected, response.StatusCode);
        var description = app.Services.GetRequiredService<LiveCaptureStatusService>()
            .Describe("tenant-a", meetingId, DateTimeOffset.UtcNow);
        Assert.Contains(expected == HttpStatusCode.NoContent ? "current" : "no browser heartbeat", description);
        await app.StopAsync();
    }
}
