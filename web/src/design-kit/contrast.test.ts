import { describe, expect, it } from "vitest";
import { contrastRatio, grade, luminanceGray, mix, parseHex, relativeLuminance } from "./contrast.ts";

// Reference values: WCAG 2.x definitions (w3.org/TR/WCAG22 — relative
// luminance, contrast ratio) and the WebAIM contrast checker's published pairs.
describe("relativeLuminance", () => {
  it("is 0 for black and 1 for white", () => {
    expect(relativeLuminance("#000000")).toBe(0);
    expect(relativeLuminance("#FFFFFF")).toBeCloseTo(1, 10);
  });

  it("weights the primaries by the Rec. 709 coefficients", () => {
    expect(relativeLuminance("#FF0000")).toBeCloseTo(0.2126, 4);
    expect(relativeLuminance("#00FF00")).toBeCloseTo(0.7152, 4);
    expect(relativeLuminance("#0000FF")).toBeCloseTo(0.0722, 4);
  });

  it("accepts 3-digit hex", () => {
    expect(relativeLuminance("#fff")).toBeCloseTo(1, 10);
    expect(parseHex("#abc")).toEqual({ r: 0xaa, g: 0xbb, b: 0xcc });
  });

  it("rejects malformed input", () => {
    expect(() => parseHex("blue")).toThrow();
  });
});

describe("contrastRatio — WCAG reference pairs", () => {
  it("#FFFFFF on #000000 = 21:1", () => {
    expect(contrastRatio("#FFFFFF", "#000000")).toBeCloseTo(21, 10);
  });

  it("is symmetric", () => {
    expect(contrastRatio("#000000", "#FFFFFF")).toBe(contrastRatio("#FFFFFF", "#000000"));
  });

  it("same color = 1:1", () => {
    expect(contrastRatio("#3B82F6", "#3B82F6")).toBe(1);
  });

  it("#767676 on #FFFFFF ≈ 4.54:1 (the lightest gray that passes AA on white)", () => {
    expect(contrastRatio("#767676", "#FFFFFF")).toBeCloseTo(4.54, 2);
    expect(grade("#767676", "#FFFFFF").aa).toBe(true);
  });

  it("#777777 on #FFFFFF ≈ 4.48:1 fails AA", () => {
    expect(contrastRatio("#777777", "#FFFFFF")).toBeCloseTo(4.48, 2);
    expect(grade("#777777", "#FFFFFF").aa).toBe(false);
  });

  it("#595959 on #FFFFFF ≈ 7.00:1 passes AAA", () => {
    expect(contrastRatio("#595959", "#FFFFFF")).toBeCloseTo(7.0, 2);
    expect(grade("#595959", "#FFFFFF").aaa).toBe(true);
  });
});

describe("luminanceGray", () => {
  it("preserves relative luminance to within one 8-bit step", () => {
    for (const hex of ["#00D4FF", "#3B82F6", "#DD9322", "#070B16", "#ECF0F8"]) {
      const g = luminanceGray(hex);
      const { r, g: gg, b } = parseHex(g);
      expect(r).toBe(gg);
      expect(gg).toBe(b);
      expect(Math.abs(relativeLuminance(g) - relativeLuminance(hex))).toBeLessThan(0.005);
    }
  });

  it("leaves neutral grays unchanged", () => {
    expect(luminanceGray("#767676")).toBe("#767676");
  });
});

describe("mix", () => {
  it("returns the endpoints at t = 0 and t = 1", () => {
    expect(mix("#000000", "#FFFFFF", 0)).toBe("#000000");
    expect(mix("#000000", "#FFFFFF", 1)).toBe("#FFFFFF");
  });
});
