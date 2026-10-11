import { describe, expect, it } from "vitest";
import { colors } from "../tokens.ts";
import { isPerceptionKind, perceptionIcons, perceptionKinds } from "./perception.tsx";

// The PERCEIVES kinds, as the schema lists them; the enum's names are the icon keys.
const groups = {
  credence: ["believes", "doubts", "disbelieves"],
  trust: ["trusts", "distrusts", "suspects"],
  threat: ["threatened", "reassured"],
  feeling: ["fearful", "hopeful", "curious", "indifferent"],
};

describe("perception icons", () => {
  it("has exactly the twelve kinds, keyed by the enum's names, each in its group", () => {
    expect([...perceptionKinds]).toEqual(Object.values(groups).flat());
    expect(Object.keys(perceptionIcons).sort()).toEqual([...perceptionKinds].sort());
    for (const [group, kinds] of Object.entries(groups)) for (const k of kinds) expect(perceptionIcons[k as keyof typeof perceptionIcons].group, k).toBe(group);
    expect(isPerceptionKind("believes")).toBe(true);
    for (const other of ["witnessed", "toString", "", null, undefined]) expect(isPerceptionKind(other)).toBe(false);
  });

  it("tells the twelve apart by shape: no two share their paths or their description", () => {
    const drawn = perceptionKinds.map((k) => `${perceptionIcons[k].stroke}|${perceptionIcons[k].fill ?? ""}`);
    expect(new Set(drawn).size).toBe(12);
    expect(new Set(perceptionKinds.map((k) => perceptionIcons[k].shape)).size).toBe(12);
    // Colour is never the only difference: two icons of different tones are different shapes too (covered by the line above), and
    // every path is real path data on the 16 × 16 box.
    for (const k of perceptionKinds) {
      const { stroke, fill } = perceptionIcons[k];
      for (const d of [stroke, fill ?? "M0 0"]) expect(d, k).toMatch(/^M[\d .\-a-zA-Z]+$/);
    }
  });

  it("uses the kit's existing colour tokens only, the error token for the hostile kinds", () => {
    const ids = new Set(colors.map((c) => c.id));
    for (const k of perceptionKinds) expect(ids.has(perceptionIcons[k].tone), k).toBe(true);
    for (const k of ["disbelieves", "distrusts", "threatened"] as const) expect(perceptionIcons[k].tone, k).toBe("error");
    for (const k of ["believes", "trusts", "reassured", "hopeful"] as const) expect(perceptionIcons[k].tone, k).toBe("fg");
  });
});
