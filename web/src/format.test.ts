import { expect, it } from "vitest";
import { formatDateTimeLocal, formatPassengerDateTime, formatSimTimestamp, parseSimulatedLocalInput } from "./format";

it("shows a friendly rider date in the local zone across midnight, without zone suffixes", () => {
  const instant = "2026-11-18T00:59:00Z";
  expect(formatPassengerDateTime(instant, "Europe/Lisbon", "en-US")).toBe("Wednesday, November 18 at 12:59 AM");
  expect(formatPassengerDateTime(instant, "America/Los_Angeles", "en-US")).toBe("Tuesday, November 17 at 4:59 PM");
  expect(formatPassengerDateTime(instant, "Europe/Lisbon", "fr-FR")).toContain("mercredi 18 novembre");
});

it("shows simulation instants in the viewer's zone, including its label", () => {
  const label = formatSimTimestamp("2026-11-17T11:34:24Z", "America/New_York");
  expect(label).toContain("6:34 AM EST");
  expect(label).not.toContain("11:34");
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
