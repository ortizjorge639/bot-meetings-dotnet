import type {
  CaptionsInfo,
  CaptionsResultType,
  TeamsCaptionsInfo
} from "@azure/communication-calling";
import {
  getIdentifierRawId,
  type CommunicationIdentifier
} from "@azure/communication-common";

export interface AcsTokenResponse {
  userId: string;
  token: string;
  expiresOn: string;
}

export interface ResolvedMeetingResponse {
  meetingId: string;
}

export interface LiveCaptionPayload {
  meetingId: string;
  speakerId: string;
  speakerDisplayName: string;
  spokenText: string;
  timestamp: string;
  resultType: "Final";
}

export type CaptionEvent = CaptionsInfo | TeamsCaptionsInfo;

export function parseTokenResponse(value: unknown): AcsTokenResponse {
  if (!isRecord(value)) {
    throw new Error("The token endpoint returned a non-object response.");
  }

  const { userId, token, expiresOn } = value;
  if (
    typeof userId !== "string" ||
    typeof token !== "string" ||
    typeof expiresOn !== "string" ||
    !userId ||
    !token ||
    Number.isNaN(Date.parse(expiresOn))
  ) {
    throw new Error("The token endpoint response is missing userId, token, or a valid expiresOn.");
  }

  return { userId, token, expiresOn };
}

export function parseResolvedMeetingResponse(value: unknown): ResolvedMeetingResponse {
  if (!isRecord(value)) {
    throw new Error("The meeting resolver returned a non-object response.");
  }

  const { meetingId } = value;
  if (typeof meetingId !== "string" || !meetingId.trim()) {
    throw new Error("The meeting resolver response is missing meetingId.");
  }

  return { meetingId: meetingId.trim() };
}

export function toFinalPayload(
  meetingId: string,
  event: CaptionEvent
): LiveCaptionPayload | undefined {
  if (event.resultType !== ("Final" satisfies CaptionsResultType)) {
    return undefined;
  }

  const identifier = event.speaker.identifier;
  const displayName = event.speaker.displayName?.trim() || "Unknown participant";

  return {
    meetingId,
    speakerId: identifier ? getIdentifierRawId(identifier as CommunicationIdentifier) : `unknown:${displayName}`,
    speakerDisplayName: displayName,
    spokenText: event.spokenText,
    timestamp: event.timestamp.toISOString(),
    resultType: "Final"
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
