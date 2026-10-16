import { describe, expect, it } from "vitest";
import type { CaptionsInfo } from "@azure/communication-calling";
import {
  parseResolvedMeetingResponse,
  parseTokenResponse,
  toFinalPayload
} from "./contracts";

describe("toFinalPayload", () => {
  const baseEvent: Omit<CaptionsInfo, "resultType"> = {
    speaker: {
      identifier: { kind: "communicationUser", communicationUserId: "8:acs:speaker-1" },
      displayName: "Ada",
      endpointDetails: []
    },
    spokenLanguage: "en-us",
    spokenText: "A final thought.",
    timestamp: new Date("2026-10-16T15:00:00.000Z")
  };

  it("ignores partial caption events", () => {
    expect(
      toFinalPayload("meeting-1", { ...baseEvent, resultType: "Partial" })
    ).toBeUndefined();
  });

  it("creates the server contract for final caption events", () => {
    expect(toFinalPayload("meeting-1", { ...baseEvent, resultType: "Final" })).toEqual({
      meetingId: "meeting-1",
      speakerId: "8:acs:speaker-1",
      speakerDisplayName: "Ada",
      spokenText: "A final thought.",
      timestamp: "2026-10-16T15:00:00.000Z",
      resultType: "Final"
    });
  });
});

describe("parseTokenResponse", () => {
  it("accepts the expected token contract", () => {
    const response = {
      userId: "8:acs:user",
      token: "runtime-secret",
      expiresOn: "2026-10-16T16:00:00.000Z"
    };
    expect(parseTokenResponse(response)).toEqual(response);
  });

  it("rejects malformed responses", () => {
    expect(() => parseTokenResponse({ token: "only-a-token" })).toThrow(
      "missing userId, token, or a valid expiresOn"
    );
  });
});

describe("parseResolvedMeetingResponse", () => {
  it("accepts and trims the resolved meeting ID", () => {
    expect(parseResolvedMeetingResponse({ meetingId: " meeting-1 " })).toEqual({
      meetingId: "meeting-1"
    });
  });

  it.each([null, {}, { meetingId: "" }, { meetingId: 42 }])(
    "rejects malformed resolver response %#",
    (response) => {
      expect(() => parseResolvedMeetingResponse(response)).toThrow(
        /meeting resolver.*(non-object|missing meetingId)/i
      );
    }
  );
});
