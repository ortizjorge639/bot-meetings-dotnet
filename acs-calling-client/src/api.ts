import {
  parseResolvedMeetingResponse,
  parseTokenResponse,
  type AcsTokenResponse,
  type LiveCaptionPayload,
  type ResolvedMeetingResponse
} from "./contracts";

const AUTH_HEADER = "X-Live-Context-Key";

export async function requestAcsToken(accessKey: string): Promise<AcsTokenResponse> {
  const response = await fetch("/api/acs/token", {
    method: "GET",
    cache: "no-store",
    headers: { [AUTH_HEADER]: accessKey }
  });

  if (!response.ok) {
    throw await responseError("ACS token request", response);
  }

  return parseTokenResponse(await response.json());
}

export async function resolveMeeting(
  accessKey: string,
  meetingLink: string
): Promise<ResolvedMeetingResponse> {
  const response = await fetch("/api/live-context/meetings/resolve", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      [AUTH_HEADER]: accessKey
    },
    body: JSON.stringify({ meetingLink })
  });

  if (!response.ok) {
    throw await responseError("Meeting resolution", response);
  }

  try {
    return parseResolvedMeetingResponse(await response.json());
  } catch (error) {
    throw new Error(
      `Meeting resolution returned an invalid response: ${error instanceof Error ? error.message : String(error)}`
    );
  }
}

export async function postFinalCaption(
  accessKey: string,
  payload: LiveCaptionPayload
): Promise<void> {
  const response = await fetch("/api/live-context/captions", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      [AUTH_HEADER]: accessKey
    },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    throw await responseError("Final caption POST", response);
  }
}

async function responseError(operation: string, response: Response): Promise<Error> {
  const body = await response.text();
  const detail = body.length > 2_000 ? `${body.slice(0, 2_000)}…` : body;
  return new Error(
    `${operation} failed: HTTP ${response.status} ${response.statusText}${detail ? `\n${detail}` : ""}`
  );
}
