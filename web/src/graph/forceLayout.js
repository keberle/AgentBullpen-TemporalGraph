// The force layout of a book's graph, as plain JavaScript so that two callers share one set of forces and parameters:
// web/scripts/layout.mjs (Node, run by `index layout`: the layout every book ships with) and layout.worker.ts (the browser's fallback
// for a book database without one, off the main thread). The graph tabs themselves run no simulation. Types: forceLayout.d.ts.

import { forceCenter, forceCollide, forceLink, forceManyBody, forceSimulation } from "d3-force-3d";

export const ALGORITHM = "d3-force-3d 3.0.6";
export const SEED = 66;
export const ITERATIONS = 300;

/** The radius the tabs draw a node with (model.ts nodeRadius): the 2D layout keeps nodes that far apart. */
export function nodeRadius(label, degree) {
  if (label === "Character") return 9 + Math.min(14, Math.sqrt(degree || 1) * 1.8);
  if (label === "Location") return 7 + Math.min(8, Math.sqrt(degree || 1));
  return label === "Event" ? 4.5 : 3.2;
}
const isEntity = (label) => label === "Character" || label === "Location";

/** A seeded generator in [0, 1) (a linear congruential one, as d3 uses), so a run can be repeated. */
function lcg(seed) {
  let s = seed >>> 0;
  return () => (s = (1664525 * s + 1013904223) >>> 0) / 4294967296;
}

function run(simulation, iterations) {
  simulation.stop();
  for (let i = 0; i < iterations; i++) simulation.tick();
  return simulation.alpha();
}

/** Moves the centroid to the origin, rounds to two decimals, and returns the largest distance from the origin. */
function centre(nodes, dims) {
  const n = Math.max(1, nodes.length);
  const c = dims.map((d) => nodes.reduce((a, p) => a + p[d], 0) / n);
  let radius = 0;
  for (const p of nodes) {
    let r2 = 0;
    dims.forEach((d, i) => {
      p[d] = Math.round((p[d] - c[i]) * 100) / 100;
      r2 += p[d] * p[d];
    });
    radius = Math.max(radius, Math.sqrt(r2));
  }
  return Math.round(radius * 100) / 100;
}

/**
 * The two layouts of a graph. `nodes`: [{ id, label }], `edges`: [{ source, target }] (ids). Returns the positions by node id and
 * what was measured.
 *
 * 3D: the forces react-force-graph-3d applied by default (the 3D tab set none of its own): links at d3's default distance (30) and
 * strength, a many-body charge of -30, a centre force; alpha decays at d3's default rate.
 * 2D: the forces Graph2D.tsx set (the approved mockup's): a link between two people or places is long and loose (90, 0.25), any other
 * short and tight (28, 0.6); people and places repel at -260 and facts and events at -30, out to 250 units and no further, so the
 * centre force can bring unlinked nodes in; no two nodes overlap (radius + 3); alpha decays at 0.03 a tick.
 */
export function layoutGraph(nodes, edges, { seed = SEED, iterations = ITERATIONS } = {}) {
  const degree = new Map();
  for (const e of edges) {
    degree.set(e.source, (degree.get(e.source) ?? 0) + 1);
    degree.set(e.target, (degree.get(e.target) ?? 0) + 1);
  }
  const known = new Set(nodes.map((n) => n.id));
  const linked = edges.filter((e) => known.has(e.source) && known.has(e.target));

  const n3 = nodes.map((n) => ({ id: n.id }));
  const sim3 = forceSimulation(n3, 3)
    .randomSource(lcg(seed))
    .force("link", forceLink(linked.map((e) => ({ source: e.source, target: e.target }))).id((n) => n.id))
    .force("charge", forceManyBody())
    .force("center", forceCenter());
  const alpha3 = run(sim3, iterations);
  const radius3 = centre(n3, ["x", "y", "z"]);

  const label = new Map(nodes.map((n) => [n.id, n.label]));
  const n2 = nodes.map((n) => ({ id: n.id, label: n.label, r: nodeRadius(n.label, degree.get(n.id) ?? 0) }));
  const ent = (l) => isEntity(label.get(l.source.id ?? l.source)) && isEntity(label.get(l.target.id ?? l.target));
  const sim2 = forceSimulation(n2, 2)
    .randomSource(lcg(seed))
    .alphaDecay(0.03)
    .force(
      "link",
      forceLink(linked.map((e) => ({ source: e.source, target: e.target })))
        .id((n) => n.id)
        .distance((l) => (ent(l) ? 90 : 28))
        .strength((l) => (ent(l) ? 0.25 : 0.6)),
    )
    .force("charge", forceManyBody().strength((n) => (isEntity(n.label) ? -260 : -30)).distanceMax(250))
    .force("center", forceCenter())
    .force("collide", forceCollide((n) => n.r + 3));
  const alpha2 = run(sim2, iterations);
  const radius2 = centre(n2, ["x", "y"]);

  const positions = new Map();
  n3.forEach((p, i) => positions.set(p.id, { x: p.x, y: p.y, z: p.z, x2: n2[i].x, y2: n2[i].y }));
  return {
    positions,
    summary: {
      algorithm: ALGORITHM,
      seed,
      iterations,
      nodes: nodes.length,
      edges: linked.length,
      alpha3: +alpha3.toFixed(6),
      alpha2: +alpha2.toFixed(6),
      radius3,
      radius2,
    },
  };
}
