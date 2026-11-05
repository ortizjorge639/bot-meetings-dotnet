namespace BotMeetings.LiveContext;

public sealed class LiveCaptureStatusService
{
    private readonly Dictionary<(string TenantId, string MeetingId), CaptureHeartbeat> heartbeats = [];
    private readonly Lock gate = new();

    public void Update(string tenantId, CaptureHeartbeatRequest request, DateTimeOffset receivedAt)
    {
        lock (gate)
        {
            foreach (var key in heartbeats.Where(pair => receivedAt - pair.Value.ReceivedAt > TimeSpan.FromMinutes(2))
                         .Select(pair => pair.Key).ToArray())
            {
                heartbeats.Remove(key);
            }
            heartbeats[(tenantId, request.MeetingId)] = new CaptureHeartbeat(
                request.State, receivedAt, request.PendingCaptions, request.FailedCaptions);
        }
    }

    public string Describe(string tenantId, string meetingId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!heartbeats.TryGetValue((tenantId, meetingId), out var heartbeat))
            {
                return "Capture state: unknown (no browser heartbeat since server start).";
            }
            var age = Math.Max(0, (now - heartbeat.ReceivedAt).TotalSeconds);
            var freshness = age > 45 ? "stale; capture state unknown" : "current";
            return $"Capture heartbeat: {freshness}, received {age:F0} seconds ago. " +
                $"Client-reported state: {heartbeat.State}; pending: {heartbeat.PendingCaptions}; failed: {heartbeat.FailedCaptions}.";
        }
    }

    private sealed record CaptureHeartbeat(
        string State, DateTimeOffset ReceivedAt, int PendingCaptions, int FailedCaptions);
}
