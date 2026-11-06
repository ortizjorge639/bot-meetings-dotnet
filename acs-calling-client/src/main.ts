import {
  CallClient,
  Features,
  type Call,
  type CallAgent,
  type Captions,
  type CaptionsCallFeature,
  type CaptionsHandler,
  type TeamsCaptions,
  type TeamsCaptionsHandler
} from "@azure/communication-calling";
import { AzureCommunicationTokenCredential } from "@azure/communication-common";
import { postCaptureStatus, postFinalCaption, requestAcsToken, resolveMeeting } from "./api";
import { enforceSilentAudio, joinSilentCall } from "./audioSession";
import { CaptionDelivery, type DeliveryCounts } from "./captionDelivery";
import { toFinalPayload, type CaptionEvent } from "./contracts";
import "./style.css";

const joinForm = element<HTMLFormElement>("join-form");
const meetingLinkInput = element<HTMLInputElement>("meeting-link");
const displayNameInput = element<HTMLInputElement>("display-name");
const accessKeyInput = element<HTMLInputElement>("access-key");
const joinButton = element<HTMLButtonElement>("join-button");
const leaveButton = element<HTMLButtonElement>("leave-button");
const clientState = element<HTMLElement>("client-state");
const callState = element<HTMLElement>("call-state");
const lobbyState = element<HTMLElement>("lobby-state");
const captionState = element<HTMLElement>("caption-state");
const microphoneState = element<HTMLElement>("microphone-state");
const speakerState = element<HTMLElement>("speaker-state");
const heartbeatState = element<HTMLElement>("heartbeat-state");
const postCount = element<HTMLElement>("post-count");
const pendingCount = element<HTMLElement>("pending-count");
const failedCount = element<HTMLElement>("failed-count");
const captionEvents = element<HTMLOListElement>("caption-events");
const diagnostics = element<HTMLPreElement>("diagnostics");

interface CaptureSession {
  accessKey: string;
  meetingId?: string;
  lifetime: AbortController;
  client?: CallClient;
  credential?: AzureCommunicationTokenCredential;
  agent?: CallAgent;
  call?: Call;
  feature?: CaptionsCallFeature;
  captionsStarted: boolean;
  removeAudio?: () => void;
  removeCaptions?: () => void;
  removeState?: () => void;
  delivery?: CaptionDelivery;
  heartbeat?: ReturnType<typeof setInterval>;
  reporting: boolean;
  state: "connecting" | "active" | "stopped" | "error";
  counts: DeliveryCounts;
  closing?: Promise<void>;
}

let session: CaptureSession | undefined;

joinForm.addEventListener("submit", (event) => {
  event.preventDefault();
  void joinMeeting();
});
leaveButton.addEventListener("click", () => {
  if (session) void closeSession(session);
});
window.addEventListener("beforeunload", (event) => {
  if (session) {
    event.preventDefault();
    event.returnValue = "";
  }
});
window.addEventListener("pagehide", () => {
  if (session) void closeSession(session);
});
window.addEventListener("unhandledrejection", (event) => {
  showError("Unhandled browser promise rejection", event.reason);
});

function isCurrent(value: CaptureSession): boolean {
  return session === value && !value.lifetime.signal.aborted;
}

async function joinMeeting(): Promise<void> {
  if (session) return;
  const current: CaptureSession = {
    accessKey: accessKeyInput.value,
    lifetime: new AbortController(),
    captionsStarted: false,
    reporting: false,
    state: "connecting",
    counts: { accepted: 0, pending: 0, failed: 0 }
  };
  session = current;
  diagnostics.textContent = "None";
  captionEvents.replaceChildren();
  postCount.textContent = pendingCount.textContent = failedCount.textContent = "0";
  microphoneState.textContent = "Muted join requested";
  speakerState.textContent = "Not yet confirmed";
  captionState.textContent = "Not started";
  heartbeatState.textContent = "Not sent";
  setControls(true);
  const meetingLink = meetingLinkInput.value.trim();
  const signal = AbortSignal.any([current.lifetime.signal, AbortSignal.timeout(30_000)]);
  try {
    clientState.textContent = "Resolving Teams meeting";
    const resolved = await resolveMeeting(current.accessKey, meetingLink, signal);
    if (!isCurrent(current)) return;
    current.meetingId = resolved.meetingId;
    clientState.textContent = "Requesting short-lived ACS token";
    const token = await requestAcsToken(current.accessKey, signal);
    if (!isCurrent(current)) return;
    current.credential = new AzureCommunicationTokenCredential(token.token);
    current.client = new CallClient();
    clientState.textContent = "Creating ACS call agent";
    const agent = await current.client.createCallAgent(current.credential, {
      displayName: displayNameInput.value.trim()
    });
    if (!isCurrent(current)) {
      await agent.dispose();
      return;
    }
    current.agent = agent;
    const call = joinSilentCall(agent, meetingLink);
    current.call = call;
    const stateChanged = (): void => {
      if (!isCurrent(current)) return;
      callState.textContent = call.state;
      lobbyState.textContent = call.state === "InLobby" ? "Waiting for organizer admission" : "Not in lobby";
      if (call.state === "Disconnected") {
        if (call.callEndReason?.code) {
          showError("Call disconnected", new Error(`ACS code ${call.callEndReason.code}, subcode ${call.callEndReason.subCode}`));
        }
        void closeSession(current);
      }
    };
    call.on("stateChanged", stateChanged);
    current.removeState = () => call.off("stateChanged", stateChanged);
    stateChanged();
    current.removeAudio = enforceSilentAudio(call, () => {
      if (isCurrent(current)) void startCaptions(current).catch((error: unknown) => failSession(current, "Caption start failed", error));
    }, (error) => { void failSession(current, "Audio policy failed", error); }, () => {
      if (!isCurrent(current)) return;
      microphoneState.textContent = call.isMuted ? "Muted"
        : call.state === "Connected" ? "UNSAFE: not muted" : "Muted join requested; awaiting connection";
      speakerState.textContent = call.isIncomingAudioMuted ? "Muted" : "Not yet muted";
    });
    if (!isCurrent(current)) {
      current.removeAudio();
      return;
    }
    current.heartbeat = setInterval(() => { void reportStatus(current); }, 15_000);
    await reportStatus(current);
  } catch (error) {
    if (isCurrent(current)) await failSession(current, "Join failed", error);
  }
}

async function startCaptions(current: CaptureSession): Promise<void> {
  if (!isCurrent(current) || !current.call || current.captionsStarted) return;
  current.captionsStarted = true;
  current.delivery = new CaptionDelivery(
    (payload, signal) => postFinalCaption(current.accessKey, payload, signal),
    (counts) => {
      current.counts = counts;
      if (session !== current) return;
      postCount.textContent = String(counts.accepted);
      pendingCount.textContent = String(counts.pending);
      failedCount.textContent = String(counts.failed);
    },
    (error) => showError("Final caption delivery failed", error)
  );
  const feature = current.call.feature(Features.Captions);
  current.feature = feature;
  const captionActiveChanged = (): void => {
    if (!isCurrent(current)) return;
    const captions = feature.captions;
    current.state = captions.isCaptionsFeatureActive ? "active" : "connecting";
    captionState.textContent = captions.isCaptionsFeatureActive
      ? `Active (${captions.activeSpokenLanguage || "en-US"})` : "Inactive";
    void reportStatus(current);
  };
  const handleCaption = (event: CaptionEvent): void => {
    if (!isCurrent(current)) return;
    const item = document.createElement("li");
    item.textContent = `${event.timestamp.toLocaleTimeString()} [${event.resultType}] ${event.speaker.displayName || "Unknown participant"}: ${event.spokenText}`;
    captionEvents.prepend(item);
    while (captionEvents.children.length > 50) captionEvents.lastElementChild?.remove();
    const payload = toFinalPayload(current.meetingId!, event);
    if (payload) current.delivery?.enqueue(payload);
  };
  const wire = (): void => {
    current.removeCaptions?.();
    const captions = feature.captions;
    if (captions.kind === "TeamsCaptions") {
      const teams = captions as TeamsCaptions;
      const listener: TeamsCaptionsHandler = handleCaption;
      teams.on("CaptionsReceived", listener);
      teams.on("CaptionsActiveChanged", captionActiveChanged);
      current.removeCaptions = () => {
        teams.off("CaptionsReceived", listener);
        teams.off("CaptionsActiveChanged", captionActiveChanged);
      };
    } else {
      const acs = captions as Captions;
      const listener: CaptionsHandler = handleCaption;
      acs.on("CaptionsReceived", listener);
      acs.on("CaptionsActiveChanged", captionActiveChanged);
      current.removeCaptions = () => {
        acs.off("CaptionsReceived", listener);
        acs.off("CaptionsActiveChanged", captionActiveChanged);
      };
    }
    captionActiveChanged();
  };
  feature.on("CaptionsKindChanged", wire);
  const oldRemoveState = current.removeState;
  current.removeState = () => {
    oldRemoveState?.();
    feature.off("CaptionsKindChanged", wire);
  };
  wire();
  const captions = feature.captions;
  const english = captions.supportedSpokenLanguages.find(language => language.toLowerCase() === "en-us");
  if (!english) throw new Error("English captions (en-US) are unavailable.");
  captionState.textContent = "Starting English captions";
  await captions.startCaptions({ spokenLanguage: english });
  if (!isCurrent(current)) return;
  captionActiveChanged();
  clientState.textContent = "Silent capture connected";
}

async function reportStatus(current: CaptureSession): Promise<void> {
  if (!current.meetingId || current.reporting || !isCurrent(current)) return;
  current.reporting = true;
  try {
    await postCaptureStatus(current.accessKey, {
      meetingId: current.meetingId,
      state: current.state,
      pendingCaptions: current.counts.pending,
      failedCaptions: current.counts.failed
    }, AbortSignal.any([current.lifetime.signal, AbortSignal.timeout(10_000)]));
    if (isCurrent(current)) heartbeatState.textContent = `Received by server at ${new Date().toLocaleTimeString()}`;
  } catch (error) {
    if (isCurrent(current)) {
      heartbeatState.textContent = "Delivery failed; server capture state can become stale";
      showError("Capture status failed", error);
    }
  } finally {
    current.reporting = false;
  }
}

async function failSession(current: CaptureSession, context: string, error: unknown): Promise<void> {
  if (!isCurrent(current)) return;
  current.state = "error";
  showError(context, error);
  await closeSession(current);
}

function closeSession(current: CaptureSession): Promise<void> {
  if (current.closing) return current.closing;
  current.lifetime.abort();
  current.closing = cleanup(current);
  return current.closing;
}

async function cleanup(current: CaptureSession): Promise<void> {
  leaveButton.disabled = true;
  clearInterval(current.heartbeat);
  current.removeAudio?.();
  current.removeState?.();
  current.removeCaptions?.();
  const lost = current.delivery?.pending ?? 0;
  current.delivery?.stop();
  if (lost) showError("Capture stopped", new Error(`${lost} pending final caption(s) were not delivered.`));
  const release = async (label: string, action: () => void | Promise<void>): Promise<void> => {
    try { await action(); } catch (error) { showError(label, error); }
  };
  if (current.call && current.call.state !== "Disconnected") {
    await release("Call hangup failed", () => current.call!.hangUp());
  }
  if (current.call) await release("Call disposal failed", () => current.call!.dispose());
  if (current.agent) await release("Call agent disposal failed", () => current.agent!.dispose());
  if (current.client) await release("Call client disposal failed", () => current.client!.dispose());
  if (current.credential) await release("Credential disposal failed", () => current.credential!.dispose());
  current.accessKey = "";
  if (session === current) {
    session = undefined;
    accessKeyInput.value = "";
    clientState.textContent = current.state === "error" ? "Capture failed; see diagnostics" : "Stopped";
    callState.textContent = "Not joined";
    lobbyState.textContent = "Not in lobby";
    captionState.textContent = "Stopped";
    microphoneState.textContent = speakerState.textContent = "Not joined";
    heartbeatState.textContent = "Stopped; server state expires after 45 seconds";
    setControls(false);
  }
}

function setControls(busy: boolean): void {
  joinButton.disabled = busy;
  leaveButton.disabled = !busy;
  meetingLinkInput.disabled = displayNameInput.disabled = accessKeyInput.disabled = busy;
}

function showError(context: string, value: unknown): void {
  const message = value instanceof Error ? `${value.name}: ${value.message}` : String(value);
  const prior = diagnostics.textContent === "None" ? "" : `${diagnostics.textContent}\n\n`;
  diagnostics.textContent = `${prior}${new Date().toISOString()} — ${context}\n${message}`.slice(-8000);
}

function element<T extends HTMLElement>(id: string): T {
  const value = document.getElementById(id);
  if (!value) throw new Error(`Required element #${id} was not found.`);
  return value as T;
}
