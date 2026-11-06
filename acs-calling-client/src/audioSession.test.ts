import { describe, expect, it, vi } from "vitest";
import type { CallState } from "@azure/communication-calling";
import { enforceSilentAudio, silentJoinOptions } from "./audioSession";

function fakeCall(state: CallState = "Connected") {
  const listeners = new Map<string, () => void>();
  const call = {
    state,
    isMuted: true,
    isIncomingAudioMuted: false,
    muteIncomingAudio: vi.fn(async () => { call.isIncomingAudioMuted = true; }),
    hangUp: vi.fn(async () => {}),
    on: vi.fn((event: string, handler: () => void) => { listeners.set(event, handler); }),
    off: vi.fn((event: string) => { listeners.delete(event); })
  };
  return { call, listeners, emit: (event: string) => listeners.get(event)?.() };
}

describe("silent audio policy", () => {
  it("requests muted microphone in the pinned SDK join options", () => {
    expect(silentJoinOptions).toEqual({ audioOptions: { muted: true } });
  });

  it("starts captions only after speaker and microphone mute are verified", async () => {
    const { call } = fakeCall();
    const ready = vi.fn();
    const failed = vi.fn();
    enforceSilentAudio(call, ready, failed, vi.fn());
    expect(ready).not.toHaveBeenCalled();
    await vi.waitFor(() => expect(ready).toHaveBeenCalledOnce());
    expect(call.muteIncomingAudio).toHaveBeenCalledOnce();
    expect(failed).not.toHaveBeenCalled();
  });

  it("waits in the lobby and applies mute after connection", async () => {
    const { call, emit } = fakeCall("InLobby");
    const ready = vi.fn();
    enforceSilentAudio(call, ready, vi.fn(), vi.fn());
    expect(call.muteIncomingAudio).not.toHaveBeenCalled();
    call.state = "Connected";
    emit("stateChanged");
    await vi.waitFor(() => expect(ready).toHaveBeenCalledOnce());
    emit("stateChanged");
    expect(call.muteIncomingAudio).toHaveBeenCalledOnce();
  });

  it.each(["isMuted", "isIncomingAudioMuted"] as const)("fails if %s becomes false", async (property) => {
    const { call, emit, listeners } = fakeCall();
    const ready = vi.fn();
    const failed = vi.fn();
    enforceSilentAudio(call, ready, failed, vi.fn());
    await vi.waitFor(() => expect(ready).toHaveBeenCalledOnce());
    call[property] = false;
    emit(`${property}Changed`);
    expect(failed).toHaveBeenCalledOnce();
    expect(listeners.size).toBe(0);
  });

  it("rejects an unmuted join before caption setup", () => {
    const { call } = fakeCall("Connecting");
    call.isMuted = false;
    const ready = vi.fn();
    const failed = vi.fn();
    enforceSilentAudio(call, ready, failed, vi.fn());
    expect(failed).toHaveBeenCalledOnce();
    expect(ready).not.toHaveBeenCalled();
  });

  it("reports mute rejection without starting captions", async () => {
    const { call } = fakeCall();
    call.muteIncomingAudio.mockRejectedValue(new Error("SDK mute failure"));
    const failed = vi.fn();
    const ready = vi.fn();
    enforceSilentAudio(call, ready, failed, vi.fn());
    await vi.waitFor(() => expect(failed).toHaveBeenCalledOnce());
    expect(ready).not.toHaveBeenCalled();
  });

  it("ignores an in-flight mute completion after cleanup", async () => {
    const { call, listeners } = fakeCall();
    let finish!: () => void;
    call.muteIncomingAudio.mockImplementation(() => new Promise(resolve => {
      finish = () => { call.isIncomingAudioMuted = true; resolve(); };
    }));
    const ready = vi.fn();
    const stop = enforceSilentAudio(call, ready, vi.fn(), vi.fn());
    stop();
    finish();
    await Promise.resolve();
    expect(ready).not.toHaveBeenCalled();
    expect(listeners.size).toBe(0);
  });
});
