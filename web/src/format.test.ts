import { expect, it } from "vitest";
import { formatDateTimeLocal, formatLocalTime, formatPassengerDateTime, formatSimTimestamp, parseSimulatedLocalInput } from "./format";

it("uses one short locale-aware local-time formatter with dates only when requested", () => {
  const instant = "2026-11-18T15:00:00Z";
  expect(formatLocalTime(instant, { timeZone: "Europe/Lisbon", locale: "en-US" })).toBe("3:00 PM");
  expect(formatLocalTime(instant, { timeZone: "Europe/Lisbon", locale: "pt-PT" })).toBe("15:00");
  expect(formatLocalTime(instant, { timeZone: "America/New_York", locale: "en-US" })).toBe("10:00 AM");
  expect(formatLocalTime(instant, {
    timeZone: "America/New_York",
    locale: "en-US",
    date: "if-different",
    relativeTo: "2026-11-19T15:00:00Z",
  })).toBe("Wed, Nov 18, 10:00 AM");
});

it("shows a short rider date in the local zone across midnight, without zone suffixes", () => {
  const instant = "2026-11-18T00:59:00Z";
  expect(formatPassengerDateTime(instant, "Europe/Lisbon", "en-US")).toBe("Wed, Nov 18, 12:59 AM");
  expect(formatPassengerDateTime(instant, "America/Los_Angeles", "en-US")).toBe("Tue, Nov 17, 4:59 PM");
  expect(formatPassengerDateTime(instant, "Europe/Lisbon", "fr-FR")).toContain("mer. 18 nov.");
});

it("shows short simulation instants in the viewer's zone without repeating the zone", () => {
  const label = formatSimTimestamp("2026-11-17T11:34:24Z", "America/New_York");
  expect(label).toContain("Tue, Nov 17, 6:34 AM");
  expect(label).not.toContain("11:34");
  expect(label).not.toContain("EST");
});

it("round-trips leave-later input in the same IANA zone", () => {
  const instant = parseSimulatedLocalInput("2026-11-17T08:00", "America/New_York");
  expect(instant).toBe("2026-11-17T13:00:00.000Z");
  expect(formatDateTimeLocal(instant, "America/New_York")).toBe("2026-11-17T08:00");
  expect(parseSimulatedLocalInput("2026-07-17T08:00", "America/New_York")).toBe("2026-07-17T12:00:00.000Z");
});

it.each(["", "not a date", "2026-02-30T08:00", "2026-11-17T25:00", "2026-03-08T02:30"])(
  "rejects invalid or nonexistent local departure %s",
  (value) => expect(() => parseSimulatedLocalInput(value, "America/New_York")).toThrow(/valid|exist/i),
);
