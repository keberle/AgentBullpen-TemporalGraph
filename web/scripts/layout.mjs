// The graph's layout, computed once. `node web/scripts/layout.mjs <book.sqlite>` reads every node and edge of a book
// database, runs the force simulation the graph tabs used to run in the browser (d3-force-3d, the same forces and parameters),
// and writes each node's position: layout_x/y/z (the 3D tab) and layout2_x/y (the 2D tab). The browser then places nodes from these
// and runs no simulation.
//
// The whole graph is laid out, nothing gated, so a node hidden at the reader's chapter has its place when it appears and nothing
// moves when the slider does. Deterministic: d3's start positions are a fixed spiral, the only randomness (two nodes on one spot) is
// a seeded generator, and the tick count is fixed; the same file gives the same numbers.
//
// Prints one line of JSON (the parameters, the node count, the settle alpha, the radius, the time) on stdout. Exit 0, or 1 with the
// reason on stderr. The forces and their parameters are in web/src/graph/forceLayout.js, which the browser's fallback worker shares.

import { layoutGraph } from "../src/graph/forceLayout.js";
import { pathToFileURL } from "node:url";

export { ALGORITHM, ITERATIONS, SEED, layoutGraph } from "../src/graph/forceLayout.js";

const LAYOUT_COLUMNS = ["layout_x", "layout_y", "layout_z", "layout2_x", "layout2_y"];

/** Lays out the graph in a book database and writes the positions. Returns the summary. */
export async function layoutDatabase(path) {
  const started = Date.now();
  const { DatabaseSync } = await import("node:sqlite");
  const db = new DatabaseSync(path);
  try {
    const have = new Set(db.prepare("PRAGMA table_info(nodes)").all().map((c) => c.name));
    if (have.size === 0) throw new Error("no nodes table: not a book database");
    for (const c of LAYOUT_COLUMNS) if (!have.has(c)) db.exec(`ALTER TABLE nodes ADD COLUMN ${c} REAL NULL`);
    // A node's label as the API gives it: the first in name order.
    const nodes = db
      .prepare("SELECT n.id AS id, (SELECT MIN(l.label) FROM node_labels l WHERE l.node_id = n.id) AS label FROM nodes n ORDER BY n.id")
      .all()
      .map((r) => ({ id: Number(r.id), label: r.label ?? "" }));
    const edges = db
      .prepare("SELECT source_id AS source, target_id AS target FROM edges ORDER BY id")
      .all()
      .map((r) => ({ source: Number(r.source), target: Number(r.target) }));
    const { positions, summary } = layoutGraph(nodes, edges);
    const update = db.prepare("UPDATE nodes SET layout_x = ?, layout_y = ?, layout_z = ?, layout2_x = ?, layout2_y = ? WHERE id = ?");
    db.exec("BEGIN");
    for (const [id, p] of positions) update.run(p.x, p.y, p.z, p.x2, p.y2, id);
    db.exec("COMMIT");
    return { ...summary, computedAt: new Date().toISOString().replace(/\.\d+Z$/, "Z"), milliseconds: Date.now() - started };
  } finally {
    db.close();
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const path = process.argv[2];
  if (!path) {
    console.error("usage: node web/scripts/layout.mjs <book.sqlite>");
    process.exit(1);
  }
  try {
    console.log(JSON.stringify(await layoutDatabase(path)));
  } catch (e) {
    console.error(e instanceof Error ? e.message : String(e));
    process.exit(1);
  }
}
