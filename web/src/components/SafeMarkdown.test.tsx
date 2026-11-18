// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { SafeMarkdown } from "./SafeMarkdown";

afterEach(cleanup);

it("renders paragraphs, emphasis and lists without Markdown markers", () => {
  const { container } = render(<SafeMarkdown text={"Take **Green** toward *Southbank*.\n\n- Enter University\n- Transfer at Central\n\n1. Board AX\n2. Exit Airport"} />);
  expect(container.querySelector("strong")?.textContent).toBe("Green");
  expect(container.querySelector("em")?.textContent).toBe("Southbank");
  expect(screen.getAllByRole("listitem")).toHaveLength(4);
  expect(container.querySelectorAll("ul, ol")).toHaveLength(2);
  expect(container.textContent).not.toContain("*");
});

it("escapes raw HTML and scripts instead of executing or creating elements", () => {
  const { container } = render(<SafeMarkdown text={'<script>window.injected = true</script>\n\n<img src=x onerror="alert(1)"> **Safe**'} />);
  expect(container.querySelector("script, img")).toBeNull();
  expect(container.textContent).toContain("<script>window.injected = true</script>");
  expect(container.textContent).toContain('<img src=x onerror="alert(1)">');
  expect(screen.getByText("Safe").tagName).toBe("STRONG");
});
