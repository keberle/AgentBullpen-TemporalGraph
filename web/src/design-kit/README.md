# AgentBullpen design kit

The portable kit for agentbullpen.ai and the two public demo repos. It is
**copied** between repos, not published as a package.

## The rule

**Nothing invented.** Every value in `tokens.ts` is an approved decision
(DESIGN-KIT-02 §2) or copied from the approved mockups. Where neither had one,
the nearest mockup value is used and the code is marked `[gap]`.

## Files

| File | Role |
|---|---|
| `tokens.ts` | Single source of truth: colors, fonts, font sizes, radii, spacing unit |
| `tokens-css.ts` → `tokens.css` | Generated CSS variables (`--abp-*`) + Tailwind v4 `@theme` (`npm run tokens`) |
| `fluent-theme.ts` | `buildTheme()`: the Fluent UI v9 dark theme from `tokens.ts` |
| `fonts.ts` | Self-hosted Open Sans (approved) and JetBrains Mono (candidate) |
| `components/` | `Wordmark`, `TopBar`, `UserMenu`, `KitButton`, `TextField`, `ArtSlot` / `ImageSlot`, `Segmented`, `DayBars` (inline-SVG per-day bars; no chart library) |
| `icons/perception.tsx` | The twelve perception icons, keyed by the `PERCEIVES` kinds: `believes`, `doubts`, `disbelieves`, `trusts`, `distrusts`, `suspects`, `threatened`, `reassured`, `fearful`, `hopeful`, `curious`, `indifferent`. Drawn for the kit (16 × 16 outlines, no icon library), told apart by shape; `fg`, and `error` for the hostile ones. `PerceptionGlyph` (inline SVG) and `drawPerceptionIcon` (canvas) |
| `format.ts` | Mockup number formats (`1.2M`, `37k`, `37,800`) |
| `assets/` | `inventory.ts` (the two hero cutouts of the original kit are people's likenesses and are kept outside the public repo; this app uses neither) |
| `contrast.ts` | WCAG contrast utilities (tested; not used by the pages) |

The Tailwind entry the kit expects lives at `web/src/kit.css`.

`UserMenu` is its own module: a standalone demo renders `<TopBar left={…} />`
and never imports it.

## Copying into another repo

1. Copy this folder.
2. Install `@fluentui/react-components`, `tailwindcss` + `@tailwindcss/vite`,
   `@fontsource-variable/open-sans`, `@fontsource-variable/jetbrains-mono`.
3. Tailwind entry CSS (no preflight, so Fluent is never reset):
   ```css
   @layer theme, base, components, utilities;
   @import "tailwindcss/theme.css" layer(theme);
   @import "tailwindcss/utilities.css" layer(utilities);
   @import "./design-kit/tokens.css";
   ```
4. `import "./design-kit/fonts.ts"` once and wrap the app in
   `<FluentProvider theme={buildTheme()}>`. Put page classes on a div inside
   the provider, not on the provider: Fluent copies the provider's className
   onto its portal node.

## Utilities

Colors: `bg-page`, `bg-panel`, `text-fg`, `text-fg-muted`, `text-fg-faint`,
`border-divider`, `border-outline`, `bg-accent` / `text-accent`,
`text-on-accent`. Sizes are named by pixel value (`text-15` = 15px), plus
`text-hero`, `text-quote` and `text-challenge-title`. Radii are
`rounded-6|8|10|12|16`. The spacing unit is 1px, so `gap-24` = 24px.
Tailwind's own palette, type scale and radii are cleared.
