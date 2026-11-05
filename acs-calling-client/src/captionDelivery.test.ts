import { afterEach, describe, expect, it, vi } from "vitest";
import { CaptionDelivery, type DeliveryCounts } from "./captionDelivery";
import { HttpError, postFinalCaption, resolveMeeting } from "./api";
import type { LiveCaptionPayload } from "./contracts";

const payload: LiveCaptionPayload = {
  meetingId: "meeting-1", speakerId: "speaker-1", speakerDisplayName: "Ada",
  spokenText: "Fresh fact", timestamp: "2026-11-05T15:00:00Z", resultType: "Final"
};
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("caption delivery", () => {
  function create(send = vi.fn(async (_payload: LiveCaptionPayload, _signal: AbortSignal) => {})) {
    let counts: DeliveryCounts = { accepted: 0, pending: 0, failed: 0 };
    const error = vi.fn();
    const queue = new CaptionDelivery(send, next => { counts = next; }, error);
    return { queue, send, error, counts: () => counts };
  }

  it("counts a persisted receipt", async () => {
    const test = create();
    test.queue.enqueue(payload);
    await vi.waitFor(() => expect(test.counts()).toEqual({ accepted: 1, pending: 0, failed: 0 }));
  });

  it("retries the identical payload three times with bounded delays", async () => {
    vi.useFakeTimers();
    const test = create(vi.fn().mockRejectedValue(new HttpError("unavailable", 503)));
    test.queue.enqueue(payload);
    await vi.runAllTimersAsync();
    expect(test.send).toHaveBeenCalledTimes(4);
    expect(test.send.mock.calls.every(([value]) => value === payload)).toBe(true);
    expect(test.counts()).toEqual({ accepted: 0, pending: 0, failed: 1 });
    expect(test.error).toHaveBeenCalledOnce();
  });

  it("respects Retry-After", async () => {
    vi.useFakeTimers();
    const send = vi.fn().mockRejectedValueOnce(new HttpError("rate", 429, 5000)).mockResolvedValue(undefined);
    const test = create(send);
    test.queue.enqueue(payload);
    await vi.advanceTimersByTimeAsync(4999);
    expect(send).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(1);
    expect(send).toHaveBeenCalledTimes(2);
    expect(test.counts().accepted).toBe(1);
  });

  it("does not wait beyond the total caption age limit", async () => {
    vi.useFakeTimers();
    const test = create(vi.fn().mockRejectedValue(new HttpError("rate", 429, 61_000)));
    test.queue.enqueue(payload);
    await vi.runAllTimersAsync();
    expect(test.send).toHaveBeenCalledOnce();
    expect(test.counts().failed).toBe(1);
  });

  it.each([400, 401, 403, 404, 409, 422])("stops the queue on terminal HTTP %s", async status => {
    const test = create(vi.fn().mockRejectedValue(new HttpError("terminal", status)));
    test.queue.enqueue(payload);
    test.queue.enqueue(payload);
    await vi.waitFor(() => expect(test.counts().pending).toBe(0));
    expect(test.send).toHaveBeenCalledOnce();
    expect(test.counts().failed).toBe(2);
    test.queue.enqueue(payload);
    expect(test.counts().failed).toBe(3);
  });

  it("reports queue overflow and cancels in-flight delivery on stop", async () => {
    let signal: AbortSignal | undefined;
    const test = create(vi.fn((_value: LiveCaptionPayload, incoming: AbortSignal) => {
      signal = incoming;
      return new Promise<void>(() => {});
    }));
    for (let i = 0; i < 101; i++) test.queue.enqueue(payload);
    expect(test.counts()).toEqual({ accepted: 0, pending: 100, failed: 1 });
    test.queue.stop();
    expect(signal?.aborted).toBe(true);
    expect(test.counts()).toEqual({ accepted: 0, pending: 0, failed: 101 });
  });

  it("cancels a pending retry without another request", async () => {
    vi.useFakeTimers();
    const test = create(vi.fn().mockRejectedValue(new TypeError("network")));
    test.queue.enqueue(payload);
    await vi.advanceTimersByTimeAsync(1);
    test.queue.stop();
    await vi.runAllTimersAsync();
    expect(test.send).toHaveBeenCalledOnce();
    expect(test.counts()).toEqual({ accepted: 0, pending: 0, failed: 1 });
  });
});

describe("caption HTTP boundary", () => {
  it("does not count HTTP 200 without an accepted persistence receipt", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response('{"accepted":false}')));
    await expect(postFinalCaption("not-a-real-key", payload)).rejects.toThrow("valid accepted persistence receipt");
  });

  it("accepts a duplicate receipt after an uncertain first response", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({
      accepted: true, duplicate: true, captionId: "caption-1",
      receivedAt: payload.timestamp, persistedAt: payload.timestamp
    }))));
    await expect(postFinalCaption("not-a-real-key", payload)).resolves.toBeUndefined();
  });

  it("returns actionable link guidance without echoing the response body", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("sensitive-server-body", { status: 404 })));
    const error = await resolveMeeting("not-a-real-key", "https://teams.microsoft.com/meet/example").catch(e => e);
    expect(error).toBeInstanceOf(HttpError);
    expect(error.message).toContain("meeting-start card");
    expect(error.message).not.toContain("sensitive-server-body");
  });
});
