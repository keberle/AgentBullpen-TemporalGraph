// Builds tokens.css FROM tokens.ts. Run `npm run tokens` after editing tokens.ts;
// tokens.test.ts fails if the committed tokens.css has drifted.
//
// Output has two parts:
//   1. Plain CSS custom properties (--abp-*) usable without Tailwind.
//   2. A Tailwind v4 `@theme inline` block mapping utilities onto those
//      properties. Tailwind's default palette, type scale and radii are
//      cleared, so only kit tokens exist as utilities.

import { colors, fluidSizes, fontSizes, fonts, radii, spacingUnit } from "./tokens.ts";

export function tokensCss(): string {
  const out: string[] = [];
  const decl = (name: string, value: string | number) => out.push(`  ${name}: ${value};`);

  out.push("/* GENERATED from tokens.ts by `npm run tokens` — do not edit by hand. */", "");

  out.push(":root {");
  for (const c of colors) decl(`--abp-${c.id}`, c.value);
  for (const f of fonts) decl(`--abp-font-${f.id}`, f.stack);
  for (const s of fontSizes) decl(`--abp-text-${s}`, `${s}px`);
  for (const [k, v] of Object.entries(fluidSizes)) decl(`--abp-text-${k}`, v);
  for (const r of radii) decl(`--abp-radius-${r}`, `${r}px`);
  decl("--abp-spacing", spacingUnit);
  out.push("}", "");

  out.push("@theme inline {");
  decl("--color-*", "initial");
  decl("--font-serif", "initial");
  decl("--text-*", "initial");
  decl("--radius-*", "initial");
  decl("--color-transparent", "transparent");
  for (const c of colors) decl(`--color-${c.id}`, `var(--abp-${c.id})`);
  for (const f of fonts) decl(`--font-${f.id}`, `var(--abp-font-${f.id})`);
  for (const s of fontSizes) decl(`--text-${s}`, `var(--abp-text-${s})`);
  for (const k of Object.keys(fluidSizes)) decl(`--text-${k}`, `var(--abp-text-${k})`);
  for (const r of radii) decl(`--radius-${r}`, `var(--abp-radius-${r})`);
  decl("--spacing", "var(--abp-spacing)");
  out.push("}", "");

  return out.join("\n");
}
