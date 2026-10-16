import {
  CallClient,
  Features,
  type Call,
  type Captions,
  type CaptionsCallFeature,
  type CaptionsHandler,
  type PropertyChangedEvent,
  type TeamsCaptions,
  type TeamsCaptionsHandler
} from "@azure/communication-calling";
import { AzureCommunicationTokenCredential } from "@azure/communication-common";
import { postFinalCaption, requestAcsToken, resolveMeeting } from "./api";
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
const postCount = element<HTMLElement>("post-count");
const captionEvents = element<HTMLOListElement>("caption-events");
const diagnostics = element<HTMLPreElement>("diagnostics");

let activeCall: Call | undefined;
let captionsFeature: CaptionsCallFeature | undefined;
let removeCaptionListeners: (() => void) | undefined;
let captionStart: Promise<void> | undefined;
let postedFinals = 0;
let session: { accessKey: string; meetingId: string } | undefined;

joinForm.addEventListener("submit", (event) => {
  event.preventDefault();
  void joinMeeting();
});

leaveButton.addEventListener("click", () => {
  void leaveMeeting();
});

window.addEventListener("unhandledrejection", (event) => {
  showError("Unhandled browser promise rejection", event.reason);
});

async function joinMeeting(): Promise<void> {
  if (activeCall) {
    showError("Join", new Error("A call is already active. Leave it before joining another meeting."));
    return;
  }

  clearErrors();
  setControls(true);
  postedFinals = 0;
  postCount.textContent = "0";
  captionEvents.replaceChildren();

  const accessKey = accessKeyInput.value;
  const meetingLink = meetingLinkInput.value.trim();

  try {
    clientState.textContent = "Resolving Teams meeting";
    const resolvedMeeting = await resolveMeeting(accessKey, meetingLink);
    session = { accessKey, meetingId: resolvedMeeting.meetingId };

    clientState.textContent = "Requesting short-lived ACS token";
    const token = await requestAcsToken(accessKey);
    clientState.textContent = `Token received for ${token.userId}; expires ${new Date(token.expiresOn).toLocaleString()}`;

    const credential = new AzureCommunicationTokenCredential(token.token);
    const callClient = new CallClient();
    clientState.textContent = "Creating ACS call agent";
    const callAgent = await callClient.createCallAgent(credential, {
      displayName: displayNameInput.value.trim()
    });

    clientState.textContent = "Call agent ready";
    const call = callAgent.join({ meetingLink });
    activeCall = call;
    call.on("stateChanged", handleCallStateChanged);
    handleCallStateChanged();
    leaveButton.disabled = false;
  } catch (error) {
    showError("Join failed", error);
    await resetCall();
  } finally {
    joinButton.disabled = activeCall !== undefined;
  }
}

const handleCallStateChanged: PropertyChangedEvent = (): void => {
  const call = activeCall;
  if (!call) return;

  callState.textContent = call.state;
  lobbyState.textContent =
    call.state === "InLobby" ? "Waiting for organizer admission" : "Not in lobby";

  if (call.state === "Connected") {
    void initializeCaptions(call);
  } else if (call.state === "Disconnected") {
    captionState.textContent = "Stopped";
    if (call.callEndReason) {
      showError(
        "Call disconnected",
        new Error(`ACS end reason code ${call.callEndReason.code}, subcode ${call.callEndReason.subCode}`)
      );
    }
    void resetCall();
  }
};

async function initializeCaptions(call: Call): Promise<void> {
  if (captionStart) return captionStart;

  captionsFeature = call.feature(Features.Captions);
  captionsFeature.on("CaptionsKindChanged", wireCaptionListeners);
  wireCaptionListeners();

  const captions = captionsFeature.captions;
  const english = captions.supportedSpokenLanguages.find(
    (language) => language.toLowerCase() === "en-us"
  );
  if (!english) {
    throw new Error(
      `English captions (en-US) are unavailable. Supported languages: ${captions.supportedSpokenLanguages.join(", ")}`
    );
  }

  captionState.textContent = "Starting English captions";
  captionStart = captions
    .startCaptions({ spokenLanguage: english })
    .catch((error: unknown) => {
      captionStart = undefined;
      captionState.textContent = "Start failed";
      showError("Could not start captions", error);
      throw error;
    });
  return captionStart;
}

const handleCaptionsActiveChanged: PropertyChangedEvent = (): void => {
  const captions = captionsFeature?.captions;
  captionState.textContent = captions?.isCaptionsFeatureActive
    ? `Active (${captions.activeSpokenLanguage || "en-US"})`
    : "Inactive";
};

const handleCaption = (event: CaptionEvent): void => {
  const item = document.createElement("li");
  item.textContent = `${event.timestamp.toLocaleTimeString()} [${event.resultType}] ${event.speaker.displayName || "Unknown participant"}: ${event.spokenText}`;
  captionEvents.prepend(item);
  while (captionEvents.children.length > 50) {
    captionEvents.lastElementChild?.remove();
  }

  if (!session) return;
  const payload = toFinalPayload(session.meetingId, event);
  if (!payload) return;

  void postFinalCaption(session.accessKey, payload)
    .then(() => {
      postedFinals += 1;
      postCount.textContent = postedFinals.toString();
    })
    .catch((error: unknown) => showError("Final caption delivery failed", error));
};

function wireCaptionListeners(): void {
  removeCaptionListeners?.();
  const captions = captionsFeature?.captions;
  if (!captions) return;

  if (captions.kind === "TeamsCaptions") {
    const teamsCaptions = captions as TeamsCaptions;
    const listener: TeamsCaptionsHandler = handleCaption;
    teamsCaptions.on("CaptionsActiveChanged", handleCaptionsActiveChanged);
    teamsCaptions.on("CaptionsReceived", listener);
    removeCaptionListeners = () => {
      teamsCaptions.off("CaptionsReceived", listener);
      teamsCaptions.off("CaptionsActiveChanged", handleCaptionsActiveChanged);
    };
  } else {
    const acsCaptions = captions as Captions;
    const listener: CaptionsHandler = handleCaption;
    acsCaptions.on("CaptionsActiveChanged", handleCaptionsActiveChanged);
    acsCaptions.on("CaptionsReceived", listener);
    removeCaptionListeners = () => {
      acsCaptions.off("CaptionsReceived", listener);
      acsCaptions.off("CaptionsActiveChanged", handleCaptionsActiveChanged);
    };
  }
  handleCaptionsActiveChanged();
}

async function leaveMeeting(): Promise<void> {
  clearErrors();
  leaveButton.disabled = true;
  try {
    await activeCall?.hangUp();
  } catch (error) {
    showError("Leave failed", error);
  } finally {
    await resetCall();
  }
}

async function resetCall(): Promise<void> {
  if (activeCall) {
    activeCall.off("stateChanged", handleCallStateChanged);
  }
  if (captionsFeature) {
    captionsFeature.off("CaptionsKindChanged", wireCaptionListeners);
  }
  removeCaptionListeners?.();
  removeCaptionListeners = undefined;
  captionsFeature = undefined;
  captionStart = undefined;
  activeCall = undefined;
  session = undefined;
  accessKeyInput.value = "";
  setControls(false);
}

function setControls(joiningOrJoined: boolean): void {
  joinButton.disabled = joiningOrJoined;
  leaveButton.disabled = !activeCall;
  meetingLinkInput.disabled = joiningOrJoined;
  displayNameInput.disabled = joiningOrJoined;
  accessKeyInput.disabled = joiningOrJoined;
}

function showError(context: string, value: unknown): void {
  const message = value instanceof Error ? `${value.name}: ${value.message}` : String(value);
  const prior = diagnostics.textContent === "None" ? "" : `${diagnostics.textContent}\n\n`;
  diagnostics.textContent = `${prior}${new Date().toISOString()} — ${context}\n${message}`;
}

function clearErrors(): void {
  diagnostics.textContent = "None";
}

function element<T extends HTMLElement>(id: string): T {
  const value = document.getElementById(id);
  if (!value) throw new Error(`Required element #${id} was not found.`);
  return value as T;
}
