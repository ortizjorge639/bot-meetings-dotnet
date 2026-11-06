import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

class NodeStub extends EventTarget {
  value = "";
  disabled = false;
  textContent = "None";
  children: NodeStub[] = [];
  replaceChildren(): void { this.children = []; }
  prepend(node: NodeStub): void { this.children.unshift(node); }
  get lastElementChild(): { remove: () => void } | undefined {
    return this.children.length ? { remove: () => { this.children.pop(); } } : undefined;
  }
}

const mocks = vi.hoisted(() => ({
  createAgent: vi.fn(),
  clientDispose: vi.fn(async () => {}),
  credentialDispose: vi.fn(),
  resolve: vi.fn(),
  token: vi.fn(),
  post: vi.fn(async () => {}),
  status: vi.fn(async () => {})
}));
vi.mock("@azure/communication-calling", () => ({
  CallClient: class {
    createCallAgent = mocks.createAgent;
    dispose = mocks.clientDispose;
  },
  Features: { Captions: "captions" }
}));
vi.mock("@azure/communication-common", async importOriginal => ({
  ...await importOriginal<object>(),
  AzureCommunicationTokenCredential: class { dispose = mocks.credentialDispose; }
}));
vi.mock("./api", async importOriginal => ({
  ...await importOriginal<object>(),
  resolveMeeting: mocks.resolve,
  requestAcsToken: mocks.token,
  postFinalCaption: mocks.post,
  postCaptureStatus: mocks.status
}));

function fixture() {
  const nodes = new Map<string, NodeStub>();
  const get = (id: string): NodeStub => {
    if (!nodes.has(id)) nodes.set(id, new NodeStub());
    return nodes.get(id)!;
  };
  get("meeting-link").value = "https://teams.microsoft.com/l/meetup-join/test";
  get("access-key").value = "not-a-real-key";
  const window = new EventTarget();
  vi.stubGlobal("window", window);
  vi.stubGlobal("document", { getElementById: get, createElement: () => new NodeStub() });
  const captionEvents = new Map<string, (event?: unknown) => void>();
  const captions = {
    kind: "TeamsCaptions",
    supportedSpokenLanguages: ["en-us"],
    isCaptionsFeatureActive: true,
    activeSpokenLanguage: "en-us",
    startCaptions: vi.fn(async () => {}),
    on: vi.fn((name: string, handler: (event?: unknown) => void) => { captionEvents.set(name, handler); }),
    off: vi.fn((name: string) => { captionEvents.delete(name); })
  };
  const feature = { captions, on: vi.fn(), off: vi.fn() };
  const subscribers = new Map<string, Set<() => void>>();
  const call = {
    state: "Connected",
    isMuted: true,
    isIncomingAudioMuted: false,
    muteIncomingAudio: vi.fn(async () => { call.isIncomingAudioMuted = true; }),
    hangUp: vi.fn(async () => {}),
    dispose: vi.fn(),
    feature: vi.fn(() => feature),
    on: vi.fn((name: string, handler: () => void) => {
      if (!subscribers.has(name)) subscribers.set(name, new Set());
      subscribers.get(name)!.add(handler);
    }),
    off: vi.fn((name: string, handler: () => void) => { subscribers.get(name)?.delete(handler); })
  };
  const agent = { join: vi.fn(() => call), dispose: vi.fn(async () => {}) };
  mocks.createAgent.mockResolvedValue(agent);
  mocks.resolve.mockResolvedValue({ meetingId: "meeting-1" });
  mocks.token.mockResolvedValue({ token: "not-a-token", userId: "user-1", expiresOn: "2026-11-05T16:00:00Z" });
  return {
    get, call, agent, captions, captionEvents,
    emit: (name: string) => [...subscribers.get(name) ?? []].forEach(handler => handler()),
    submit: () => get("join-form").dispatchEvent(new Event("submit", { cancelable: true })),
    leave: () => get("leave-button").dispatchEvent(new Event("click"))
  };
}

beforeEach(() => {
  vi.resetModules();
  vi.clearAllMocks();
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe("capture session wiring (mocked SDK, not a live audio test)", () => {
  it("waits for connected mute state instead of failing during initial call setup", async () => {
    const test = fixture();
    test.call.state = "Connecting";
    test.call.isMuted = false;
    await import("./main");
    test.submit();
    await vi.waitFor(() => expect(test.get("microphone-state").textContent).toContain("awaiting connection"));
    expect(test.captions.startCaptions).not.toHaveBeenCalled();
    expect(test.call.hangUp).not.toHaveBeenCalled();
    expect(test.get("diagnostics").textContent).toBe("None");
    test.call.isMuted = true;
    test.call.state = "Connected";
    test.emit("stateChanged");
    await vi.waitFor(() => expect(test.captions.startCaptions).toHaveBeenCalledOnce());
    expect(test.get("microphone-state").textContent).toBe("Muted");
    expect(test.get("speaker-state").textContent).toBe("Muted");
    test.leave();
    await vi.waitFor(() => expect(test.agent.dispose).toHaveBeenCalledOnce());
  });

  it("uses muted join, starts captions after speaker mute, and disposes resources on leave", async () => {
    const test = fixture();
    await import("./main");
    test.submit();
    await vi.waitFor(() => expect(test.captions.startCaptions).toHaveBeenCalledOnce());
    expect(test.agent.join).toHaveBeenCalledWith(
      { meetingLink: test.get("meeting-link").value }, { audioOptions: { muted: true } });
    expect(test.call.muteIncomingAudio).toHaveBeenCalledOnce();
    test.leave();
    await vi.waitFor(() => expect(test.get("join-button").disabled).toBe(false));
    expect(test.call.hangUp).toHaveBeenCalledOnce();
    expect(test.call.dispose).toHaveBeenCalledOnce();
    expect(test.agent.dispose).toHaveBeenCalledOnce();
    expect(mocks.clientDispose).toHaveBeenCalledOnce();
    expect(mocks.credentialDispose).toHaveBeenCalledOnce();
    expect(test.get("access-key").value).toBe("");
  });

  it("posts final captions only and keeps both speaker identities", async () => {
    const test = fixture();
    await import("./main");
    test.submit();
    await vi.waitFor(() => expect(test.captions.startCaptions).toHaveBeenCalledOnce());
    const event = {
      timestamp: new Date("2026-11-05T15:00:00Z"),
      spokenText: "Same words",
      speaker: { displayName: "Ada", identifier: { kind: "communicationUser", communicationUserId: "speaker-a" } }
    };
    test.captionEvents.get("CaptionsReceived")?.({ ...event, resultType: "Partial" });
    expect(mocks.post).not.toHaveBeenCalled();
    test.captionEvents.get("CaptionsReceived")?.({ ...event, resultType: "Final" });
    test.captionEvents.get("CaptionsReceived")?.({
      ...event, resultType: "Final",
      speaker: { displayName: "Bob", identifier: { kind: "communicationUser", communicationUserId: "speaker-b" } }
    });
    await vi.waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2));
    expect(test.get("post-count").textContent).toBe("2");
    test.leave();
    await vi.waitFor(() => expect(test.agent.dispose).toHaveBeenCalledOnce());
  });

  it("stops and cleans up if a confirmed mute is lost", async () => {
    const test = fixture();
    await import("./main");
    test.submit();
    await vi.waitFor(() => expect(test.captions.startCaptions).toHaveBeenCalledOnce());
    test.call.isMuted = false;
    test.emit("isMutedChanged");
    await vi.waitFor(() => expect(test.agent.dispose).toHaveBeenCalledOnce());
    expect(test.get("diagnostics").textContent).toContain("Audio policy failed");
    expect(test.get("client-state").textContent).toContain("Capture failed");
  });

  it("cleans up after caption initialization fails", async () => {
    const test = fixture();
    test.captions.startCaptions.mockRejectedValueOnce(new Error("caption failure"));
    await import("./main");
    test.submit();
    await vi.waitFor(() => expect(test.agent.dispose).toHaveBeenCalledOnce());
    expect(test.get("diagnostics").textContent).toContain("Caption start failed");
  });

  it("hangs up on speaker-mute failure and never starts captions", async () => {
    const test = fixture();
    test.call.muteIncomingAudio.mockRejectedValueOnce(new Error("speaker mute rejected"));
    await import("./main");
    test.submit();
    await vi.waitFor(() => expect(test.agent.dispose).toHaveBeenCalledOnce());
    expect(test.call.hangUp).toHaveBeenCalledOnce();
    expect(test.captions.startCaptions).not.toHaveBeenCalled();
    expect(test.get("diagnostics").textContent).toContain("Audio policy failed");
  });

  it("does not join a late call agent after the operator leaves", async () => {
    const test = fixture();
    let finish!: (value: typeof test.agent) => void;
    mocks.createAgent.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    await import("./main");
    test.submit();
    await vi.waitFor(() => expect(mocks.createAgent).toHaveBeenCalledOnce());
    test.leave();
    await vi.waitFor(() => expect(test.get("join-button").disabled).toBe(false));
    finish(test.agent);
    await vi.waitFor(() => expect(test.agent.dispose).toHaveBeenCalledOnce());
    expect(test.agent.join).not.toHaveBeenCalled();
  });

  it("removes old listeners and permits a fresh session after disconnect", async () => {
    const test = fixture();
    await import("./main");
    test.submit();
    await vi.waitFor(() => expect(test.captions.startCaptions).toHaveBeenCalledOnce());
    test.call.state = "Disconnected";
    test.emit("stateChanged");
    await vi.waitFor(() => expect(test.get("join-button").disabled).toBe(false));
    expect(test.call.hangUp).not.toHaveBeenCalled();
    expect(test.captionEvents.size).toBe(0);
    test.call.state = "Connected";
    test.get("access-key").value = "another-test-key";
    test.submit();
    await vi.waitFor(() => expect(test.captions.startCaptions).toHaveBeenCalledTimes(2));
    test.leave();
    await vi.waitFor(() => expect(test.agent.dispose).toHaveBeenCalledTimes(2));
  });
});
