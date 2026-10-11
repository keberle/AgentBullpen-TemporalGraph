import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import ForceGraph2D, { type ForceGraphMethods, type LinkObject, type NodeObject } from "react-force-graph-2d";
import {
  autoFitTarget,
  bigChange,
  chapterOf,
  escapeHtml,
  GRAPH_MAX_ZOOM,
  GRAPH_MIN_ZOOM,
  largestComponent,
  isEntityLabel,
  nodeInitials,
  nodeRadius,
  type Gated,
  type GraphPayload,
  type Highlight,
} from "./model.ts";
import { drawPerceptionIcon, isPerceptionKind, perceptionIcons, type PerceptionKind } from "../design-kit/icons/perception.tsx";
import { readGraphTheme } from "./theme.ts";
import { useElementSize } from "./useElementSize.ts";
import { useLayout } from "./useLayout.ts";

// The 2D graph tab, drawn with the approved mockup's code on
// react-force-graph-2d: node shapes, sizes and luminance by label, edge styles by type, selection and highlight in the accent,
// labels at the mockup's zoom/size thresholds.
// No force simulation runs here. Every node is drawn where the book's stored 2D layout puts it (useLayout.ts: computed once by
// `index layout` with the mockup's forces, or by a worker for a book without one) and is pinned there; the library's engine is given
// no ticks. Gating only hides and shows nodes. Node and link objects are kept per id across re-gatings.
// Shapes say the label (Character disc, Location diamond, Event ring, Fact square), and a referenced Event's ring is dashed
// where a narrated one's is whole; a PERCEIVES edge carries its kind's icon at its midpoint (the design kit's perception icons),
// named in a tooltip on hover.

export type GNode = { id: number; label: string; display: string; r: number; pos: number | null; kind: string | null };
type GLink = { id: number; type: string; ent: boolean; kind: string | null };

const nobody: ReadonlySet<number> = new Set<number>();
/** A perception icon's size: 16 px on screen when zoomed in, never more than 14 graph units (it shrinks with the graph when zoomed out). */
const iconSize = (scale: number) => Math.min(14, 16 / scale);

/**
 * A perception icon drawn once (the page-coloured disc, its rim and the glyph) and kept as an image per kind and colours.
 * A book with facts on has thousands of these on screen, and stamping an image costs far less than drawing the disc and the
 * glyph's paths each time. 64 px, for an icon that is 16 px on screen at most; the disc's radius is 0.72 of the icon's size.
 */
const iconImages = new Map<string, HTMLCanvasElement>();
const ICON_PX = 64;
function iconImage(kind: PerceptionKind, colour: string, rim: string, page: string): HTMLCanvasElement {
  const key = `${kind}|${colour}|${rim}|${page}`;
  let image = iconImages.get(key);
  if (!image) {
    image = document.createElement("canvas");
    image.width = image.height = ICON_PX;
    const ctx = image.getContext("2d")!;
    const size = (ICON_PX - 2) / 1.44; // the icon's size when the disc (radius 0.72 × size) fills the image
    ctx.beginPath();
    ctx.arc(ICON_PX / 2, ICON_PX / 2, size * 0.72, 0, 2 * Math.PI);
    ctx.fillStyle = page;
    ctx.fill();
    ctx.lineWidth = Math.max(1, ICON_PX / 24);
    ctx.strokeStyle = rim;
    ctx.stroke();
    drawPerceptionIcon(ctx, kind, ICON_PX / 2, ICON_PX / 2, size, colour);
    iconImages.set(key, image);
  }
  return image;
}

export default function Graph2D({
  payload,
  gated,
  degree,
  highlight,
  selectedId,
  onSelect,
  onFrame,
  fitRequest = 0,
  focusRequest = 0,
  zoomRequest,
  onZoomChange,
  avatars,
}: {
  payload: GraphPayload;
  gated: Gated;
  degree: Map<number, number>;
  highlight: Highlight;
  selectedId: number | null;
  onSelect: (id: number | null) => void;
  /** Called after each drawn frame with the node's screen position, for the popover. */
  onFrame?: (screen: { x: number; y: number } | null) => void;
  /** Incremented by the Fit button. */
  fitRequest?: number;
  /** Incremented by the Focus button (zoom to the largest connected component). */
  focusRequest?: number;
  /** The zoom slider's requested zoom (a new object per request). */
  zoomRequest?: { k: number } | null;
  /** The zoom after every change (wheel, pinch, Fit, Focus, slider), for the slider. */
  onZoomChange?: (k: number) => void;
  /** Roster characters' avatar paths by node id (relative to the site root); their discs show the image once loaded. */
  avatars?: ReadonlyMap<number, string>;
}) {
  const [box, size] = useElementSize<HTMLDivElement>();
  const fg = useRef<ForceGraphMethods<NodeObject<GNode>, LinkObject<GNode, GLink>> | undefined>(undefined);
  const theme = useMemo(readGraphTheme, []);
  const cache = useRef(new Map<number, NodeObject<GNode>>());
  const linkCache = useRef(new Map<number, LinkObject<GNode, GLink>>());
  const cachedFor = useRef<GraphPayload | null>(null);
  // The stored layout, or a worker's for a book without one (the canvas is empty meanwhile).
  const { layouts, pending: layingOut } = useLayout(payload);
  const positions = layouts?.two ?? null;

  // One Image per avatar path, created once (the browser caches the file; a failed load is never retried). A disc shows its
  // image only when it has loaded; until then (or on error) it is drawn as before, with initials.
  // The canvas is redrawn only when something changes (nothing moves any more), so an image that arrives asks for a redraw.
  const images = useRef(new Map<string, HTMLImageElement>());
  const [imagesLoaded, setImagesLoaded] = useState(0);
  useEffect(() => {
    for (const path of avatars?.values() ?? []) {
      if (images.current.has(path)) continue;
      const img = new Image();
      img.decoding = "async";
      img.onload = () => setImagesLoaded((n) => n + 1);
      img.src = `/${path}`;
      images.current.set(path, img);
    }
  }, [avatars]);

  // What the canvas shows, in graph units, taken before each frame is drawn: a node or a link wholly outside it is skipped.
  const view = useRef({ x0: -Infinity, y0: -Infinity, x1: Infinity, y1: Infinity });
  const smallNodesOwed = useRef(false);

  // Same node and link objects for the same ids, each node pinned (fx/fy) at its stored position. The mockup's forces (entity links
  // long and loose, fact links short and tight; entities repel strongly; no overlaps) are applied where the layout is computed
  // (forceLayout.js), not here. Another book's payload starts both caches again.
  const data = useMemo(() => {
    if (cachedFor.current !== payload) {
      cache.current.clear();
      linkCache.current.clear();
      cachedFor.current = payload;
    }
    if (!positions) return { nodes: [] as NodeObject<GNode>[], links: [] as LinkObject<GNode, GLink>[] };
    const nodes = gated.nodes.map((n) => {
      const at = positions.get(n.id) ?? { x: 0, y: 0 };
      const o = cache.current.get(n.id) ?? ({ id: n.id } as NodeObject<GNode>);
      Object.assign(o, { label: n.label, display: n.display, r: nodeRadius(n.label, degree.get(n.id) ?? 0), pos: n.pos, kind: n.kind ?? null });
      Object.assign(o, { x: at.x, y: at.y, fx: at.x, fy: at.y });
      cache.current.set(n.id, o);
      return o;
    });
    const label = new Map(gated.nodes.map((n) => [n.id, n.label]));
    const links = gated.edges.map((e) => {
      const ent = isEntityLabel(label.get(e.source)!) && isEntityLabel(label.get(e.target)!);
      const l = linkCache.current.get(e.id) ?? ({ id: e.id, type: e.type, kind: e.kind ?? null, source: e.source, target: e.target, ent } as LinkObject<GNode, GLink>);
      Object.assign(l, { type: e.type, kind: e.kind ?? null, ent });
      linkCache.current.set(e.id, l);
      return l;
    });
    return { nodes, links };
  }, [gated, degree, positions, payload]);

  const dataRef = useRef(data);
  dataRef.current = data;

  // The selected node's neighbours (in what is drawn).
  const near = useMemo(() => {
    if (selectedId == null) return nobody; // the same empty set each time: a re-gating with nothing selected changes no look
    const s = new Set<number>();
    for (const l of data.links) {
      const a = typeof l.source === "object" ? (l.source as GNode).id : (l.source as number);
      const b = typeof l.target === "object" ? (l.target as GNode).id : (l.target as number);
      if (a === selectedId) s.add(b);
      if (b === selectedId) s.add(a);
    }
    return s;
  }, [data, selectedId]);

  const dashOf = (type: string) => (type === "RELATED_TO" ? [6, 4] : type === "LOCATED_AT" ? [2, 4] : []);

  // Before each frame: what the canvas shows, in graph units (a node or a link wholly outside it is skipped), and the links
  // that are neither highlighted nor at the selection, stroked as one path per look (entity or fact link, its dash) instead of one
  // stroke per link: a book with facts on has thousands. The lit ones, and every link's perception icon, are drawn per link below.
  const beforeFrame = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      const g = fg.current;
      const el = box.current;
      if (!g || !el) return;
      const a = g.screen2GraphCoords(0, 0);
      const b = g.screen2GraphCoords(el.clientWidth, el.clientHeight);
      const v = (view.current = { x0: Math.min(a.x, b.x), y0: Math.min(a.y, b.y), x1: Math.max(a.x, b.x), y1: Math.max(a.y, b.y) });
      smallNodesOwed.current = true;
      const paths = new Map<string, { path: Path2D; ent: boolean; type: string }>();
      for (const link of dataRef.current.links) {
        const l = link as LinkObject<GNode, GLink> & GLink;
        const s = l.source as NodeObject<GNode>;
        const t = l.target as NodeObject<GNode>;
        if (typeof s !== "object" || typeof t !== "object" || s.x == null || t.x == null) continue;
        if (highlight.edges.has(l.id) || (selectedId != null && (s.id === selectedId || t.id === selectedId))) continue;
        if ((s.x < v.x0 && t.x < v.x0) || (s.x > v.x1 && t.x > v.x1) || (s.y! < v.y0 && t.y! < v.y0) || (s.y! > v.y1 && t.y! > v.y1)) continue;
        const key = `${l.ent}|${l.type === "RELATED_TO" || l.type === "LOCATED_AT" ? l.type : ""}`;
        let p = paths.get(key);
        if (!p) paths.set(key, (p = { path: new Path2D(), ent: l.ent, type: l.type }));
        p.path.moveTo(s.x, s.y!);
        p.path.lineTo(t.x, t.y!);
      }
      for (const { path, ent, type } of paths.values()) {
        ctx.lineWidth = ent ? 1.4 : 0.7;
        ctx.setLineDash(dashOf(type));
        ctx.strokeStyle = ent ? theme.fgMuted : theme.fgFaint;
        ctx.globalAlpha = selectedId != null ? 0.25 : ent ? 0.8 : 0.45;
        ctx.stroke(path);
      }
      ctx.setLineDash([]);
      ctx.globalAlpha = 1;
    },
    [box, highlight, selectedId, theme],
  );

  const drawLink = useCallback(
    (link: LinkObject<GNode, GLink>, ctx: CanvasRenderingContext2D, scale: number) => {
      const l = link as LinkObject<GNode, GLink> & GLink;
      const s = l.source as NodeObject<GNode>;
      const t = l.target as NodeObject<GNode>;
      if (s.x == null || t.x == null) return;
      const v = view.current;
      if ((s.x < v.x0 && t.x < v.x0) || (s.x > v.x1 && t.x > v.x1) || (s.y! < v.y0 && t.y! < v.y0) || (s.y! > v.y1 && t.y! > v.y1)) return;
      const hi = highlight.edges.has(l.id);
      const isNear = selectedId != null && (s.id === selectedId || t.id === selectedId);
      if (hi || isNear) {
        // A lit link's own stroke (the others were stroked together before the frame).
        ctx.beginPath();
        ctx.moveTo(s.x, s.y!);
        ctx.lineTo(t.x, t.y!);
        ctx.lineWidth = hi ? 2.4 : l.ent ? 1.4 : 0.7;
        ctx.setLineDash(dashOf(l.type));
        ctx.strokeStyle = theme.accent;
        ctx.globalAlpha = 1;
        ctx.stroke();
        ctx.setLineDash([]);
      }
      // A PERCEIVES edge's render point is its kind's icon, at the midpoint, on a page-coloured disc so the line doesn't
      // run through the glyph. The glyph's shape says the kind; its colour is the kit's fg, or error for the hostile kinds (accent
      // when the edge is lit). Skipped when it would be under 5 px on screen.
      if (l.type === "PERCEIVES" && isPerceptionKind(l.kind)) {
        const size = iconSize(scale);
        if (size * scale >= 5) {
          const mx = (s.x + t.x) / 2;
          const my = (s.y! + t.y!) / 2;
          const colour = hi || isNear ? theme.accent : perceptionIcons[l.kind].tone === "error" ? theme.error : theme.fg;
          ctx.globalAlpha = hi || isNear ? 1 : selectedId != null ? 0.3 : 1;
          const half = size * 0.72 * (ICON_PX / (ICON_PX - 2));
          ctx.drawImage(iconImage(l.kind, colour, hi || isNear ? theme.accent : theme.outline, theme.page), mx - half, my - half, 2 * half, 2 * half);
        }
      }
      ctx.globalAlpha = 1;
    },
    [highlight, selectedId, theme],
  );

  // Hovering a perception icon names it. The pointer area of a PERCEIVES edge is its icon's disc; other edges have none (no
  // edge had a tooltip or a click before).
  const paintLinkArea = useCallback((link: LinkObject<GNode, GLink>, color: string, ctx: CanvasRenderingContext2D, scale: number) => {
    const l = link as LinkObject<GNode, GLink> & GLink;
    const s = l.source as NodeObject<GNode>;
    const t = l.target as NodeObject<GNode>;
    if (l.type !== "PERCEIVES" || !isPerceptionKind(l.kind) || s.x == null || t.x == null) return;
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc((s.x + t.x) / 2, (s.y! + t.y!) / 2, iconSize(scale) * 0.72, 0, 2 * Math.PI);
    ctx.fill();
  }, []);
  const linkTooltip = useCallback((link: LinkObject<GNode, GLink>) => {
    const l = link as LinkObject<GNode, GLink> & GLink;
    if (l.type !== "PERCEIVES" || !isPerceptionKind(l.kind)) return "";
    const s = l.source as NodeObject<GNode>;
    const t = l.target as NodeObject<GNode>;
    return `<small>PERCEIVES · ${escapeHtml(perceptionIcons[l.kind].group)}</small><br>${escapeHtml(s.display ?? "")} <b>${escapeHtml(l.kind)}</b> ${escapeHtml(t.display ?? "")}`;
  }, []);

  // The Facts and Events that are neither highlighted nor selected, drawn together: one path of squares and one of rings
  // (whole, and dashed for the referenced ones) per opacity, instead of a fill or a stroke per node. Drawn once a frame, when the
  // library reaches its first node, so they lie over the links and under the people and places.
  const drawSmallNodes = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      const v = view.current;
      const paths = new Map<string, { path: Path2D; shape: string; dim: boolean }>();
      for (const n of dataRef.current.nodes) {
        if (isEntityLabel(n.label) || n.x == null || highlight.nodes.has(n.id) || n.id === selectedId) continue;
        const [x, y, r] = [n.x, n.y!, n.r];
        if (x + r < v.x0 || x - r > v.x1 || y + r < v.y0 || y - r > v.y1) continue;
        const shape = n.label === "Event" ? (n.kind === "referenced" ? "referenced" : "event") : "fact";
        const dim = selectedId != null && !near.has(n.id);
        const key = `${shape}|${dim}`;
        let p = paths.get(key);
        if (!p) paths.set(key, (p = { path: new Path2D(), shape, dim }));
        if (shape === "fact") p.path.rect(x - r, y - r, 2 * r, 2 * r);
        else {
          p.path.moveTo(x + r, y);
          p.path.arc(x, y, r, 0, 2 * Math.PI);
        }
      }
      for (const { path, shape, dim } of paths.values()) {
        ctx.globalAlpha = dim ? 0.3 : 1;
        if (shape === "fact") {
          ctx.fillStyle = theme.outline;
          ctx.fill(path);
        } else {
          ctx.strokeStyle = theme.fgFaint;
          ctx.lineWidth = 1.5;
          ctx.setLineDash(shape === "referenced" ? [1.8, 1.7] : []);
          ctx.stroke(path);
          ctx.setLineDash([]);
        }
      }
      ctx.globalAlpha = 1;
    },
    [highlight, selectedId, near, theme],
  );

  const drawNode = useCallback(
    (node: NodeObject<GNode>, ctx: CanvasRenderingContext2D, scale: number) => {
      if (smallNodesOwed.current) {
        smallNodesOwed.current = false;
        drawSmallNodes(ctx);
      }
      const n = node;
      const x = n.x!;
      const y = n.y!;
      const r = n.r;
      const v = view.current;
      if (x + r + 80 < v.x0 || x - r - 80 > v.x1 || y + r + 20 < v.y0 || y - r - 20 > v.y1) return; // its label can reach past the disc
      const hi = highlight.nodes.has(n.id);
      if (!isEntityLabel(n.label) && !hi && n.id !== selectedId) return; // drawn with the others, above
      const isNear = selectedId != null && (n.id === selectedId || near.has(n.id));
      ctx.globalAlpha = selectedId != null && !isNear && !hi ? 0.3 : 1;
      const lit = hi || n.id === selectedId;
      if (lit) {
        ctx.beginPath();
        ctx.arc(x, y, r + 7, 0, 2 * Math.PI);
        ctx.fillStyle = theme.accentHalo;
        ctx.fill();
      }
      const path = n.label === "Character" ? avatars?.get(n.id) : undefined;
      const img = path ? images.current.get(path) : undefined;
      const avatar = img && img.complete && img.naturalWidth > 0 ? img : undefined;
      ctx.beginPath();
      if (n.label === "Location") {
        ctx.moveTo(x, y - r);
        ctx.lineTo(x + r, y);
        ctx.lineTo(x, y + r);
        ctx.lineTo(x - r, y);
        ctx.closePath();
        ctx.fillStyle = theme.fgMuted;
        ctx.fill();
      } else if (n.label === "Character") {
        ctx.arc(x, y, r, 0, 2 * Math.PI);
        ctx.fillStyle = theme.fg;
        ctx.fill();
        if (avatar) {
          ctx.save();
          ctx.clip();
          ctx.drawImage(avatar, x - r, y - r, 2 * r, 2 * r);
          ctx.restore();
        }
      } else if (n.label === "Event") {
        // A narrated Event's ring is whole; a referenced one's (told about, not shown) is dashed.
        ctx.arc(x, y, r, 0, 2 * Math.PI);
        ctx.strokeStyle = theme.fgFaint;
        ctx.lineWidth = 1.5;
        if (n.kind === "referenced") ctx.setLineDash([1.8, 1.7]);
        ctx.stroke();
        ctx.setLineDash([]);
      } else {
        // A Fact is a filled square (its shape, not its colour, tells it from a Character's disc and an Event's ring).
        ctx.rect(x - r, y - r, 2 * r, 2 * r);
        ctx.fillStyle = hi ? theme.fg : theme.outline;
        ctx.fill();
      }
      // The selection/highlight ring goes on top of the disc and its image.
      if (lit) {
        ctx.beginPath();
        ctx.arc(x, y, r + 3, 0, 2 * Math.PI);
        ctx.strokeStyle = theme.accent;
        ctx.lineWidth = n.id === selectedId ? 3 : 2;
        ctx.stroke();
      }
      if (n.label === "Character" && r >= 12 && !avatar) {
        ctx.fillStyle = theme.onDisc;
        ctx.font = `600 ${Math.round(r * 0.9)}px ${theme.font}`;
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText(nodeInitials(n.display), x, y + 0.5);
      }
      if (isEntityLabel(n.label) && (scale > 0.7 || r >= 12 || isNear || hi)) {
        ctx.fillStyle = theme.fg;
        ctx.font = `${isNear || hi ? "600 " : ""}11px ${theme.font}`;
        ctx.textAlign = "center";
        ctx.textBaseline = "top";
        ctx.fillText(n.display, x, y + r + 3);
      }
      ctx.globalAlpha = 1;
    },
    [highlight, selectedId, near, theme, avatars, imagesLoaded, drawSmallNodes],
  );

  const paintArea = useCallback((node: NodeObject<GNode>, color: string, ctx: CanvasRenderingContext2D) => {
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(node.x!, node.y!, node.r + 2, 0, 2 * Math.PI);
    ctx.fill();
  }, []);

  const tooltip = useCallback(
    (node: NodeObject<GNode>) => {
      const ch = chapterOf(payload.chapters, node.pos);
      return `<small>${escapeHtml(node.label)}${ch ? ` · first seen ch. ${ch}` : ""}</small><br>${escapeHtml(node.display)}`;
    },
    [payload],
  );

  // The popover's anchor: the selected node's screen position after each frame (pan, zoom, simulation).
  const frame = useCallback(() => {
    if (!onFrame) return;
    const n = selectedId == null ? undefined : cache.current.get(selectedId);
    const g = fg.current;
    onFrame(n && n.x != null && g && data.nodes.includes(n) ? g.graph2ScreenCoords(n.x, n.y!) : null);
  }, [onFrame, selectedId, data]);

  // Dev builds only: lets the Playwright checks find a node's screen position on the canvas.
  const ringRef = useRef({ selected: selectedId, hi: highlight.nodes });
  ringRef.current = { selected: selectedId, hi: highlight.nodes };
  useEffect(() => {
    if (!import.meta.env.DEV) return;
    (window as unknown as { __graph2d?: unknown }).__graph2d = {
      screenOf: (id: number) => {
        const n = cache.current.get(id);
        return n && n.x != null && fg.current ? fg.current.graph2ScreenCoords(n.x, n.y!) : null;
      },
      // The automatic fit's choice (largest component vs visible nodes).
      autoTarget: () => {
        const c = largestComponent(gatedRef.current);
        return { visible: gatedRef.current.nodes.length, largest: c?.size ?? 0, focus: autoFitTarget(gatedRef.current) !== null };
      },
      // The nodes drawn with the accent ring (selection plus chat highlight) — exactly one after a character switch.
      rings: () => {
        const { selected, hi } = ringRef.current;
        const drawn = new Set(dataRef.current.nodes.map((n) => n.id));
        return { selected: selected != null && drawn.has(selected) ? selected : null, ringed: [...drawn].filter((id) => id === selected || hi.has(id)) };
      },
      // How many drawn nodes lie outside the canvas (the Fit checks).
      outside: () => {
        const g = fg.current;
        const el = box.current;
        if (!g || !el) return null;
        const w = el.clientWidth;
        const h = el.clientHeight;
        const ns = dataRef.current.nodes.filter((n) => n.x != null);
        const out = ns.filter((n) => {
          const p = g.graph2ScreenCoords(n.x!, n.y!);
          return p.x < 0 || p.y < 0 || p.x > w || p.y > h;
        });
        return { total: ns.length, outside: out.length, zoom: g.zoom() };
      },
    };
  }, [box]);

  // The canvas narrows when the chat panel slides in: keep the graph's centre in the middle of the new width.
  useEffect(() => {
    if (size.width > 0) fg.current?.centerAt(0, 0, 400);
  }, [size.width]);

  // Zooming to the graph. Fit frames every visible node; Focus the largest connected component (≥ 3 nodes);
  // the automatic fit (the first nodes, a re-gating that changes the visible set by > 20 %) uses Focus whenever that component exists
  // else Fit. The zoom floor is 0.05, low enough to frame a whole book with facts on.
  // Nothing moves, so a Fit or Focus frames the nodes at once; the automatic one is made when the library has taken the data
  // (its engine, given no ticks, stops then).
  const gatedRef = useRef(gated);
  gatedRef.current = gated;
  const pending = useRef<"fit" | "focus" | "auto" | null>("auto");
  const shownIds = useRef<Set<number>>(new Set());
  const zoomTo = useCallback((kind: "fit" | "focus" | "auto") => {
    const g = fg.current;
    if (!g) return;
    const component = kind === "fit" ? null : kind === "focus" ? largestComponent(gatedRef.current) : autoFitTarget(gatedRef.current);
    if (component) g.zoomToFit(400, 40, (n: NodeObject<GNode>) => component.has(n.id));
    else g.zoomToFit(400, 40);
  }, []);
  useEffect(() => {
    const ids = new Set(data.nodes.map((n) => n.id));
    if (ids.size > 0 && bigChange(shownIds.current, ids)) pending.current = "auto";
    shownIds.current = ids;
  }, [data]);
  const onStop = useCallback(() => {
    if (dataRef.current.nodes.length === 0) return; // a book still being laid out: the fit waits for its nodes
    if (pending.current) zoomTo(pending.current);
    pending.current = null;
  }, [zoomTo]);
  const request = useCallback(
    (kind: "fit" | "focus") => {
      if (dataRef.current.nodes.length === 0) pending.current = kind; // nothing to frame yet: made when the nodes are there
      else zoomTo(kind);
    },
    [zoomTo],
  );
  useEffect(() => {
    if (fitRequest > 0) request("fit");
  }, [fitRequest, request]);
  useEffect(() => {
    if (focusRequest > 0) request("focus");
  }, [focusRequest, request]);
  // The zoom slider sets the zoom about the current centre; wheel, pinch, Fit and Focus report it back (onZoom).
  useEffect(() => {
    if (zoomRequest) fg.current?.zoom(zoomRequest.k, 200);
  }, [zoomRequest]);

  const [warm, setWarm] = useState(false);
  useEffect(() => setWarm(true), []);

  return (
    <div ref={box} className="absolute inset-0" data-testid="graph-2d" data-layout={layingOut ? "pending" : "ready"}>
      {layingOut && (
        <div role="status" className="pointer-events-none absolute inset-x-0 top-56 z-1 text-center text-13 text-fg-muted">
          Laying out…
        </div>
      )}
      {warm && size.width > 0 && (
        <ForceGraph2D<GNode, GLink>
          ref={fg}
          width={size.width}
          height={size.height}
          graphData={data}
          backgroundColor={theme.page}
          nodeId="id"
          nodeLabel={tooltip}
          nodeCanvasObject={drawNode}
          nodeCanvasObjectMode={() => "replace"}
          nodePointerAreaPaint={paintArea}
          linkCanvasObject={drawLink}
          linkCanvasObjectMode={() => "replace"}
          linkPointerAreaPaint={paintLinkArea}
          linkLabel={linkTooltip}
          warmupTicks={0}
          cooldownTicks={0}
          minZoom={GRAPH_MIN_ZOOM}
          maxZoom={GRAPH_MAX_ZOOM}
          onRenderFramePre={beforeFrame}
          enableNodeDrag={false}
          onNodeClick={(n) => onSelect(n.id)}
          onBackgroundClick={() => onSelect(null)}
          onRenderFramePost={frame}
          onEngineStop={onStop}
          onZoom={(t) => onZoomChange?.(t.k)}
        />
      )}
    </div>
  );
}
