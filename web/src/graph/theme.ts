// The graph tabs' colours and fonts, read from the design kit's CSS tokens at runtime (no literals). The mockup's
// palette maps onto the kit by luminance: Character = fg (white), Location = fg-muted, Event ring = fg-faint, Fact = outline;
// entity edges fg-muted, link edges fg-faint; selection, highlight and halo = accent (the timeline's blue); ground = page.

export type GraphTheme = {
  page: string;
  fg: string;
  fgMuted: string;
  fgFaint: string;
  outline: string;
  accent: string;
  accentHalo: string;
  /** The kit's error token, for the hostile perception icons (their shape says it first). */
  error: string;
  onDisc: string;
  font: string;
};

const token = (cs: CSSStyleDeclaration, name: string, fallback: string) => cs.getPropertyValue(name).trim() || fallback;

/** The kit's tokens as the graph uses them (fallbacks only matter outside a styled page, e.g. tests). */
export function readGraphTheme(): GraphTheme {
  const cs = getComputedStyle(document.documentElement);
  const accent = token(cs, "--abp-accent", "#60A5FA");
  return {
    page: token(cs, "--abp-page", "#000000"),
    fg: token(cs, "--abp-fg", "#FFFFFF"),
    fgMuted: token(cs, "--abp-fg-muted", "rgba(255,255,255,0.6)"),
    fgFaint: token(cs, "--abp-fg-faint", "rgba(255,255,255,0.4)"),
    outline: token(cs, "--abp-outline", "rgba(255,255,255,0.2)"),
    accent,
    accentHalo: withAlpha(accent, 0.22),
    error: token(cs, "--abp-error", "#F87171"),
    onDisc: token(cs, "--abp-page", "#000000"),
    font: token(cs, "--abp-font-sans", "sans-serif"),
  };
}

/** A #rgb / #rrggbb colour with an alpha, as rgba(); anything else unchanged. */
export function withAlpha(color: string, alpha: number): string {
  const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(color.trim());
  if (!m) return color;
  const h = m[1].length === 3 ? [...m[1]].map((c) => c + c).join("") : m[1];
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16));
  return `rgba(${r},${g},${b},${alpha})`;
}

/** A colour (hex or rgba over the black page) as an opaque #rrggbb, for three.js materials. */
export function opaque(color: string, ground = "#000000"): string {
  const c = color.trim();
  if (/^#[0-9a-f]{6}$/i.test(c)) return c;
  if (/^#[0-9a-f]{3}$/i.test(c)) return "#" + [...c.slice(1)].map((x) => x + x).join("");
  const m = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$/i.exec(c);
  if (!m) return c;
  const a = m[4] === undefined ? 1 : Number(m[4]);
  const g = opaque(ground, "#000000");
  const bg = [1, 3, 5].map((i) => parseInt(g.slice(i, i + 2), 16));
  return (
    "#" +
    [1, 2, 3]
      .map((i, k) =>
        Math.round(Number(m[i]) * a + bg[k] * (1 - a))
          .toString(16)
          .padStart(2, "0"),
      )
      .join("")
  );
}
