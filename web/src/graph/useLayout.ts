import { useEffect, useState } from "react";
import type { GraphPayload } from "./model.ts";
import type { LayoutReply, LayoutRequest } from "./layout.worker.ts";

// Where each node is drawn. The positions are data: computed once by `index layout` over the whole graph, stored in the book
// database and sent with the graph (`layout`, `layout2` on every node). The tabs place nodes from them and run no force simulation,
// so gating (the slider, Spoilers, facts on or off) only hides and shows nodes; nothing moves.
//
// A book without a stored layout is laid out once in a Web Worker (layout.worker.ts), never on the main thread; until it answers
// there are no positions and the pane says "Laying out…".

export type Position3 = { x: number; y: number; z: number };
export type Position2 = { x: number; y: number };
export type Layouts = { three: ReadonlyMap<number, Position3>; two: ReadonlyMap<number, Position2> };

/** The stored layout of a payload, when every node has both positions; else null. */
export function storedLayout(payload: GraphPayload): Layouts | null {
  const three = new Map<number, Position3>();
  const two = new Map<number, Position2>();
  for (const n of payload.nodes) {
    if (!n.layout || !n.layout2) return null;
    three.set(n.id, n.layout);
    two.set(n.id, n.layout2);
  }
  return payload.nodes.length > 0 ? { three, two } : null;
}

/** The worker's reply as the two maps. */
export function fromReply(reply: LayoutReply): Layouts {
  const three = new Map<number, Position3>();
  const two = new Map<number, Position2>();
  reply.ids.forEach((id, i) => {
    three.set(id, { x: reply.xyz[3 * i], y: reply.xyz[3 * i + 1], z: reply.xyz[3 * i + 2] });
    two.set(id, { x: reply.xy[2 * i], y: reply.xy[2 * i + 1] });
  });
  return { three, two };
}

// One layout per payload, shared by the 2D and the 3D tab (a tab switch does not lay the graph out again).
const computed = new WeakMap<GraphPayload, Promise<Layouts>>();

/** For tests: how the fallback worker is made. */
export const workerFactory = { create: (): Worker => new Worker(new URL("./layout.worker.ts", import.meta.url), { type: "module" }) };

function layoutInWorker(payload: GraphPayload): Promise<Layouts> {
  let promise = computed.get(payload);
  if (!promise) {
    promise = new Promise<Layouts>((resolve, reject) => {
      const worker = workerFactory.create();
      worker.onmessage = (e: MessageEvent<LayoutReply>) => {
        resolve(fromReply(e.data));
        worker.terminate();
      };
      worker.onerror = (e) => {
        reject(new Error(e.message || "the layout worker failed"));
        worker.terminate();
      };
      const request: LayoutRequest = {
        nodes: payload.nodes.map((n) => ({ id: n.id, label: n.label })),
        edges: payload.edges.map((e) => ({ source: e.source, target: e.target })),
      };
      worker.postMessage(request);
    });
    computed.set(payload, promise);
  }
  return promise;
}

/**
 * The layouts of a payload: the stored ones at once; else null while a worker lays the graph out (`pending`), then its result.
 * Without Worker support (a test DOM) a book with no stored layout stays pending.
 */
export function useLayout(payload: GraphPayload): { layouts: Layouts | null; pending: boolean } {
  const [state, setState] = useState<{ payload: GraphPayload; layouts: Layouts | null }>(() => ({ payload, layouts: storedLayout(payload) }));
  const current = state.payload === payload ? state : { payload, layouts: storedLayout(payload) };
  if (current !== state) setState(current);
  useEffect(() => {
    if (current.layouts || typeof Worker === "undefined") return;
    let live = true;
    layoutInWorker(payload).then(
      (layouts) => live && setState({ payload, layouts }),
      () => {}, // no layout: the pane keeps its line; nothing else on the page depends on it
    );
    return () => {
      live = false;
    };
  }, [payload, current.layouts]);
  return { layouts: current.layouts, pending: current.layouts === null };
}
