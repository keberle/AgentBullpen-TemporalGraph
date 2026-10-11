// WCAG 2.x relative luminance and contrast ratio.
// Formulas: https://www.w3.org/TR/WCAG22/#dfn-relative-luminance and
// https://www.w3.org/TR/WCAG22/#dfn-contrast-ratio. Every ratio shown in the
// kit is computed here at render time; none are hand-typed.
//
// Note: WCAG 2.2's definition uses 0.04045 as the sRGB linearization threshold
// (older text said 0.03928; the difference is below display precision).

export type Rgb = { r: number; g: number; b: number }; // 0–255

export function parseHex(hex: string): Rgb {
  const m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) throw new Error(`Not a hex color: ${hex}`);
  let h = m[1];
  if (h.length === 3) h = h.split("").map((c) => c + c).join("");
  return {
    r: parseInt(h.slice(0, 2), 16),
    g: parseInt(h.slice(2, 4), 16),
    b: parseInt(h.slice(4, 6), 16),
  };
}

export function toHex({ r, g, b }: Rgb): string {
  const c = (v: number) => Math.round(Math.min(255, Math.max(0, v))).toString(16).padStart(2, "0");
  return `#${c(r)}${c(g)}${c(b)}`.toUpperCase();
}

/** sRGB channel (0–255) → linear light (0–1). */
export function linearize(channel: number): number {
  const c = channel / 255;
  return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

/** Linear light (0–1) → sRGB channel (0–255). Inverse of linearize. */
export function delinearize(linear: number): number {
  const c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * linear ** (1 / 2.4) - 0.055;
  return Math.round(Math.min(1, Math.max(0, c)) * 255);
}

/** WCAG relative luminance, 0 (black) – 1 (white). */
export function relativeLuminance(hex: string): number {
  const { r, g, b } = parseHex(hex);
  return 0.2126 * linearize(r) + 0.7152 * linearize(g) + 0.0722 * linearize(b);
}

/** WCAG contrast ratio, 1 – 21. Order of arguments does not matter. */
export function contrastRatio(a: string, b: string): number {
  const la = relativeLuminance(a);
  const lb = relativeLuminance(b);
  const [hi, lo] = la >= lb ? [la, lb] : [lb, la];
  return (hi + 0.05) / (lo + 0.05);
}

/**
 * The neutral gray with the SAME WCAG relative luminance as `hex`.
 * This is the "true luminance grayscale" the kit shows beside every color:
 * contrast ratios between grays equal the ratios between the original colors.
 */
export function luminanceGray(hex: string): string {
  const v = delinearize(relativeLuminance(hex));
  return toHex({ r: v, g: v, b: v });
}

export type WcagGrade = {
  ratio: number;
  /** 1.4.3 normal text ≥ 4.5 */
  aa: boolean;
  /** 1.4.3 large text (≥ 24px, or ≥ 18.66px bold) ≥ 3 — also 1.4.11 non-text ≥ 3 */
  aaLarge: boolean;
  /** 1.4.6 normal text ≥ 7 */
  aaa: boolean;
};

export function grade(fg: string, bg: string): WcagGrade {
  const ratio = contrastRatio(fg, bg);
  return { ratio, aa: ratio >= 4.5, aaLarge: ratio >= 3, aaa: ratio >= 7 };
}

/** Linear-light mix of two colors (t = 0 → a, t = 1 → b). Used to derive ramps, never to pick values. */
export function mix(a: string, b: string, t: number): string {
  const A = parseHex(a);
  const B = parseHex(b);
  const ch = (x: number, y: number) => delinearize(linearize(x) * (1 - t) + linearize(y) * t);
  return toHex({ r: ch(A.r, B.r), g: ch(A.g, B.g), b: ch(A.b, B.b) });
}
