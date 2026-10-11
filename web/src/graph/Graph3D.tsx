import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import ForceGraph3D, { type ForceGraphMethods } from "react-force-graph-3d";
import * as THREE from "three";
import {
  autoFitTarget,
  bigChange,
  chapterOf,
  largestComponent,
  escapeHtml,
  isEntityLabel,
  flyTargets,
  frameAround,
  nodeRadius,
  type Gated,
  type GraphPayload,
  type Highlight,
} from "./model.ts";
import { KitButton } from "../design-kit/components/KitButton.tsx";
import { drawPerceptionIcon, isPerceptionKind, perceptionIcons, type PerceptionKind } from "../design-kit/icons/perception.tsx";
import { opaque, readGraphTheme, type GraphTheme } from "./theme.ts";
import { useElementSize } from "./useElementSize.ts";
import { useLayout, type Position3 } from "./useLayout.ts";
import { releaseContext, watchContext, type ReleasableRenderer } from "./webgl.ts";

// The 3D graph tab, lazy-loaded (three.js is large). The same gate, display names, selection and highlight as the 2D
// tab, on react-force-graph-3d: Character sphere, Location octahedron, Event torus, Fact small sphere, in the same luminance
// order; the accent for selection and highlight. Library primitives only, no custom shaders.
//
// Roster characters with an avatar are a round face (a sprite) with a thin accent ring when selected or highlighted; name
// labels (canvas-text sprites under the node) for roster characters and locations, and for other characters near the selection;
// entity links wider and brighter than fact links, both faded unless highlighted; orbit controls with an optional slow auto-rotate;
// the camera flies to a node selected from the dropdown or the roster (not on a node click); fog in the page colour so distant
// strays fade while the Focus component stays fully lit. Geometries, materials and textures are shared and cached, and the fact and
// event links that aren't lit are drawn as one line set (one draw call instead of one per link: thousands with facts on).
//
// One renderer (one WebGL context) for as long as the tab is shown. Graph3D keeps the pane's last real size, so a measurement
// of 0 never unmounts the scene; leaving the tab gives the context back to the browser (webgl.ts); and a context that is lost anyway
// shows a dark panel with "Reload graph", which mounts a new scene, instead of the browser's blank white canvas.

//
// A Fact is a cube (its shape tells it from a Character's sphere and an Event's ring) and a referenced Event an open ring
// where a narrated one's is closed; every PERCEIVES edge carries its kind's icon (the design kit's perception icons) as a sprite at
// its midpoint. The sprites are this file's own (one group, positions copied from the moving nodes like the merged lines), since
// most PERCEIVES edges go to Facts and are not drawn by the library.
//
// No force simulation runs here. Every node is drawn where the book's stored layout puts it (useLayout.ts: computed once by
// `index layout`, or by a worker for a book without one) and is pinned there (fx/fy/fz), with the library's engine given no ticks.
// Gating only hides and shows nodes, so nothing moves when the slider does. Node and link objects are kept per id, so a re-gating
// allocates only what is new. Facts and Events, thousands with facts on, are instanced: one mesh per shape and look instead of one
// object per node; the library draws only the people and places. Picking and the tooltip for the instanced nodes are this file's.

type N3 = {
  id: number;
  label: string;
  display: string;
  r: number;
  pos: number | null;
  kind: string | null;
  x?: number;
  y?: number;
  z?: number;
  fx?: number;
  fy?: number;
  fz?: number;
};
type L3 = { id: number; type: string; ent: boolean; kind: string | null; source: number | N3; target: number | N3 };
const nobody: ReadonlySet<number> = new Set<number>();
type Point = { x: number; y: number };

// ---- Textures, cached per page (avatar paths are per book) ------------------------------------------------------------

/** An avatar as a round texture (the image clipped to a circle), loaded once per path; null while loading or after a failure. */
const avatarTextures = new Map<string, THREE.Texture | "loading" | "failed">();
function avatarTexture(path: string, onLoad: () => void): THREE.Texture | null {
  const t = avatarTextures.get(path);
  if (t instanceof THREE.Texture) return t;
  if (t === undefined) {
    avatarTextures.set(path, "loading");
    const img = new Image();
    img.onload = () => {
      const size = 256;
      const canvas = document.createElement("canvas");
      canvas.width = canvas.height = size;
      const ctx = canvas.getContext("2d")!;
      ctx.beginPath();
      ctx.arc(size / 2, size / 2, size / 2, 0, 2 * Math.PI);
      ctx.clip();
      ctx.drawImage(img, 0, 0, size, size);
      const texture = new THREE.CanvasTexture(canvas);
      texture.colorSpace = THREE.SRGBColorSpace;
      avatarTextures.set(path, texture);
      onLoad();
    };
    img.onerror = () => avatarTextures.set(path, "failed");
    img.src = path.startsWith("/") ? path : `/${path}`;
  }
  return null;
}

/** A thin ring in a colour, as a texture (the selection/highlight ring behind an avatar; a sprite always faces the camera). */
const ringTextures = new Map<string, THREE.Texture>();
function ringTexture(colour: string, width: number): THREE.Texture {
  const key = `${colour}|${width}`;
  let t = ringTextures.get(key);
  if (!t) {
    const size = 256;
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = size;
    const ctx = canvas.getContext("2d")!;
    ctx.beginPath();
    ctx.arc(size / 2, size / 2, size / 2 - width / 2 - 1, 0, 2 * Math.PI);
    ctx.strokeStyle = colour;
    ctx.lineWidth = width;
    ctx.stroke();
    t = new THREE.CanvasTexture(canvas);
    t.colorSpace = THREE.SRGBColorSpace;
    ringTextures.set(key, t);
  }
  return t;
}

/** A name as a canvas-text texture (the 2D labels' font), with its width/height ratio; cached per text and weight. */
const labelTextures = new Map<string, { texture: THREE.Texture; aspect: number }>();
function labelTexture(text: string, bold: boolean, theme: GraphTheme): { texture: THREE.Texture; aspect: number } {
  const key = `${bold ? "b" : "n"}|${text}`;
  let t = labelTextures.get(key);
  if (!t) {
    const px = 40;
    const font = `${bold ? "600 " : ""}${px}px ${theme.font}`;
    const measure = document.createElement("canvas").getContext("2d")!;
    measure.font = font;
    const w = Math.ceil(measure.measureText(text).width) + 12;
    const h = Math.ceil(px * 1.35);
    const canvas = document.createElement("canvas");
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext("2d")!;
    ctx.font = font;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    // A faint page-coloured outline keeps the text readable over links.
    ctx.lineWidth = 6;
    ctx.strokeStyle = theme.page;
    ctx.strokeText(text, w / 2, h / 2);
    ctx.fillStyle = theme.fg;
    ctx.fillText(text, w / 2, h / 2);
    const texture = new THREE.CanvasTexture(canvas);
    texture.colorSpace = THREE.SRGBColorSpace;
    t = { texture, aspect: w / h };
    labelTextures.set(key, t);
  }
  return t;
}

/**
 * A perception icon as a texture: the kit's glyph in a colour, on a page-coloured disc with a thin rim in that colour (so the
 * link behind doesn't run through the glyph). Cached per kind and colour.
 */
const perceptionTextures = new Map<string, THREE.Texture>();
function perceptionTexture(kind: PerceptionKind, colour: string, page: string): THREE.Texture {
  const key = `${kind}|${colour}`;
  let t = perceptionTextures.get(key);
  if (!t) {
    const size = 96;
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = size;
    const ctx = canvas.getContext("2d")!;
    ctx.beginPath();
    ctx.arc(size / 2, size / 2, size / 2 - 3, 0, 2 * Math.PI);
    ctx.fillStyle = page;
    ctx.fill();
    ctx.lineWidth = 3;
    ctx.strokeStyle = colour;
    ctx.stroke();
    drawPerceptionIcon(ctx, kind, size / 2, size / 2, size * 0.62, colour);
    t = new THREE.CanvasTexture(canvas);
    t.colorSpace = THREE.SRGBColorSpace;
    perceptionTextures.set(key, t);
  }
  return t;
}

/** The radius a Fact's cube and an Event's ring are drawn with (their fixed model radius × 0.6, in quarter steps, as the entities'). */
const instancedRadius = (label: string) => Math.round(nodeRadius(label, 0) * 0.6 * 4) / 4;

/** A label's height in world units (node radii are about 3–14 there). */
const LABEL_HEIGHT = 6.5;
/** A perception icon's width in world units (a little under a label's height). */
const ICON_SIZE = 5.5;

type Graph3DProps = {
  payload: GraphPayload;
  gated: Gated;
  degree: Map<number, number>;
  highlight: Highlight;
  selectedId: number | null;
  onSelect: (id: number | null) => void;
  /** Incremented by the Fit button. */
  fitRequest?: number;
  /** Incremented by the Focus button. */
  focusRequest?: number;
  /** Incremented by Reset view (the camera back to the Focus framing). */
  resetRequest?: number;
  /** Roster characters' avatar paths by node id; those nodes are drawn as faces. */
  avatars?: ReadonlyMap<number, string>;
  /** Roster characters' node ids (always labelled). */
  roster?: ReadonlySet<number>;
  /** A slow orbit; any drag stops it (onAutoRotateStop). */
  autoRotate?: boolean;
  onAutoRotateStop?: () => void;
  /** The selected node's screen position each frame (the Details card follows it), null when it isn't drawn. */
  onFrame?: (p: Point | null) => void;
};

const stoppedText = "The 3D view stopped";

export default function Graph3D(props: Graph3DProps) {
  // The last real size of the pane (useElementSize holds it), so the scene mounts once and is only ever resized.
  const [box, size] = useElementSize<HTMLDivElement>();
  // The WebGL context was lost (the browser's context limit, a GPU reset). `scene` counts the scenes mounted: "Reload graph"
  // starts the next one, with a new renderer.
  const [lost, setLost] = useState(false);
  const [scene, setScene] = useState(0);
  const onLost = useCallback(() => setLost(true), []);
  const onRestored = useCallback(() => setLost(false), []);
  // The stored layout, or a worker's for a book without one (the scene is mounted meanwhile, empty).
  const { layouts, pending } = useLayout(props.payload);
  return (
    <div ref={box} className="absolute inset-0" data-testid="graph-3d" data-layout={pending ? "pending" : "ready"}>
      {size.width > 0 && (
        <Scene key={scene} {...props} positions={layouts?.three ?? null} width={size.width} height={size.height} onContextLost={onLost} onContextRestored={onRestored} />
      )}
      {pending && !lost && (
        <div role="status" className="pointer-events-none absolute inset-x-0 top-56 text-center text-13 text-fg-muted">
          Laying out…
        </div>
      )}
      {lost && (
        <div role="alert" className="absolute inset-0 flex flex-col items-center justify-center gap-12 bg-page text-14 text-fg-muted">
          <span>{stoppedText}</span>
          <KitButton
            variant="outline"
            size="xs"
            onClick={() => {
              setLost(false);
              setScene((n) => n + 1);
            }}
          >
            Reload graph
          </KitButton>
        </div>
      )}
    </div>
  );
}

function Scene({
  payload,
  gated,
  degree,
  highlight,
  selectedId,
  onSelect,
  fitRequest = 0,
  focusRequest = 0,
  resetRequest = 0,
  avatars,
  roster,
  autoRotate = false,
  onAutoRotateStop,
  onFrame,
  positions,
  width,
  height,
  onContextLost,
  onContextRestored,
}: Graph3DProps & { positions: ReadonlyMap<number, Position3> | null; width: number; height: number; onContextLost: () => void; onContextRestored: () => void }) {
  const fg = useRef<ForceGraphMethods<N3, L3> | undefined>(undefined);
  const theme = useMemo(readGraphTheme, []);
  // This scene's renderer, from mount to unmount. While mounted, a lost or restored context is reported to Graph3D. On
  // unmount (the tab left, another book, "Reload graph") the context goes back to the browser, once the library has torn the scene
  // down (its cleanup runs after this one).
  useEffect(() => {
    const renderer = fg.current?.renderer() as (ReleasableRenderer & { domElement: HTMLCanvasElement; debug?: { checkShaderErrors: boolean } }) | undefined;
    if (!renderer) return;
    // Three.js asks the driver for a program's log every time it links one (a synchronous wait on the GPU process, about
    // 40 ms here), which a re-gating can cause. Dev builds keep the check.
    if (renderer.debug && !import.meta.env.DEV) renderer.debug.checkShaderErrors = false;
    const stop = watchContext(renderer.domElement, onContextLost, onContextRestored);
    return () => {
      stop();
      window.setTimeout(() => releaseContext(renderer), 0);
    };
  }, [onContextLost, onContextRestored]);
  const colours = useMemo(
    () => ({
      Character: opaque(theme.fg),
      Location: opaque(theme.fgMuted),
      Event: opaque(theme.fgFaint),
      Fact: opaque(theme.outline),
      accent: opaque(theme.accent),
      error: opaque(theme.error),
      entityLink: opaque(theme.fgMuted),
      link: opaque(theme.fgFaint),
    }),
    [theme],
  );
  const cache = useRef(new Map<number, N3>());
  // Performance: one geometry per shape and size and one material per look, shared by every node that has them (a book with
  // facts on draws thousands of nodes; separate geometries and materials cost a state change per node per frame).
  const geometries = useRef(new Map<string, THREE.BufferGeometry>());
  const materials = useRef(new Map<string, THREE.Material>());
  const shared = useCallback(<T extends THREE.Material>(key: string, make: () => T): T => {
    let m = materials.current.get(key) as T | undefined;
    if (!m) {
      m = make();
      materials.current.set(key, m);
    }
    return m;
  }, []);
  // Avatars load once; each arrival redraws the nodes (the face replaces the sphere).
  const [avatarsLoaded, setAvatarsLoaded] = useState(0);
  const avatarLoaded = useCallback(() => setAvatarsLoaded((n) => n + 1), []);

  // Node and link objects are kept per id (a re-gating reuses them), each node pinned at its stored position. Another book's
  // payload starts both caches again.
  const linkCache = useRef(new Map<number, L3>());
  const cachedFor = useRef<GraphPayload | null>(null);
  const data = useMemo(() => {
    if (cachedFor.current !== payload) {
      cache.current.clear();
      linkCache.current.clear();
      cachedFor.current = payload;
    }
    if (!positions) return { nodes: [] as N3[], links: [] as L3[] };
    const nodes = gated.nodes.map((n) => {
      const at = positions.get(n.id) ?? { x: 0, y: 0, z: 0 };
      const o = cache.current.get(n.id) ?? { id: n.id, label: n.label, display: n.display, r: 0, pos: n.pos, kind: n.kind ?? null };
      Object.assign(o, { label: n.label, display: n.display, r: nodeRadius(n.label, degree.get(n.id) ?? 0), pos: n.pos, kind: n.kind ?? null });
      Object.assign(o, { x: at.x, y: at.y, z: at.z, fx: at.x, fy: at.y, fz: at.z });
      cache.current.set(n.id, o);
      return o;
    });
    const label = new Map(gated.nodes.map((n) => [n.id, n.label]));
    const links = gated.edges.map((e) => {
      const ent = isEntityLabel(label.get(e.source)!) && isEntityLabel(label.get(e.target)!);
      const l = linkCache.current.get(e.id) ?? { id: e.id, type: e.type, kind: e.kind ?? null, source: e.source, target: e.target, ent };
      Object.assign(l, { type: e.type, kind: e.kind ?? null, ent });
      linkCache.current.set(e.id, l);
      return l;
    });
    return { nodes, links };
  }, [gated, degree, positions, payload]);
  const dataRef = useRef(data);
  dataRef.current = data;
  /** A link's end as its node object (the library replaces the id with the object when it takes the data). */
  const end = useCallback((v: number | N3) => (typeof v === "object" ? v : cache.current.get(v)), []);

  const near = useMemo(() => {
    if (selectedId == null) return nobody; // the same empty set each time: a re-gating with nothing selected changes no look
    const s = new Set<number>();
    for (const l of data.links) {
      const a = typeof l.source === "object" ? l.source.id : l.source;
      const b = typeof l.target === "object" ? l.target.id : l.target;
      if (a === selectedId) s.add(b);
      if (b === selectedId) s.add(a);
    }
    return s;
  }, [data, selectedId]);

  const object = useCallback(
    (n: N3) => {
      const r = Math.round(n.r * 0.6 * 4) / 4; // sizes in quarter steps, so nodes share geometries
      const hi = highlight.nodes.has(n.id);
      const lit = hi || n.id === selectedId;
      const isNear = n.id === selectedId || near.has(n.id);
      const dim = selectedId != null && !isNear && !hi;

      // A roster character with a loaded avatar is a face, with a thin accent ring behind it when lit.
      const path = n.label === "Character" ? avatars?.get(n.id) : undefined;
      const face = path ? avatarTexture(path, avatarLoaded) : null;
      let core: THREE.Object3D;
      let reach = r; // how far the drawn node reaches below its centre (the label goes under it)
      if (face) {
        const group = new THREE.Group();
        const size = 2.2 * r;
        reach = size / 2;
        if (lit) {
          const ring = new THREE.Sprite(
            shared(`ring|${n.id === selectedId}`, () => new THREE.SpriteMaterial({ map: ringTexture(theme.accent, n.id === selectedId ? 14 : 10), transparent: true, depthWrite: false })),
          );
          ring.scale.set(size * 1.2, size * 1.2, 1);
          ring.renderOrder = 0;
          group.add(ring);
        }
        const sprite = new THREE.Sprite(
          shared(`face|${path}|${dim}`, () => new THREE.SpriteMaterial({ map: face, alphaTest: 0.5, transparent: dim, opacity: dim ? 0.3 : 1 })),
        );
        sprite.scale.set(size, size, 1);
        sprite.renderOrder = 1;
        group.add(sprite);
        core = group;
      } else {
        // The library draws people and places only (nodeVisibility); Facts and Events are instanced, below.
        const gKey = `${n.label}:${r}`;
        let geometry = geometries.current.get(gKey);
        if (!geometry) {
          geometry = n.label === "Character" ? new THREE.SphereGeometry(r, 16, 12) : new THREE.OctahedronGeometry(r);
          geometries.current.set(gKey, geometry);
        }
        const colour = lit ? colours.accent : ((colours as Record<string, string>)[n.label] ?? colours.Fact);
        const material = shared(
          `mesh|${colour}|${lit}|${dim}`,
          () =>
            new THREE.MeshLambertMaterial({
              color: colour,
              transparent: dim,
              opacity: dim ? 0.3 : 1,
              emissive: lit ? colours.accent : "#000000",
              emissiveIntensity: lit ? 0.35 : 0,
            }),
        );
        core = new THREE.Mesh(geometry, material);
      }

      // Names under roster characters and locations always; other characters near the selection or highlighted; never
      // facts or events.
      const labelled =
        n.label === "Location" || (n.label === "Character" && (roster?.has(n.id) || isNear || hi));
      if (!labelled) return core;
      const bold = isNear || hi;
      const { texture, aspect } = labelTexture(n.display, bold, theme);
      const label = new THREE.Sprite(
        shared(`label|${n.display}|${bold}|${dim}`, () => new THREE.SpriteMaterial({ map: texture, transparent: true, depthWrite: false, opacity: dim ? 0.35 : 1 })),
      );
      label.scale.set(LABEL_HEIGHT * aspect, LABEL_HEIGHT, 1);
      label.position.set(0, -(reach + LABEL_HEIGHT * 0.6 + 0.5), 0);
      label.renderOrder = 2;
      const group = core instanceof THREE.Group ? core : new THREE.Group().add(core);
      group.add(label);
      return group;
    },
    [highlight, selectedId, near, colours, theme, avatars, roster, avatarLoaded, shared, avatarsLoaded],
  );

  // Entity links (people and places) wider and brighter than fact/event links, both faded unless highlighted or touching
  // the selection (accent). Lit links 0.5 wide (1.5 read as tubes up close; widths are in world units), entity links 0.4 so
  // the lit ones stay the strongest. Per-link opacity is linkOpacity × the colour's alpha.
  const isLit = useCallback(
    (l: L3) => {
      const s = typeof l.source === "object" ? l.source.id : l.source;
      const t = typeof l.target === "object" ? l.target.id : l.target;
      return highlight.edges.has(l.id) || (selectedId != null && (s === selectedId || t === selectedId));
    },
    [highlight, selectedId],
  );
  const linkColour = useCallback(
    (l: L3) => {
      return isLit(l) ? colours.accent : l.ent ? withAlpha(colours.entityLink, 0.75) : withAlpha(colours.link, 0.35);
    },
    [isLit, colours],
  );
  const linkWidth = useCallback(
    (l: L3) => (isLit(l) ? 0.5 : l.ent ? 0.4 : 0),
    [isLit],
  );

  // Facts and Events as instanced meshes: one per shape (a Fact's cube; an Event's ring, open for a referenced one)
  // and look (plain, lit in the accent, dimmed beside a selection), each instance placed at its node. A book with facts on has
  // thousands of these nodes and they were one object and one draw call each; now they are at most nine draw calls. The shapes,
  // sizes and materials are the ones the nodes had.
  const instanced = useRef<{ mesh: THREE.InstancedMesh; ids: number[] }[]>([]);
  const instancedGroup = useRef<THREE.Group | null>(null);
  const buildInstanced = useCallback(() => {
    const group = instancedGroup.current;
    if (!group) return;
    for (const { mesh } of instanced.current) {
      group.remove(mesh);
      mesh.dispose();
    }
    instanced.current = [];
    const buckets = new Map<string, { geometry: THREE.BufferGeometry; material: THREE.Material; nodes: N3[] }>();
    for (const n of data.nodes) {
      if (isEntityLabel(n.label)) continue;
      const hi = highlight.nodes.has(n.id);
      const lit = hi || n.id === selectedId;
      const dim = selectedId != null && !lit && !near.has(n.id);
      const shape = n.label === "Event" ? (n.kind === "referenced" ? "Event:referenced" : "Event") : "Fact";
      const key = `${shape}|${lit}|${dim}`;
      let bucket = buckets.get(key);
      if (!bucket) {
        const r = instancedRadius(shape === "Fact" ? "Fact" : "Event");
        let geometry = geometries.current.get(shape);
        if (!geometry) {
          geometry =
            shape === "Fact" ? new THREE.BoxGeometry(r * 1.4, r * 1.4, r * 1.4) : new THREE.TorusGeometry(r, r * 0.28, 6, 12, shape === "Event" ? 2 * Math.PI : 1.5 * Math.PI);
          geometries.current.set(shape, geometry);
        }
        const colour = lit ? colours.accent : shape === "Fact" ? colours.Fact : colours.Event;
        const material = shared(
          `mesh|${colour}|${lit}|${dim}`,
          () =>
            new THREE.MeshLambertMaterial({
              color: colour,
              transparent: dim,
              opacity: dim ? 0.3 : 1,
              emissive: lit ? colours.accent : "#000000",
              emissiveIntensity: lit ? 0.35 : 0,
            }),
        );
        buckets.set(key, (bucket = { geometry, material, nodes: [] }));
      }
      bucket.nodes.push(n);
    }
    const at = new THREE.Matrix4();
    for (const { geometry, material, nodes } of buckets.values()) {
      const mesh = new THREE.InstancedMesh(geometry, material, nodes.length);
      nodes.forEach((n, i) => mesh.setMatrixAt(i, at.makeTranslation(n.x ?? 0, n.y ?? 0, n.z ?? 0)));
      mesh.instanceMatrix.needsUpdate = true;
      mesh.frustumCulled = false; // the instances are spread over the whole graph
      group.add(mesh);
      instanced.current.push({ mesh, ids: nodes.map((n) => n.id) });
    }
  }, [data, highlight, selectedId, near, colours, shared]);
  useEffect(buildInstanced, [buildInstanced]);

  /** The instanced node under a pointer position, by a ray from the camera; null when there is none. */
  const pick = useCallback((ev: { clientX: number; clientY: number }): number | null => {
    const g = fg.current;
    if (!g || instanced.current.length === 0) return null;
    const rect = (g.renderer().domElement as HTMLCanvasElement).getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) return null;
    const ray = new THREE.Raycaster();
    ray.setFromCamera(new THREE.Vector2(((ev.clientX - rect.left) / rect.width) * 2 - 1, -((ev.clientY - rect.top) / rect.height) * 2 + 1), g.camera());
    const hit = ray.intersectObjects(instanced.current.map((i) => i.mesh), false)[0];
    if (!hit || hit.instanceId == null) return null;
    return instanced.current.find((i) => i.mesh === hit.object)?.ids[hit.instanceId] ?? null;
  }, []);

  // Performance: the library draws entity links and lit links; every other link is a segment of one LineSegments, its
  // positions taken from the nodes' (they are fixed, so the segments are written when the drawn set changes, not per tick).
  const merged = useCallback((l: L3) => !l.ent && !isLit(l), [isLit]);
  // The library is given only the links it draws. It walks every link it holds on every frame (arrows, particles), and the
  // merged ones, thousands with facts on, are drawn here from the nodes' fixed positions and need nothing from it.
  const libraryData = useMemo(() => ({ nodes: data.nodes, links: data.links.filter((l) => !merged(l)) }), [data, merged]);
  const lines = useRef<THREE.LineSegments | null>(null);
  const linesData = useRef<L3[]>([]);
  const syncLines = useCallback(() => {
    const seg = lines.current;
    if (!seg) return;
    const ls = linesData.current;
    const attr = seg.geometry.getAttribute("position") as THREE.BufferAttribute | undefined;
    const arr = attr && attr.count >= ls.length * 2 ? (attr.array as Float32Array) : new Float32Array(Math.max(6, ls.length * 6));
    let k = 0;
    for (const l of ls) {
      const a = end(l.source);
      const b = end(l.target);
      if (!a || !b || a.x == null || b.x == null) continue;
      arr[k++] = a.x; arr[k++] = a.y!; arr[k++] = a.z!;
      arr[k++] = b.x; arr[k++] = b.y!; arr[k++] = b.z!;
    }
    if (!attr || attr.array !== arr) seg.geometry.setAttribute("position", new THREE.BufferAttribute(arr, 3));
    else attr.needsUpdate = true;
    seg.geometry.setDrawRange(0, k / 3);
  }, [end]);
  useEffect(() => {
    linesData.current = data.links.filter(merged);
    syncLines();
  }, [data, merged, syncLines]);

  // The perception icons, one at the midpoint of each PERCEIVES edge, always facing the camera. The glyph's shape says the
  // kind; its colour is the kit's fg, or error for the hostile kinds, the accent when the edge is lit, and it fades with the rest
  // when something else is selected.
  // A book with facts on has thousands of these edges, and a sprite each was a draw call each. They are point sets now: one
  // per kind and look (its texture and opacity), every icon of it a point, sized in world units like the sprites were.
  const icons = useRef<THREE.Group | null>(null);
  const iconCount = useRef(0);
  const buildIcons = useCallback(() => {
    const group = icons.current;
    const g = fg.current;
    if (!group || !g) return;
    for (const child of [...group.children]) {
      group.remove(child);
      (child as THREE.Points).geometry.dispose();
    }
    iconCount.current = 0;
    const sets = new Map<string, { material: THREE.PointsMaterial; at: number[] }>();
    // A point's size is in world units at the camera's distance scale: this makes it ICON_SIZE wide, as a sprite of that scale is.
    const size = ICON_SIZE / Math.tan((((g.camera() as THREE.PerspectiveCamera).fov ?? 50) / 2) * (Math.PI / 180));
    for (const link of data.links) {
      const kind = link.kind;
      if (link.type !== "PERCEIVES" || !isPerceptionKind(kind)) continue;
      const a = end(link.source);
      const b = end(link.target);
      if (!a || !b || a.x == null || b.x == null) continue;
      const lit = isLit(link);
      const dim = selectedId != null && !lit;
      const colour = lit ? colours.accent : perceptionIcons[kind].tone === "error" ? colours.error : colours.Character;
      const key = `perception|${kind}|${colour}|${dim}`;
      let set = sets.get(key);
      if (!set) {
        const material = shared(
          key,
          () => new THREE.PointsMaterial({ map: perceptionTexture(kind, colour, opaque(theme.page)), size, sizeAttenuation: true, transparent: true, alphaTest: 0.05, depthWrite: false, opacity: dim ? 0.3 : 1 }),
        );
        material.size = size;
        sets.set(key, (set = { material, at: [] }));
      }
      set.at.push((a.x + b.x) / 2, (a.y! + b.y!) / 2, (a.z! + b.z!) / 2);
      iconCount.current++;
    }
    for (const { material, at } of sets.values()) {
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute("position", new THREE.Float32BufferAttribute(at, 3));
      const points = new THREE.Points(geometry, material);
      points.frustumCulled = false;
      points.renderOrder = 1;
      group.add(points);
    }
  }, [data, isLit, selectedId, colours, theme, shared, end]);
  useEffect(buildIcons, [buildIcons]);

  // Zooming to the graph, as in the 2D tab: Fit, Focus (largest component), and the automatic fit when the
  // scene first has its nodes and after big re-gatings (Focus when that component holds half the visible nodes). The frame is
  // worked out from the nodes' own positions (the library's zoomToFit measures its objects, and the instanced nodes are not its),
  // with the library's arithmetic: the box of the nodes, seen from where the camera looks, 40 px of padding.
  const gatedRef = useRef(gated);
  gatedRef.current = gated;
  const pending = useRef<"fit" | "focus" | "auto" | null>("auto");
  const shownIds = useRef<Set<number>>(new Set());
  const heightRef = useRef(height);
  heightRef.current = height;
  const zoomTo = useCallback((kind: "fit" | "focus" | "auto") => {
    const g = fg.current;
    if (!g) return;
    const component = kind === "fit" ? null : kind === "focus" ? largestComponent(gatedRef.current) : autoFitTarget(gatedRef.current);
    const pts = dataRef.current.nodes.filter((n) => n.x != null && (!component || component.has(n.id)));
    if (pts.length === 0) return;
    const box = new THREE.Box3();
    for (const n of pts) box.expandByPoint(new THREE.Vector3(n.x! - n.r, n.y! - n.r, n.z! - n.r)).expandByPoint(new THREE.Vector3(n.x! + n.r, n.y! + n.r, n.z! + n.r));
    const camera = g.camera() as THREE.PerspectiveCamera;
    camera.updateMatrixWorld();
    const forward = camera.getWorldDirection(new THREE.Vector3());
    const right = new THREE.Vector3().crossVectors(forward, camera.up).normalize();
    const up = new THREE.Vector3().crossVectors(right, forward).normalize();
    const vHalf = (((1 - (40 * 2) / Math.max(1, heightRef.current)) * camera.fov) / 2) * (Math.PI / 180);
    const hHalf = Math.atan(Math.tan(vHalf) * camera.aspect);
    const centre = box.getCenter(new THREE.Vector3());
    let distance = 0;
    for (const x of [box.min.x, box.max.x])
      for (const y of [box.min.y, box.max.y])
        for (const z of [box.min.z, box.max.z]) {
          const local = new THREE.Vector3(x, y, z).sub(centre);
          distance = Math.max(distance, Math.max(Math.abs(local.dot(up)) / Math.tan(vHalf), Math.abs(local.dot(right)) / Math.tan(hHalf)) - local.dot(forward));
        }
    g.cameraPosition({ x: centre.x - forward.x * distance, y: centre.y - forward.y * distance, z: centre.z - forward.z * distance }, centre, 400);
  }, []);

  // Fog in the page colour. It starts just beyond the far side of the Focus component (its radius around its centre, measured
  // when the layout settles) as seen from the camera, so that component stays fully lit and distant strays fade.
  const focusRadius = useRef(200);
  const updateFog = useCallback(() => {
    const g = fg.current;
    if (!g) return;
    const scene = g.scene();
    const fog = scene.fog instanceof THREE.Fog ? scene.fog : (scene.fog = new THREE.Fog(opaque(theme.page), 1, 2000));
    const controls = g.controls() as { target?: THREE.Vector3 };
    const d = g.camera().position.distanceTo(controls.target ?? new THREE.Vector3());
    fog.near = d + focusRadius.current;
    fog.far = fog.near + Math.max(300, 2.5 * focusRadius.current);
  }, [theme]);
  const measureFocus = useCallback(() => {
    const component = largestComponent(gatedRef.current);
    const pts = [...cache.current.values()].filter((n) => n.x != null && (!component || component.has(n.id)));
    if (pts.length === 0) return;
    const c = pts.reduce((a, n) => ({ x: a.x + n.x! / pts.length, y: a.y + n.y! / pts.length, z: a.z + n.z! / pts.length }), { x: 0, y: 0, z: 0 });
    focusRadius.current = Math.max(60, ...pts.map((n) => Math.hypot(n.x! - c.x, n.y! - c.y, n.z! - c.z) + n.r));
  }, []);

  useEffect(() => {
    const ids = new Set(data.nodes.map((n) => n.id));
    if (ids.size > 0 && bigChange(shownIds.current, ids)) pending.current = "auto";
    shownIds.current = ids;
  }, [data]);
  // The library's engine is given no ticks (cooldownTicks 0), so it stops at once after taking the data or a changed look:
  // that is when its objects are in place. Nothing settles; this only measures the Focus component, makes a fit that is owed, and
  // sets the fog. The first stop is also when the scene exists (the lines, the icons and the instanced nodes are attached then).
  const [ready, setReady] = useState(false);
  const onStop = useCallback(() => {
    if (!ready) setReady(true);
    if (dataRef.current.nodes.length === 0) return; // a book still being laid out: the fit waits for its nodes
    measureFocus();
    if (pending.current) zoomTo(pending.current);
    pending.current = null;
    updateFog();
  }, [ready, zoomTo, measureFocus, updateFog]);

  // Orbit controls (they keep "up", so labels stay under their nodes), a slow auto-rotate that any drag stops, and the
  // fog following the camera. Attached once the graph exists.
  useEffect(() => {
    const g = fg.current;
    if (!ready || !g) return;
    const seg = new THREE.LineSegments(
      new THREE.BufferGeometry(),
      new THREE.LineBasicMaterial({ color: colours.link, transparent: true, opacity: 0.35, depthWrite: false }),
    );
    seg.frustumCulled = false;
    g.scene().add(seg);
    lines.current = seg;
    syncLines();
    return () => {
      g.scene().remove(seg);
      seg.geometry.dispose();
      (seg.material as THREE.Material).dispose();
      lines.current = null;
    };
  }, [ready, colours, syncLines]);
  // The group the instanced Facts and Events live in, once the scene exists; the meshes are (re)built by buildInstanced.
  const buildInstancedRef = useRef(buildInstanced);
  buildInstancedRef.current = buildInstanced;
  useEffect(() => {
    const g = fg.current;
    if (!ready || !g) return;
    const group = new THREE.Group();
    g.scene().add(group);
    instancedGroup.current = group;
    buildInstancedRef.current();
    return () => {
      g.scene().remove(group);
      for (const { mesh } of instanced.current) mesh.dispose();
      instanced.current = [];
      instancedGroup.current = null;
    };
  }, [ready]);
  // The group the perception icons live in, once the scene exists; the point sets are (re)built by buildIcons.
  const buildIconsRef = useRef(buildIcons);
  buildIconsRef.current = buildIcons;
  useEffect(() => {
    const g = fg.current;
    if (!ready || !g) return;
    const group = new THREE.Group();
    g.scene().add(group);
    icons.current = group;
    buildIconsRef.current();
    return () => {
      g.scene().remove(group);
      for (const child of group.children) (child as THREE.Points).geometry.dispose();
      group.clear();
      icons.current = null;
    };
  }, [ready]);
  const stopRef = useRef(onAutoRotateStop);
  stopRef.current = onAutoRotateStop;
  useEffect(() => {
    const g = fg.current;
    if (!ready || !g) return;
    const controls = g.controls() as THREE.EventDispatcher<{ start: object; change: object }> & { autoRotate?: boolean };
    const onStart = () => {
      if (controls.autoRotate) stopRef.current?.();
    };
    controls.addEventListener("start", onStart);
    controls.addEventListener("change", updateFog);
    return () => {
      controls.removeEventListener("start", onStart);
      controls.removeEventListener("change", updateFog);
    };
  }, [ready, updateFog]);
  useEffect(() => {
    const g = fg.current;
    if (!ready || !g) return;
    const controls = g.controls() as { autoRotate?: boolean; autoRotateSpeed?: number };
    controls.autoRotate = autoRotate;
    controls.autoRotateSpeed = 0.6;
  }, [ready, autoRotate]);

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
  useEffect(() => {
    if (resetRequest > 0) request("focus");
  }, [resetRequest, request]);

  // A node selected from the dropdown or the roster: the camera flies (600 ms) to frame it and its neighbours, keeping its
  // viewing direction. A node click selects without moving the camera.
  const clicked = useRef<number | null>(null);
  const nearRef = useRef(near);
  nearRef.current = near;
  const degreeRef = useRef(degree);
  degreeRef.current = degree;
  useEffect(() => {
    if (selectedId == null) return;
    if (clicked.current === selectedId) {
      clicked.current = null;
      return;
    }
    const g = fg.current;
    const n = cache.current.get(selectedId);
    if (!g || !n || n.x == null) return;
    // A hub (d'Artagnan with facts on) has hundreds of neighbours; frame it with its ~15 best-connected people and places.
    const targets = flyTargets(nearRef.current, (id) => cache.current.get(id)?.label, degreeRef.current);
    const pts = targets.map((id) => cache.current.get(id)).filter((p): p is N3 => !!p && p.x != null);
    const camera = g.camera() as THREE.PerspectiveCamera;
    const { position, lookAt } = frameAround(
      { x: n.x, y: n.y!, z: n.z!, r: n.r },
      pts.map((p) => ({ x: p.x!, y: p.y!, z: p.z! })),
      camera.position,
      camera.fov ?? 40,
    );
    g.cameraPosition(position, lookAt, 600);
  }, [selectedId]);

  // The Details card's anchor: the selected node's screen position, every frame while something is selected.
  const frameRef = useRef(onFrame);
  frameRef.current = onFrame;
  useEffect(() => {
    if (selectedId == null) {
      frameRef.current?.(null);
      return;
    }
    let raf = 0;
    const tick = () => {
      const n = cache.current.get(selectedId);
      const g = fg.current;
      frameRef.current?.(n && n.x != null && g && data.nodes.includes(n) ? g.graph2ScreenCoords(n.x, n.y!, n.z!) : null);
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [selectedId, data]);

  // Dev builds only: lets the Playwright checks read the camera, a node's screen position, auto-rotate and the fog.
  useEffect(() => {
    if (!import.meta.env.DEV) return;
    (window as unknown as { __graph3d?: unknown }).__graph3d = {
      camera: () => {
        const c = fg.current?.camera().position;
        return c ? { x: c.x, y: c.y, z: c.z } : null;
      },
      screenOf: (id: number) => {
        const n = cache.current.get(id);
        return n && n.x != null && fg.current ? fg.current.graph2ScreenCoords(n.x, n.y!, n.z!) : null;
      },
      // World positions and the fly-to's chosen neighbours, for checking the hub framing.
      pos: (id: number) => {
        const n = cache.current.get(id);
        return n && n.x != null ? { x: n.x, y: n.y!, z: n.z!, r: n.r } : null;
      },
      flyTargets: (id: number) =>
        id === selectedId ? flyTargets(nearRef.current, (i) => cache.current.get(i)?.label, degreeRef.current) : null,
      autoRotate: () => !!(fg.current?.controls() as { autoRotate?: boolean } | undefined)?.autoRotate,
      // How the scene is drawn: the nodes the library draws (people and places), the instanced ones and their meshes.
      drawn: () => ({
        library: dataRef.current.nodes.filter((n) => isEntityLabel(n.label)).length,
        instanced: instanced.current.reduce((a, i) => a + i.ids.length, 0),
        meshes: instanced.current.length,
      }),
      // What one frame costs the renderer (draw calls, triangles) and how many perception icons and library links are drawn.
      render: () => {
        const info = (fg.current?.renderer() as unknown as { info?: { render: { calls: number; triangles: number } } } | undefined)?.info?.render;
        return { calls: info?.calls ?? 0, triangles: info?.triangles ?? 0, icons: iconCount.current, mergedLinks: linesData.current.length, links: dataRef.current.links.length };
      },
      // The screen position of an instanced node and what a pointer there picks (the checks click a Fact with these).
      pick: (x: number, y: number) => pick({ clientX: x, clientY: y }),
      fog: () => {
        const f = fg.current?.scene().fog;
        return f instanceof THREE.Fog ? { near: f.near, far: f.far } : null;
      },
    };
  });

  const tooltip = useCallback(
    (n: N3) => {
      const ch = chapterOf(payload.chapters, n.pos);
      return `<small>${escapeHtml(n.label)}${ch ? ` · first seen ch. ${ch}` : ""}</small><br>${escapeHtml(n.display)}`;
    },
    [payload],
  );

  // The tooltip of an instanced node (the library shows its own for the nodes it draws). The pointer is tested against the
  // instanced meshes once a frame at most, and not while a button is down (a drag turns the camera).
  const tip = useRef<HTMLDivElement>(null);
  const libraryHover = useRef(false);
  useEffect(() => {
    const g = fg.current;
    const el = tip.current;
    if (!ready || !g || !el) return;
    const canvas = g.renderer().domElement as HTMLCanvasElement;
    let last: PointerEvent | null = null;
    let raf = 0;
    const hide = () => (el.style.visibility = "hidden");
    const show = () => {
      raf = 0;
      const ev = last;
      const id = ev && ev.buttons === 0 && !libraryHover.current ? pick(ev) : null;
      const n = id == null ? undefined : cache.current.get(id);
      if (!ev || !n) return hide();
      const rect = canvas.getBoundingClientRect();
      el.innerHTML = tooltip(n);
      el.style.left = `${ev.clientX - rect.left + 14}px`;
      el.style.top = `${ev.clientY - rect.top + 14}px`;
      el.style.visibility = "visible";
    };
    const move = (ev: PointerEvent) => {
      last = ev;
      if (!raf) raf = requestAnimationFrame(show);
    };
    canvas.addEventListener("pointermove", move);
    canvas.addEventListener("pointerleave", hide);
    return () => {
      canvas.removeEventListener("pointermove", move);
      canvas.removeEventListener("pointerleave", hide);
      cancelAnimationFrame(raf);
      hide();
    };
  }, [ready, pick, tooltip]);

  return (
    <>
    <ForceGraph3D<N3, L3>
      ref={fg}
      controlType="orbit"
      rendererConfig={{ antialias: false, powerPreference: "high-performance" }}
      warmupTicks={0}
      cooldownTicks={0}
      onEngineStop={onStop}
      nodeVisibility={(n) => isEntityLabel(n.label)}
      onNodeHover={(n) => {
        libraryHover.current = n != null;
      }}
      width={width}
      height={height}
      graphData={libraryData}
      backgroundColor={opaque(theme.page)}
      nodeId="id"
      nodeLabel={tooltip}
      nodeThreeObject={object}
      linkColor={linkColour}
      linkWidth={linkWidth}
      linkOpacity={1}
      enableNodeDrag={false}
      showNavInfo={false}
      onNodeClick={(n) => {
        clicked.current = n.id;
        onSelect(n.id);
      }}
      // A click the library found none of its own nodes under: an instanced Fact or Event there is selected, else the selection goes.
      onBackgroundClick={(ev) => {
        const id = pick(ev);
        if (id != null) clicked.current = id;
        onSelect(id);
      }}
    />
    <div ref={tip} className="float-tooltip-kap" style={{ visibility: "hidden", zIndex: 1 }} />
    </>
  );
}

/** "#rrggbb" with an alpha, as rgba() (three-forcegraph reads a link colour's alpha into its opacity). */
function withAlpha(hex: string, alpha: number): string {
  const v = parseInt(hex.slice(1), 16);
  return `rgba(${(v >> 16) & 255}, ${(v >> 8) & 255}, ${v & 255}, ${alpha})`;
}
