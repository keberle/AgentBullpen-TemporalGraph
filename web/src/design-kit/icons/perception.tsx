// Perception icons: one glyph per PERCEIVES kind, keyed by the enum's names, which are the icon keys. The graph
// tabs draw one at the midpoint of every PERCEIVES edge ("its render point is a little icon image of the perception represented").
//
// The twelve are told apart by SHAPE, so colour is never the only cue: a tick, a question mark, a cross, a shield,
// a struck-out shield, a magnifying glass, a warning triangle, a heart, a lightning bolt, a rising sun, an eye, a circled dash.
// Colour is second: the kit's `fg`, and the kit's `error` token for the hostile ones (disbelieves, distrusts, threatened, fearful).
// No colour is added to the kit.
//
// Drawn for this kit on a 16 × 16 box as outlines 1.6 wide with round ends (plus a filled dot or pupil where a glyph has one), to
// read at 14–16 px; nothing is taken from an icon library and there is no dependency. One path set, two uses: <PerceptionGlyph>
// (inline SVG: the legend) and drawPerceptionIcon (canvas: the 2D tab's edges and the 3D tab's sprite textures).

/** The PERCEIVES kinds, in the schema's order: credence, trust, threat, feeling. */
export const perceptionKinds = [
  "believes",
  "doubts",
  "disbelieves",
  "trusts",
  "distrusts",
  "suspects",
  "threatened",
  "reassured",
  "fearful",
  "hopeful",
  "curious",
  "indifferent",
] as const;
export type PerceptionKind = (typeof perceptionKinds)[number];

export type PerceptionIcon = {
  group: "credence" | "trust" | "threat" | "feeling";
  /** The kit colour token the glyph is drawn in (`--abp-fg` / `--abp-error`). */
  tone: "fg" | "error";
  /** What the glyph is, in words (the SVG's title; also how to tell it from the others without colour). */
  shape: string;
  /** SVG path data on the 16 × 16 box, stroked. */
  stroke: string;
  /** SVG path data filled in the same colour (dots, the eye's pupil). */
  fill?: string;
};

/** The icons' box (their path data's coordinates) and outline width in it. */
export const PERCEPTION_ICON_BOX = 16;
export const PERCEPTION_ICON_STROKE = 1.6;

/** A full circle as path data. */
const disc = (cx: number, cy: number, r: number) => `M${cx - r} ${cy}a${r} ${r} 0 1 0 ${2 * r} 0a${r} ${r} 0 1 0 ${-2 * r} 0Z`;
const shield = "M8 2 13 4V8C13 11 10.6 13.3 8 14.2 5.4 13.3 3 11 3 8V4Z";

export const perceptionIcons: Record<PerceptionKind, PerceptionIcon> = {
  // Credence
  believes: { group: "credence", tone: "fg", shape: "tick", stroke: "M3 8.5 6.5 12 13 4.5" },
  doubts: { group: "credence", tone: "fg", shape: "question mark", stroke: "M5.4 5.8A2.6 2.6 0 1 1 9.3 8C8.4 8.6 8 9.2 8 10.3", fill: disc(8, 13, 1) },
  disbelieves: { group: "credence", tone: "error", shape: "cross", stroke: "M4 4 12 12M12 4 4 12" },
  // Trust
  trusts: { group: "trust", tone: "fg", shape: "shield", stroke: shield },
  distrusts: { group: "trust", tone: "error", shape: "struck-out shield", stroke: `${shield}M2 14 14 2` },
  suspects: { group: "trust", tone: "fg", shape: "magnifying glass", stroke: `${disc(6.8, 6.8, 4.2)}M10 10 14 14` },
  // Threat
  threatened: { group: "threat", tone: "error", shape: "warning triangle", stroke: "M8 2.2 14.6 13.6H1.4ZM8 6.6V9.8", fill: disc(8, 11.8, 0.9) },
  reassured: { group: "threat", tone: "fg", shape: "heart", stroke: "M8 13.6C3.4 10.4 2 8 2 5.8A3.05 3.05 0 0 1 8 4.8 3.05 3.05 0 0 1 14 5.8C14 8 12.6 10.4 8 13.6Z" },
  // Feeling
  fearful: { group: "feeling", tone: "error", shape: "lightning bolt", stroke: "M9.2 1.6 4 9H7.8L6.8 14.4 12 7H8.2Z" },
  hopeful: { group: "feeling", tone: "fg", shape: "rising sun", stroke: "M2 12.5H14M4.4 12.5A3.6 3.6 0 0 1 11.6 12.5M8 3.6V6M3 7.2 4.6 8.8M13 7.2 11.4 8.8" },
  curious: { group: "feeling", tone: "fg", shape: "eye", stroke: "M1.6 8C4 4.2 12 4.2 14.4 8 12 11.8 4 11.8 1.6 8Z", fill: disc(8, 8, 1.8) },
  indifferent: { group: "feeling", tone: "fg", shape: "circled dash", stroke: `${disc(8, 8, 5.8)}M5.4 8H10.6` },
};

export const isPerceptionKind = (kind: string | null | undefined): kind is PerceptionKind =>
  kind != null && Object.prototype.hasOwnProperty.call(perceptionIcons, kind);

/** An icon as inline SVG in its kit colour; `title` names it for a tooltip and screen readers (without one it is decorative). */
export function PerceptionGlyph({ kind, size = 16, title }: { kind: PerceptionKind; size?: number; title?: string }) {
  const icon = perceptionIcons[kind];
  const colour = `var(--abp-${icon.tone})`;
  return (
    <svg
      viewBox={`0 0 ${PERCEPTION_ICON_BOX} ${PERCEPTION_ICON_BOX}`}
      width={size}
      height={size}
      className="inline-block shrink-0"
      role={title ? "img" : undefined}
      aria-label={title}
      aria-hidden={title ? undefined : true}
      data-perception={kind}
    >
      {title && <title>{title}</title>}
      <path d={icon.stroke} fill="none" stroke={colour} strokeWidth={PERCEPTION_ICON_STROKE} strokeLinecap="round" strokeLinejoin="round" />
      {icon.fill && <path d={icon.fill} fill={colour} />}
    </svg>
  );
}

const paths = new Map<PerceptionKind, { stroke: Path2D; fill: Path2D | null }>();

/**
 * The icon on a 2D canvas, centred on (x, y) and `size` across, in `colour` (the caller reads the kit's token for the icon's tone).
 * The canvas's alpha and transform are kept; its paths are built once per kind.
 */
export function drawPerceptionIcon(ctx: CanvasRenderingContext2D, kind: PerceptionKind, x: number, y: number, size: number, colour: string): void {
  let p = paths.get(kind);
  if (!p) {
    const icon = perceptionIcons[kind];
    p = { stroke: new Path2D(icon.stroke), fill: icon.fill ? new Path2D(icon.fill) : null };
    paths.set(kind, p);
  }
  const k = size / PERCEPTION_ICON_BOX;
  ctx.save();
  ctx.translate(x - size / 2, y - size / 2);
  ctx.scale(k, k);
  ctx.setLineDash([]);
  ctx.lineWidth = PERCEPTION_ICON_STROKE;
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  ctx.strokeStyle = colour;
  ctx.fillStyle = colour;
  ctx.stroke(p.stroke);
  if (p.fill) ctx.fill(p.fill);
  ctx.restore();
}
