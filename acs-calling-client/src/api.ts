import {
  parseResolvedMeetingResponse,
  parseTokenResponse,
  requireAcceptedReceipt,
  type AcsTokenResponse,
  type LiveCaptionPayload,
  type ResolvedMeetingResponse
} from "./contracts";

const AUTH_HEADER = "X-Live-Context-Key";

export class HttpError extends Error {
  constructor(message: string, readonly status: number, readonly retryAfterMs = 0) {
    super(message);
    this.name = "HttpError";
  }
}

export async function requestAcsToken(accessKey: string, signal?: AbortSignal): Promise<AcsTokenResponse> {
  const response = await fetch("/api/acs/token", {
    method: "GET",
    cache: "no-store",
    signal,
    headers: { [AUTH_HEADER]: accessKey }
  });

  if (!response.ok) {
    throw await responseError("ACS token request", response);
  }

  return parseTokenResponse(await response.json());
}

export async function resolveMeeting(
  accessKey: string,
  meetingLink: string,
  signal?: AbortSignal
): Promise<ResolvedMeetingResponse> {
  const response = await fetch("/api/live-context/meetings/resolve", {
    method: "POST",
    signal,
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
  payload: LiveCaptionPayload,
  signal?: AbortSignal
): Promise<void> {
  const response = await fetch("/api/live-context/captions", {
    method: "POST",
    signal,
    headers: {
      "Content-Type": "application/json",
      [AUTH_HEADER]: accessKey
    },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    throw await responseError("Final caption POST", response);
  }
  requireAcceptedReceipt(await response.json());
}

export async function postCaptureStatus(
  accessKey: string,
  status: { meetingId: string; state: string; pendingCaptions: number; failedCaptions: number },
  signal: AbortSignal
): Promise<void> {
  const response = await fetch("/api/live-context/status", {
    method: "POST",
    headers: { "Content-Type": "application/json", [AUTH_HEADER]: accessKey },
    body: JSON.stringify(status),
    signal
  });
  if (!response.ok) throw await responseError("Capture status", response);
}

async function responseError(operation: string, response: Response): Promise<Error> {
  const retryAfter = response.headers.get("Retry-After");
  const seconds = retryAfter === null ? NaN : Number(retryAfter);
  const retryAfterMs = Number.isFinite(seconds) ? Math.max(0, seconds * 1000)
    : Math.max(0, Date.parse(retryAfter ?? "") - Date.now()) || 0;
  const guidance = operation === "Meeting resolution" && response.status === 404
    ? " Use the Join Meeting link from the bot's meeting-start card. If the card is absent, stop and check the meeting-start event."
    : "";
  return new HttpError(
    `${operation} failed: HTTP ${response.status}.${guidance}`,
    response.status,
    retryAfterMs
  );
}
