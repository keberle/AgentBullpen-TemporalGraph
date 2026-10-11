// @vitest-environment jsdom
import { act, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { degrees, gate, noHighlight, type Gated, type GraphPayload } from "./model.ts";
import type { LayoutReply } from "./layout.worker.ts";

// The graph tabs run no force simulation. With the stored layout in the payload each node is pinned where it is stored, the
// library's engine is given no ticks, no force is ever configured and no tick callback is even handed over; a re-gating reuses the
// node and link objects. Without a stored layout the graph is laid out by a worker, never on the main thread, and the canvas is
// empty until it answers. The two libraries are replaced by stand-ins that record what they are given (there is no canvas or WebGL
// in the test DOM).

type Given = { graphData: { nodes: Record<string, unknown>[]; links: Record<string, unknown>[] } } & Record<string, unknown>;
const lib = vi.hoisted(() => ({ three: [] as Record<string, unknown>[], two: [] as Record<string, unknown>[], d3Force: [] as string[], reheats: 0 }));

function standIn(which: "three" | "two") {
  return async () => {
    const React = await import("react");
    const Fake = React.forwardRef((props: Record<string, unknown>, ref) => {
      lib[which].push(props);
      React.useImperativeHandle(
        ref,
        () => ({
          renderer: () => null,
          zoomToFit: () => {},
          cameraPosition: () => {},
          centerAt: () => {},
          zoom: () => 1,
          d3Force: (name: string) => void lib.d3Force.push(name),
          d3ReheatSimulation: () => void lib.reheats++,
        }),
        [],
      );
      return <div data-fake={which} />;
    });
    return { default: Fake };
  };
}
vi.mock("react-force-graph-3d", standIn("three"));
vi.mock("react-force-graph-2d", standIn("two"));

const { default: Graph3D } = await import("./Graph3D.tsx");
const { default: Graph2D } = await import("./Graph2D.tsx");

const at = (id: number) => ({ layout: { x: id * 10, y: id * 20, z: id * 30 }, layout2: { x: id * -10, y: id * -20 } });
function book(withLayout: boolean): GraphPayload {
  const node = (id: number, label: string, pos: number, name: string | null, text: string | null) => ({
    id,
    label,
    name,
    text,
    pos,
    aliasFirstPos: name ? [{ alias: name, pos }] : [],
    ...(withLayout ? at(id) : {}),
  });
  return {
    nodes: [node(1, "Character", 10, "Jim Hawkins", null), node(2, "Character", 20, "Long John Silver", null), node(3, "Location", 1500, "Hispaniola", null), node(4, "Fact", 1600, null, "Jim found the map")],
    edges: [
      { id: 1, source: 1, target: 2, type: "MET", pos: 20 },
      { id: 2, source: 4, target: 1, type: "ABOUT", pos: 1600 },
      { id: 3, source: 2, target: 3, type: "LOCATED_AT", pos: 1500 },
    ],
    chapters: [
      { number: 1, charEnd: 1000 },
      { number: 2, charEnd: 2000 },
    ],
  };
}
const gated = (payload: GraphPayload, chapter: number, facts = true): Gated => gate(payload, { chapter, spoilers: false, facts, highlight: noHighlight });

class FakeWorker {
  static made: FakeWorker[] = [];
  onmessage: ((e: { data: LayoutReply }) => void) | null = null;
  onerror: ((e: { message: string }) => void) | null = null;
  posted: unknown[] = [];
  terminated = false;
  constructor() {
    FakeWorker.made.push(this);
  }
  postMessage(message: unknown) {
    this.posted.push(message);
  }
  terminate() {
    this.terminated = true;
  }
  /** The worker's answer: every node of the request, at a position made from its id. */
  answer() {
    const { nodes } = this.posted[0] as { nodes: { id: number }[] };
    this.onmessage?.({
      data: {
        ids: Int32Array.from(nodes.map((n) => n.id)),
        xyz: Float32Array.from(nodes.flatMap((n) => [n.id, 2 * n.id, 3 * n.id])),
        xy: Float32Array.from(nodes.flatMap((n) => [-n.id, -2 * n.id])),
      },
    });
  }
}

type Observer = { target: Element; callback: ResizeObserverCallback };
const observers: Observer[] = [];
let host: HTMLDivElement;
let root: Root;
const render = (node: ReactNode) => act(async () => root.render(node));
/** Every observed element measured at 800 × 600, as the browser's ResizeObserver reports it. */
const measure = () =>
  act(async () => {
    for (const o of [...observers]) o.callback([{ contentRect: { width: 800, height: 600 } } as ResizeObserverEntry], null as never);
  });
const last = (which: "three" | "two") => lib[which][lib[which].length - 1] as Given;
const status = () => host.querySelector("[role='status']")?.textContent ?? null;

function tab(which: "three" | "two", payload: GraphPayload, g: Gated) {
  const shared = { payload, gated: g, degree: degrees(payload), highlight: noHighlight, selectedId: null, onSelect: () => {} };
  return which === "three" ? <Graph3D {...shared} /> : <Graph2D {...shared} />;
}

beforeEach(() => {
  Object.assign(lib, { three: [], two: [], d3Force: [], reheats: 0 });
  FakeWorker.made = [];
  observers.length = 0;
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  vi.stubGlobal(
    "ResizeObserver",
    class {
      constructor(private callback: ResizeObserverCallback) {}
      observe(target: Element) {
        observers.push({ target, callback: this.callback });
      }
      unobserve() {}
      disconnect() {
        for (let i = observers.length - 1; i >= 0; i--) if (observers[i].callback === this.callback) observers.splice(i, 1);
      }
    },
  );
  vi.stubGlobal("Worker", FakeWorker);
  host = document.body.appendChild(document.createElement("div"));
  root = createRoot(host);
});
afterEach(async () => {
  await act(async () => root.unmount());
  host.remove();
  vi.unstubAllGlobals();
});

describe.each(["three", "two"] as const)("the %s-dimensional tab with a stored layout", (which) => {
  it("pins every node where it is stored and gives the engine nothing to do", async () => {
    const payload = book(true);
    await render(tab(which, payload, gated(payload, 2)));
    await measure();
    const given = last(which);
    expect(given.graphData.nodes.map((n) => n.id)).toEqual([1, 2, 3, 4]);
    for (const n of given.graphData.nodes) {
      const id = n.id as number;
      const want = which === "three" ? { fx: id * 10, fy: id * 20, fz: id * 30 } : { fx: id * -10, fy: id * -20 };
      expect(n).toMatchObject({ ...want, x: want.fx, y: want.fy });
    }
    // No ticks, and nothing that would start or steer a simulation: no tick callback, no alpha settings, no force configured.
    expect(given).toMatchObject({ cooldownTicks: 0, warmupTicks: 0, enableNodeDrag: false });
    for (const prop of ["onEngineTick", "d3AlphaMin", "d3AlphaDecay", "d3VelocityDecay", "cooldownTime"]) expect(given[prop]).toBeUndefined();
    expect(typeof given.onEngineStop).toBe("function");
    expect(lib.d3Force).toEqual([]);
    expect(lib.reheats).toBe(0);
    expect(FakeWorker.made).toEqual([]);
    expect(status()).toBeNull();
    expect(host.querySelector("[data-layout]")!.getAttribute("data-layout")).toBe("ready");
  });

  it("re-gating hides and shows nodes without moving or remaking them, links included", async () => {
    const payload = book(true);
    await render(tab(which, payload, gated(payload, 2)));
    await measure();
    const before = last(which).graphData;
    const jim = before.nodes.find((n) => n.id === 1)!;
    // The 3D tab hands the library only the links it draws itself (people and places); the fact's link is its own line set.
    const met = before.links.find((l) => l.id === 1)!;
    expect(before.links.map((l) => l.id)).toEqual(which === "three" ? [1, 3] : [1, 2, 3]);

    await render(tab(which, payload, gated(payload, 1, false))); // chapter 1, facts off: Silver's place and the fact go
    const fewer = last(which).graphData;
    expect(fewer.nodes.map((n) => n.id)).toEqual([1, 2]);
    expect(fewer.nodes.find((n) => n.id === 1)).toBe(jim);
    expect(fewer.links.find((l) => l.id === 1)).toBe(met);

    await render(tab(which, payload, gated(payload, 2))); // and back
    const again = last(which).graphData;
    expect(again.nodes.find((n) => n.id === 1)).toBe(jim);
    expect(again.nodes.find((n) => n.id === 3)).toBe(before.nodes.find((n) => n.id === 3));
    expect(again.links.find((l) => l.id === 3)).toBe(before.links.find((l) => l.id === 3));
    expect(jim).toMatchObject(which === "three" ? { fx: 10, fy: 20, fz: 30 } : { fx: -10, fy: -20 });
    expect(lib.d3Force).toEqual([]);
  });
});

describe.each(["three", "two"] as const)("the %s-dimensional tab without a stored layout", (which) => {
  it("lays the whole graph out in a worker, shows nothing until it answers, and still runs no simulation", async () => {
    const payload = book(false);
    await render(tab(which, payload, gated(payload, 1)));
    await measure();
    // One worker, sent every node and edge of the book (not only what the gate shows), and nothing drawn meanwhile.
    expect(FakeWorker.made.length).toBe(1);
    const worker = FakeWorker.made[0];
    expect(worker.posted).toEqual([
      { nodes: payload.nodes.map((n) => ({ id: n.id, label: n.label })), edges: payload.edges.map((e) => ({ source: e.source, target: e.target })) },
    ]);
    expect(last(which).graphData).toEqual({ nodes: [], links: [] });
    expect(status()).toBe("Laying out…");
    expect(host.querySelector("[data-layout]")!.getAttribute("data-layout")).toBe("pending");

    await act(async () => worker.answer());
    const given = last(which);
    expect(given.graphData.nodes.map((n) => n.id)).toEqual([1, 2]);
    expect(given.graphData.nodes[1]).toMatchObject(which === "three" ? { fx: 2, fy: 4, fz: 6 } : { fx: -2, fy: -4 });
    expect(given).toMatchObject({ cooldownTicks: 0, warmupTicks: 0 });
    expect(given.onEngineTick).toBeUndefined();
    expect(status()).toBeNull();
    expect(worker.terminated).toBe(true);
    expect(lib.d3Force).toEqual([]);

    // The same payload again (the other tab, or this one after a tab switch) does not lay it out a second time.
    await act(async () => root.unmount());
    root = createRoot(host);
    await render(tab(which === "three" ? "two" : "three", payload, gated(payload, 1)));
    await measure();
    expect(FakeWorker.made.length).toBe(1);
    expect(last(which === "three" ? "two" : "three").graphData.nodes.map((n) => n.id)).toEqual([1, 2]);
  });
});
