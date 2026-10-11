import { describe, expect, it } from "vitest";
import { ALGORITHM, ITERATIONS, SEED, layoutGraph, nodeRadius as layoutRadius } from "./forceLayout.js";
import { nodeRadius } from "./model.ts";
import { fromReply, storedLayout } from "./useLayout.ts";
import type { GraphPayload } from "./model.ts";

// The layout is computed once, by code that gives the same numbers every time.

const nodes = [
  { id: 1, label: "Character" },
  { id: 2, label: "Character" },
  { id: 3, label: "Location" },
  { id: 4, label: "Fact" },
  { id: 5, label: "Event" },
  { id: 6, label: "Fact" }, // linked to nothing
];
const edges = [
  { source: 1, target: 2 },
  { source: 1, target: 3 },
  { source: 5, target: 4 },
  { source: 1, target: 5 },
  { source: 2, target: 5 },
  { source: 9, target: 1 }, // an end the graph does not have: left out, not an error
];

describe("the force layout", () => {
  it("places every node, in both layouts, and gives the same positions every time", () => {
    const a = layoutGraph(nodes, edges);
    const b = layoutGraph(nodes, edges);
    expect([...a.positions.keys()]).toEqual(nodes.map((n) => n.id));
    for (const p of a.positions.values()) for (const v of [p.x, p.y, p.z, p.x2, p.y2]) expect(Number.isFinite(v)).toBe(true);
    expect([...a.positions]).toEqual([...b.positions]);
    expect(a.summary).toEqual(b.summary);
    expect(a.summary).toMatchObject({ algorithm: ALGORITHM, seed: SEED, iterations: ITERATIONS, nodes: 6, edges: 5 });
  });

  it("is centred on the origin, to two decimals, and reports how far it reaches", () => {
    const { positions, summary } = layoutGraph(nodes, edges);
    const all = [...positions.values()];
    for (const key of ["x", "y", "z", "x2", "y2"] as const) {
      expect(Math.abs(all.reduce((s, p) => s + p[key], 0) / all.length)).toBeLessThan(0.02);
      for (const p of all) expect(Math.round(p[key] * 100) / 100).toBe(p[key]);
    }
    expect(summary.radius3).toBeCloseTo(Math.max(...all.map((p) => Math.hypot(p.x, p.y, p.z))), 1);
    expect(summary.radius2).toBeCloseTo(Math.max(...all.map((p) => Math.hypot(p.x2, p.y2))), 1);
  });

  it("has run to the end: 300 ticks leave d3's alpha under the 0.005 the tabs used to stop at", () => {
    const { summary } = layoutGraph(nodes, edges);
    expect(summary.alpha3).toBeLessThan(0.005);
    expect(summary.alpha2).toBeLessThan(0.005);
  });

  it("another seed or tick count is another run, recorded as such", () => {
    expect(layoutGraph(nodes, edges, { iterations: 10 }).summary).toMatchObject({ iterations: 10 });
    expect(layoutGraph(nodes, edges, { seed: 7 }).summary.seed).toBe(7);
  });

  it("keeps 2D nodes apart by the radius the tab draws them with", () => {
    for (const [label, degree] of [["Character", 0], ["Character", 40], ["Location", 9], ["Event", 3], ["Fact", 1]] as const)
      expect(layoutRadius(label, degree)).toBe(nodeRadius(label, degree));
    const { positions } = layoutGraph(nodes, edges);
    const [a, b] = [positions.get(1)!, positions.get(2)!];
    expect(Math.hypot(a.x2 - b.x2, a.y2 - b.y2)).toBeGreaterThan(nodeRadius("Character", 3) + nodeRadius("Character", 2));
  });
});

describe("the layout a payload carries", () => {
  const node = (id: number, layout: boolean) => ({
    id,
    label: "Character",
    name: `n${id}`,
    text: null,
    pos: 1,
    aliasFirstPos: [],
    layout: layout ? { x: id, y: 2 * id, z: 3 * id } : null,
    layout2: layout ? { x: -id, y: -2 * id } : null,
  });
  const payload = (nodes: ReturnType<typeof node>[]): GraphPayload => ({ nodes, edges: [], chapters: [] });

  it("is used when every node has both positions", () => {
    const stored = storedLayout(payload([node(1, true), node(2, true)]))!;
    expect(stored.three.get(2)).toEqual({ x: 2, y: 4, z: 6 });
    expect(stored.two.get(2)).toEqual({ x: -2, y: -4 });
  });

  it("is not used when a node lacks one (an older database), or for an empty graph", () => {
    expect(storedLayout(payload([node(1, true), node(2, false)]))).toBeNull();
    expect(storedLayout(payload([]))).toBeNull();
  });

  it("reads the worker's reply into the same two maps", () => {
    const layouts = fromReply({ ids: Int32Array.from([7, 8]), xyz: Float32Array.from([1, 2, 3, 4, 5, 6]), xy: Float32Array.from([9, 8, 7, 6]) });
    expect(layouts.three.get(8)).toEqual({ x: 4, y: 5, z: 6 });
    expect(layouts.two.get(7)).toEqual({ x: 9, y: 8 });
  });
});
