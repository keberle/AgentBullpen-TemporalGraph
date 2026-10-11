// Builds the Fluent UI v9 dark theme FROM tokens.ts.
//
// Fluent derives many control colors from a 16-step brand ramp and expects
// hover/pressed shades. The mockups define none of these, so every brand step
// is the accent and every hover/pressed state equals its rest state. [gap]
// Component shapes (radius, height, padding) come from the kit components in
// ./components, which restyle Fluent to match the mockups.

import { createDarkTheme, type BrandVariants, type Theme } from "@fluentui/react-components";
import { color, fonts } from "./tokens.ts";

const STEPS = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160] as const;

export function buildTheme(): Theme {
  const accent = color("accent");
  const page = color("page");
  const panel = color("panel");
  const fg = color("fg");
  const muted = color("fg-muted");
  const faint = color("fg-faint");
  const divider = color("divider");
  const outline = color("outline");

  // [gap] No brand ramp in the mockups: every step is the accent.
  const brand = Object.fromEntries(STEPS.map((s) => [s, accent])) as unknown as BrandVariants;
  const base = createDarkTheme(brand);

  return {
    ...base,
    fontFamilyBase: fonts.find((f) => f.id === "sans")!.stack,
    fontFamilyMonospace: fonts.find((f) => f.id === "mono")!.stack,

    colorNeutralBackground1: page,
    colorNeutralBackground1Hover: page, // [gap]
    colorNeutralBackground1Pressed: page, // [gap]
    colorNeutralBackground1Selected: page, // [gap]
    colorNeutralBackground2: panel,
    colorNeutralBackground3: panel,
    colorNeutralBackgroundDisabled: panel, // [gap]
    colorTransparentBackgroundHover: "transparent", // [gap]
    colorTransparentBackgroundPressed: "transparent", // [gap]
    // Menu item highlight: TopBar.dc.html shows the hovered item on the divider tint.
    colorSubtleBackgroundHover: divider,
    colorSubtleBackgroundPressed: divider,
    colorSubtleBackgroundSelected: divider,

    colorNeutralForeground1: fg,
    colorNeutralForeground1Hover: fg,
    colorNeutralForeground1Pressed: fg,
    colorNeutralForeground2: muted,
    colorNeutralForeground2Hover: fg,
    colorNeutralForeground3: muted,
    colorNeutralForeground4: faint,
    colorNeutralForegroundDisabled: faint, // [gap]
    colorNeutralForegroundOnBrand: color("on-accent"),

    colorNeutralStroke1: outline,
    colorNeutralStroke1Hover: outline, // [gap]
    colorNeutralStroke1Pressed: outline, // [gap]
    colorNeutralStroke2: divider,
    colorNeutralStrokeAccessible: outline,
    colorNeutralStrokeAccessibleHover: outline, // [gap]
    colorNeutralStrokeAccessiblePressed: outline, // [gap]
    colorNeutralStrokeDisabled: divider, // [gap]

    colorBrandBackground: accent,
    colorBrandBackgroundHover: accent, // [gap]
    colorBrandBackgroundPressed: accent, // [gap]
    colorBrandForeground1: accent,
    colorBrandForeground2: accent,
    colorBrandForegroundLink: accent,
    colorBrandForegroundLinkHover: accent, // [gap]
    colorBrandForegroundLinkPressed: accent, // [gap]
    colorBrandStroke1: accent,
    colorCompoundBrandStroke: accent,
    colorCompoundBrandStrokeHover: accent, // [gap]
    colorCompoundBrandStrokePressed: accent, // [gap]
  };
}
