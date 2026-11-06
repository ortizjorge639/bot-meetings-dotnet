import type { Call, CallAgent, JoinCallOptions, PropertyChangedEvent } from "@azure/communication-calling";

export const silentJoinOptions = {
  audioOptions: { muted: true }
} satisfies JoinCallOptions;

export type AudioCall = Pick<Call,
  "state" | "isMuted" | "isIncomingAudioMuted" | "muteIncomingAudio" | "hangUp" | "on" | "off">;

export function joinSilentCall(agent: Pick<CallAgent, "join">, meetingLink: string): Call {
  return agent.join({ meetingLink }, silentJoinOptions);
}

export function enforceSilentAudio(
  call: AudioCall,
  ready: () => void,
  failed: (error: unknown) => void,
  changed: () => void
): () => void {
  let stopped = false;
  let applying = false;
  let verified = false;
  const fail = (error: unknown): void => {
    if (stopped) return;
    stopped = true;
    detach();
    failed(error);
  };
  const inspect: PropertyChangedEvent = () => {
    if (stopped) return;
    changed();
    if (!call.isMuted) {
      void fail(new Error("ACS microphone is not muted. Capture stopped."));
      return;
    }
    if (verified && !call.isIncomingAudioMuted) {
      void fail(new Error("ACS speaker output is not muted. Capture stopped."));
      return;
    }
    if (call.state !== "Connected" || applying || verified) return;
    applying = true;
    void call.muteIncomingAudio().then(() => {
      if (stopped) return;
      if (!call.isMuted || !call.isIncomingAudioMuted) {
        return fail(new Error("Silent ACS audio state could not be confirmed."));
      }
      verified = true;
      changed();
      ready();
    }).catch(fail);
  };
  const detach = (): void => {
    call.off("stateChanged", inspect);
    call.off("isMutedChanged", inspect);
    call.off("isIncomingAudioMutedChanged", inspect);
  };
  call.on("stateChanged", inspect);
  call.on("isMutedChanged", inspect);
  call.on("isIncomingAudioMutedChanged", inspect);
  inspect();
  return () => {
    stopped = true;
    detach();
  };
}
