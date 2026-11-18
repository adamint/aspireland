// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import entryHtml from "../../index.html?raw";
import { TransitBrand, TransitMark } from "./TransitBrand";

afterEach(cleanup);

it("shares the favicon asset across wordmarks and assistant marks without duplicate accessible names", () => {
  const { container } = render(<><TransitBrand /><TransitMark /></>);
  const entry = new DOMParser().parseFromString(entryHtml, "text/html");
  const favicon = entry.querySelector<HTMLLinkElement>('link[rel="icon"]')!.getAttribute("href");
  const marks = [...container.querySelectorAll<HTMLImageElement>(".transit-mark")];

  expect(marks).toHaveLength(2);
  expect(marks.every((mark) => mark.getAttribute("src") === favicon)).toBe(true);
  expect(marks.every((mark) => mark.alt === "" && mark.getAttribute("aria-hidden") === "true")).toBe(true);
  expect(screen.queryByRole("img")).toBeNull();
  expect(screen.getByText("Aspireland")).toBeTruthy();
  expect(screen.getByText("Transit")).toBeTruthy();
});
