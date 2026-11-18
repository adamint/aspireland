import { expect, it } from "vitest";
import { formatDateTimeLocal, formatSimTimestamp, parseSimulatedLocalInput } from "./format";

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
