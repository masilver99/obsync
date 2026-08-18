import { describe, expect, it } from "vitest";
import { conflictPath } from "../src/paths";

describe("conflictPath", () => {
  it("preserves the folder and extension", () => {
    const result = conflictPath("Notes/Chili.md", "Michael Laptop", new Date("2026-08-16T15:04:05.000Z"));
    expect(result).toBe("Notes/Chili (conflict MichaelLaptop 20260816T150405Z).md");
  });

  it("adds a suffix when the first candidate exists", () => {
    let firstCandidate = true;
    const result = conflictPath("Chili.md", "phone", new Date("2026-08-16T15:04:05.000Z"), () => firstCandidate ? (firstCandidate = false, true) : false);
    expect(result).toContain(" 2).md");
  });
});
