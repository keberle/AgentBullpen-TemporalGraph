// AgentBullpen design kit — SINGLE source of truth for every token.
//
// fluent-theme.ts builds the Fluent v9 theme from this file; tokens.css is
// generated from it (`npm run tokens`, drift-checked by a unit test).
//
// RULE: nothing invented. Every value is either an approved decision
// (the design brief) or copied from the approved
// mockups. Where neither had a value, the nearest
// mockup value is used and the line is marked [gap].

export type TokenStatus = "approved" | "candidate";

export type ColorToken = {
  /** Stable id; also the CSS custom property suffix (--abp-<id>) and Tailwind color name. */
  id: string;
  name: string;
  value: string;
  status: TokenStatus;
  source: string;
  /** "brief" = a row in the design brief; "mockups" = only found in the mockup markup. */
  origin: "brief" | "mockups";
};

const openai = "Measured on openai.com";

export const colors: readonly ColorToken[] = [
  { id: "page", name: "Page background", value: "#000000", status: "approved", source: openai, origin: "brief" },
  { id: "panel", name: "Panel / raised surface", value: "#1F1F1F", status: "approved", source: openai, origin: "brief" },
  { id: "fg", name: "Primary text", value: "#FFFFFF", status: "approved", source: openai, origin: "brief" },
  { id: "fg-muted", name: "Secondary text", value: "rgba(255,255,255,0.6)", status: "approved", source: openai, origin: "brief" },
  { id: "divider", name: "Divider / hairline", value: "rgba(255,255,255,0.12)", status: "approved", source: openai, origin: "brief" },
  { id: "outline", name: "Outline (secondary buttons, inputs)", value: "rgba(255,255,255,0.2)", status: "approved", source: openai, origin: "brief" },
  { id: "accent", name: "Accent", value: "#60A5FA", status: "approved", source: "Tailwind blue-400 — approved for now", origin: "brief" },
  { id: "on-accent", name: "Label on accent buttons", value: "#000000", status: "approved", source: "White on the accent fails contrast", origin: "brief" },
  {
    id: "fg-faint",
    name: "Placeholder text",
    value: "rgba(255,255,255,0.4)",
    status: "approved",
    source: "Mockups: image-slot labels (Public, Challenge) and input placeholders (SignIn). Not in the brief.",
    origin: "mockups",
  },
  {
    id: "track",
    name: "Chart grid / bar track",
    value: "rgba(255,255,255,0.08)",
    status: "approved",
    source: "Usage mockup: chart grid lines and breakdown bar tracks. Not in the brief.",
    origin: "mockups",
  },
  // The site's kit: error text and borders. The same token, name and value as the site's copy of the
  // kit, so the two stay in sync. Here: the chat's error bubble.
  { id: "error", name: "Error text and borders", value: "#F87171", status: "approved", source: "Tailwind red-400 — approved", origin: "mockups" },
  {
    id: "warning",
    name: "Warning (an allowance running low)",
    value: "#F59E0B",
    status: "approved",
    source: "Budget chat mockup: the allowance meter under 10 % (Tailwind amber-500). Not in the brief.",
    origin: "mockups",
  },
];

export function color(id: string): string {
  const t = colors.find((c) => c.id === id);
  if (!t) throw new Error(`Unknown color token: ${id}`);
  return t.value;
}

// ── Type ──────────────────────────────────────────────────────────────────
export type FontToken = { id: string; name: string; family: string; stack: string; weights: readonly number[]; status: TokenStatus; source: string };

export const fonts: readonly FontToken[] = [
  {
    id: "sans",
    name: "Open Sans",
    family: "Open Sans Variable",
    stack: '"Open Sans Variable", "Open Sans", sans-serif',
    weights: [400, 600, 700, 800],
    status: "approved",
    source: "Approved",
  },
  {
    id: "mono",
    name: "JetBrains Mono",
    family: "JetBrains Mono Variable",
    stack: '"JetBrains Mono Variable", "JetBrains Mono", monospace',
    weights: [400],
    status: "candidate",
    source: "Still a candidate. Used only in the Docs code block.",
  },
];

/**
 * Every fixed font size used in the mockups, named by its pixel value so a
 * class reads like the mockup (`text-15` = 15px).
 */
export const fontSizes = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 22, 26, 28, 30, 32, 36, 40, 44, 48] as const;

/** Fluid sizes, verbatim from the mockups. */
export const fluidSizes = {
  /** Public hero “Challenge accepted.” */
  hero: "clamp(36px, 3.9vw, 56px)",
  /** Public hero quote */
  quote: "clamp(26px, 2.6vw, 38px)",
  /** Challenge page title */
  "challenge-title": "clamp(40px, 5vw, 64px)",
} as const;

/** Corner radii used in the mockups (px). 3, 4, 14 and 999 (pill) come from the later mockups. */
export const radii = [3, 4, 6, 8, 10, 12, 14, 16, 999] as const;

/**
 * Spacing unit for Tailwind's `--spacing`. 1px makes every spacing class read
 * as the mockup's pixel value (`gap-24` = 24px, `px-28` = 28px).
 */
export const spacingUnit = "1px";
