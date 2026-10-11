// The fallback for a book database with no stored layout (one built before `index layout`, or where Node.js was missing).
// The same force layout as web/scripts/layout.mjs (forceLayout.js), run here, off the main thread: the page stays responsive and the
// picture appears when this posts the positions back. The three books ship with layouts, so this path is not tuned.
import { layoutGraph } from "./forceLayout.js";

export type LayoutRequest = { nodes: { id: number; label: string }[]; edges: { source: number; target: number }[] };
/** ids[i] is at (xyz[3i], xyz[3i+1], xyz[3i+2]) in the 3D layout and (xy[2i], xy[2i+1]) in the 2D one. */
export type LayoutReply = { ids: Int32Array; xyz: Float32Array; xy: Float32Array };

self.onmessage = (e: MessageEvent<LayoutRequest>) => {
  const { positions } = layoutGraph(e.data.nodes, e.data.edges);
  const ids = new Int32Array(positions.size);
  const xyz = new Float32Array(positions.size * 3);
  const xy = new Float32Array(positions.size * 2);
  let i = 0;
  for (const [id, p] of positions) {
    ids[i] = id;
    xyz.set([p.x, p.y, p.z], 3 * i);
    xy.set([p.x2, p.y2], 2 * i);
    i++;
  }
  const reply: LayoutReply = { ids, xyz, xy };
  (self as unknown as { postMessage(message: unknown, transfer: Transferable[]): void }).postMessage(reply, [ids.buffer, xyz.buffer, xy.buffer]);
};
