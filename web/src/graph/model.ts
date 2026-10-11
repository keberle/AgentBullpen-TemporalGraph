// The graph tabs' data rules, shared by the 2D and 3D tabs and tested in model.test.ts: the payload's types, the
// spoiler-safe display name (the chat tools' rule), the chapter gate, a node's revealed rows for the popover, and the
// highlight set kept from the chat's last answer.
// A book on the bi-temporal schema ships both clocks on every node and edge and its `timeline` (isTemporal). For it the gate
// is the spoiler rule, the same rule as C# SpoilerGate (storyAt, visible), an IDENTICAL_TO edge that passes it
// makes its two characters share a display name, and the timeline slider's ticks and story-time axis come from timelineLayout.
// A book indexed before the full schema has an empty timeline and no clocks: everything here behaves for it as it did.

export type AliasPos = { alias: string; pos: number };
/**
 * Full-schema fields (all absent in a book indexed before it): `kind` an Event's "narrated" | "referenced"; `revealedPos` / `retractedPos`
 * the reader's clock; `storyFrom` the story clock as EDTF (display), `storyFromLo` / `storyUntilLo` its ISO bounds (what Spoilers on
 * compares); `subject` the ids of the nodes a Fact is about (there is no ABOUT edge).
 */
export type GraphNode = {
  id: number;
  label: string;
  name: string | null;
  shortName?: string | null;
  rosterName?: string | null;
  text: string | null;
  pos: number | null;
  aliasFirstPos: AliasPos[];
  kind?: string | null;
  revealedPos?: number | null;
  retractedPos?: number | null;
  storyFrom?: string | null;
  storyFromLo?: string | null;
  storyUntilLo?: string | null;
  /** Where the 3D tab draws the node (computed once by `index layout`); null or absent: the book has no stored layout. */
  layout?: { x: number; y: number; z: number } | null;
  /** Where the 2D tab draws it. */
  layout2?: { x: number; y: number } | null;
  subject?: number[] | null;
};
/** `pos` is the edge's revealedPos. `kind` a PERCEIVES edge's kind (also its icon's key), a DISCOVERED edge's how, a RELATED_TO kind. */
export type GraphEdge = {
  id: number;
  source: number;
  target: number;
  type: string;
  pos: number | null;
  kind?: string | null;
  retractedPos?: number | null;
  storyFrom?: string | null;
  storyFromLo?: string | null;
  storyUntilLo?: string | null;
};
export type GraphChapter = { number: number; charEnd: number };
/** One row of the book's timeline: the "anchor" (`note` states the assumed date), a "chapter" (refId its number) or an "event" (refId the Event node's id). */
export type TimelineRow = {
  kind: string;
  refId: number | null;
  label: string | null;
  revealedPos: number | null;
  revealedPosEnd?: number | null;
  storyFrom?: string | null;
  storyUntil?: string | null;
  storyFromLo: string | null;
  storyFromHi: string | null;
  storyUntilLo?: string | null;
  storyUntilHi?: string | null;
  fabulaSeq?: number | null;
  note?: string | null;
};
/** `timeline`: empty (or absent) for a book indexed before the full schema. */
export type GraphPayload = { nodes: GraphNode[]; edges: GraphEdge[]; chapters: GraphChapter[]; timeline?: TimelineRow[] };

export const graphQuery =
  "query($id: String!) { graph(bookId: $id) { " +
  "nodes { id label name shortName rosterName text pos aliasFirstPos { alias pos } kind revealedPos retractedPos storyFrom storyFromLo storyUntilLo subject " +
  "layout { x y z } layout2 { x y } } " +
  "edges { id source target type pos kind retractedPos storyFrom storyFromLo storyUntilLo } " +
  "chapters { number charEnd } " +
  "timeline { kind refId label revealedPos revealedPosEnd storyFrom storyUntil storyFromLo storyFromHi storyUntilLo storyUntilHi fabulaSeq note } } }";

export const isEntityLabel = (label: string) => label === "Character" || label === "Location";

/** An alias's words, lower-cased (letters and digits; anything else separates) — as ChatTools.AliasWords. */
export function aliasWords(alias: string): string[] {
  return alias
    .toLowerCase()
    .split(/[^\p{L}\p{N}]+/u)
    .filter((w) => w.length > 0);
}

/**
 * ChatTools.AmbiguousAliases: the aliases shared with another entity — the alias's words, as a run of whole words, also
 * occur in an alias of a different entity ("Baskerville" in "Sir Charles Baskerville", "Hugo Baskerville", …). Once per book.
 */
export function ambiguousAliases(payload: GraphPayload): Set<string> {
  const owners = new Map<string, Set<number>>();
  for (const n of payload.nodes)
    for (const { alias } of n.aliasFirstPos) {
      const w = aliasWords(alias);
      for (let i = 0; i < w.length; i++)
        for (let j = i + 1; j <= w.length; j++) {
          const key = w.slice(i, j).join(" ");
          const set = owners.get(key) ?? new Set<number>();
          set.add(n.id);
          owners.set(key, set);
        }
    }
  const out = new Set<string>();
  for (const n of payload.nodes)
    for (const { alias } of n.aliasFirstPos) {
      const w = aliasWords(alias);
      if (w.length > 0 && (owners.get(w.join(" "))?.size ?? 0) > 1) out.add(alias);
    }
  return out;
}

/**
 * The name a node is shown under (the chat tools' rule, ChatTools.DisplayName). A roster character's books.json
 * spelling, once the text has used it (spoilers on: always). Then a character's short name (the name the
 * book uses most), whenever the reader has met it: always with spoilers on, with them off once it is mentioned by `pos`. Otherwise:
 * spoilers on, or a node without
 * aliasFirstPos: its canonical name (Facts and Events: their text). Spoilers off: the alias first mentioned by `pos` (ties: the
 * longest, then ordinal), skipping aliases shared with another entity unless nothing else is revealed. No alias
 * mentioned by `pos` → null: the node is not drawn.
 */
export function displayName(node: GraphNode, pos: number, spoilers: boolean, ambiguous?: ReadonlySet<string>): string | null {
  const canonical = node.name ?? node.text ?? "";
  // A roster character's books.json spelling wins once the text has used it (the same name ignoring case and the apostrophe's shape).
  const roster = node.rosterName;
  if (roster && (spoilers || node.aliasFirstPos.some((a) => sameName(a.alias, roster) && a.pos <= pos))) return roster;
  const short = node.shortName;
  if (short && (spoilers || node.aliasFirstPos.some((a) => a.alias === short && a.pos <= pos))) return short;
  if (spoilers || node.aliasFirstPos.length === 0) return canonical;
  const all = node.aliasFirstPos.filter((a) => a.pos <= pos);
  if (all.length === 0) return null;
  const clear = ambiguous ? all.filter((a) => !ambiguous.has(a.alias)) : all;
  const revealed = clear.length > 0 ? clear : all;
  revealed.sort((a, b) => a.pos - b.pos || b.alias.length - a.alias.length || (a.alias < b.alias ? -1 : a.alias > b.alias ? 1 : 0));
  return revealed[0].alias;
}

/** ChatTools.SameName: two spellings of a name are the same ignoring case, surrounding spaces and the apostrophe's shape. */
export function sameName(a: string, b: string): boolean {
  const norm = (s: string) => s.trim().replace(/[\u2018\u2019]/g, "'").toLowerCase();
  return norm(a) === norm(b);
}

/** The end offset of `chapter` ($pos); past the last chapter → the last chapter's end. */
export function chapterEnd(chapters: GraphChapter[], chapter: number): number {
  return (chapters.find((c) => c.number === chapter) ?? chapters[chapters.length - 1])?.charEnd ?? 0;
}

/** The chapter holding `pos` (the first whose end is beyond it), or null. */
export function chapterOf(chapters: GraphChapter[], pos: number | null): number | null {
  if (pos == null) return null;
  return chapters.find((c) => pos < c.charEnd)?.number ?? chapters[chapters.length - 1]?.number ?? null;
}

// ---- The spoiler rule (the same rule as C# SpoilerGate; the two must stay identical) --------------------------------

/** Whether the book is on the full schema: it has a timeline. Everything that differs for it in this file is decided by this. */
export const isTemporal = (payload: GraphPayload): boolean => (payload.timeline?.length ?? 0) > 0;

/** The four clock values the rule reads, as a node carries them (an edge's revealedPos is its `pos`: edgeClocks). */
export type Clocks = { revealedPos?: number | null; retractedPos?: number | null; storyFromLo?: string | null; storyUntilLo?: string | null };
export const edgeClocks = (e: GraphEdge): Clocks => ({ revealedPos: e.pos, retractedPos: e.retractedPos, storyFromLo: e.storyFromLo, storyUntilLo: e.storyUntilLo });

/**
 * `story(pos)` (SpoilerGate.StoryAt): among the timeline's chapter and event rows with `revealedPos ≤ pos` and both `storyFromLo` and
 * `storyFromHi`, the row that starts latest in story time (the greatest `storyFromLo`; ties: the greater `storyFromHi`); the value is
 * that row's `storyFromHi`. It is the furthest the story clock has reached in what the reader has been shown: a flashback told late
 * never turns it back, and a date known only to the year ("1889", whose upper bound is 31 December) does not carry it to the year's
 * end while day-precise rows stand later than its start. An event row counts from its `revealedPos`; a chapter row once the chapter
 * has been read to its end (`revealedPosEnd ≤ pos`). Dates are ISO yyyy-MM-dd and compare as text. Null when no row qualifies.
 */
export function storyAt(timeline: readonly TimelineRow[], pos: number): string | null {
  let latest: TimelineRow | null = null;
  for (const row of timeline) {
    if ((row.kind !== "chapter" && row.kind !== "event") || row.revealedPos == null || row.revealedPos > pos) continue;
    // A chapter row carries the story span of the whole chapter, so it counts once the chapter has been read to its end (the end
    // of chapter N is the start of chapter N + 1, which has not been read). Inside a chapter its events carry the clock.
    if (row.kind === "chapter" && (row.revealedPosEnd ?? row.revealedPos) > pos) continue;
    if (row.storyFromLo == null || row.storyFromHi == null) continue;
    if (latest === null || row.storyFromLo > latest.storyFromLo! || (row.storyFromLo === latest.storyFromLo && row.storyFromHi > latest.storyFromHi!)) latest = row;
  }
  return latest?.storyFromHi ?? null;
}

/**
 * SpoilerGate.Visible. Spoilers off (the reader's clock): `revealedPos ≤ pos` and (`retractedPos` null or `> pos`). Spoilers on (the
 * story's clock): `storyFromLo ≤ story` and (`storyUntilLo` null or `> story`); the reveal position is ignored. An element without
 * the clock a mode needs is visible in that mode, and so is everything with Spoilers on while `story` is null.
 */
export function visible(el: Clocks, pos: number, spoilers: boolean, story: string | null): boolean {
  if (!spoilers) {
    if (el.revealedPos != null && el.revealedPos > pos) return false;
    return el.retractedPos == null || el.retractedPos > pos;
  }
  if (story === null) return true;
  if (el.storyFromLo != null && el.storyFromLo > story) return false;
  return el.storyUntilLo == null || el.storyUntilLo > story;
}

/**
 * What a chapter and the Spoilers switch let through, for the gate, the popover's rows and the character dropdown: `node` and `edge`
 * say whether an element passes on its own clocks (an edge is drawn only when its two ends pass too: the callers check that), `name`
 * is the display name. A book indexed before the full schema: the earlier rule (spoilers off: `pos` ≤ the chapter's end; spoilers on: everything)
 * and displayName. A full-schema book: `visible`, and identity: characters joined by IDENTICAL_TO edges that pass the rule (their ends
 * too) are one character for the name; while such an edge is hidden its two characters are unrelated.
 */
export type View = {
  pos: number;
  /** story(pos); null in a book without a timeline. */
  story: string | null;
  node: (n: GraphNode) => boolean;
  edge: (e: GraphEdge) => boolean;
  name: (n: GraphNode) => string | null;
};

export function viewAt(payload: GraphPayload, chapter: number, spoilers: boolean, ambiguous?: ReadonlySet<string>): View {
  const pos = chapterEnd(payload.chapters, chapter);
  if (!isTemporal(payload)) {
    const shown = (at: number | null) => spoilers || at == null || at <= pos;
    return { pos, story: null, node: (n) => shown(n.pos), edge: (e) => shown(e.pos), name: (n) => displayName(n, pos, spoilers, ambiguous) };
  }
  const story = storyAt(payload.timeline!, pos);
  const node = (n: GraphNode) => visible(n, pos, spoilers, story);
  const edge = (e: GraphEdge) => visible(edgeClocks(e), pos, spoilers, story);
  const unions = identityUnions(payload, node, edge);
  const names = new Map<number, string | null>(); // one name per union, worked out once
  const name = (n: GraphNode) => {
    const members = unions.get(n.id);
    if (!members) return displayName(n, pos, spoilers, ambiguous);
    const key = members[0].id;
    if (!names.has(key)) names.set(key, unionDisplayName(members, pos, spoilers, ambiguous));
    return names.get(key)!;
  };
  return { pos, story, node, edge, name };
}

/**
 * The characters joined by IDENTICAL_TO edges that pass the rule with both their ends: node id → its union's members
 * (in id order), for unions of two or more. The nodes are never merged; this only decides the name they share.
 */
function identityUnions(payload: GraphPayload, node: (n: GraphNode) => boolean, edge: (e: GraphEdge) => boolean): Map<number, GraphNode[]> {
  const out = new Map<number, GraphNode[]>();
  const links = payload.edges.filter((e) => e.type === "IDENTICAL_TO" && e.source !== e.target && edge(e));
  if (links.length === 0) return out;
  const byId = new Map(payload.nodes.map((n) => [n.id, n]));
  const next = new Map<number, number[]>();
  for (const e of links) {
    const a = byId.get(e.source);
    const b = byId.get(e.target);
    if (!a || !b || !node(a) || !node(b)) continue;
    next.set(a.id, [...(next.get(a.id) ?? []), b.id]);
    next.set(b.id, [...(next.get(b.id) ?? []), a.id]);
  }
  for (const start of next.keys()) {
    if (out.has(start)) continue;
    const seen = new Set([start]);
    const queue = [start];
    while (queue.length > 0)
      for (const id of next.get(queue.pop()!) ?? [])
        if (!seen.has(id)) {
          seen.add(id);
          queue.push(id);
        }
    const members = [...seen].sort((a, b) => a - b).map((id) => byId.get(id)!);
    for (const m of members) out.set(m.id, members);
  }
  return out;
}

/**
 * The one name a union of identical characters is shown under: displayName's rule taken over the union. The aliases are
 * all the members' aliases, so with spoilers off it is the alias the reader saw first among them; a roster spelling, then a short
 * name, of any member wins once the text has used it (when two members have one, the one used first); the canonical name is that of
 * the member the reader met first.
 */
function unionDisplayName(members: readonly GraphNode[], pos: number, spoilers: boolean, ambiguous?: ReadonlySet<string>): string | null {
  const aliases = members.flatMap((m) => m.aliasFirstPos);
  const met = (m: GraphNode) => Math.min(m.pos ?? Infinity, ...m.aliasFirstPos.map((a) => a.pos));
  const lead = [...members].sort((a, b) => met(a) - met(b) || a.id - b.id)[0];
  const pick = (of: (m: GraphNode) => string | null | undefined, same: (alias: string, name: string) => boolean): string | null => {
    let best: string | null = null;
    let bestAt = Infinity;
    for (const m of members) {
      const candidate = of(m);
      if (!candidate) continue;
      const at = Math.min(Infinity, ...aliases.filter((a) => same(a.alias, candidate)).map((a) => a.pos));
      if (!(spoilers || at <= pos)) continue;
      if (best === null || at < bestAt) [best, bestAt] = [candidate, at];
    }
    return best;
  };
  return displayName(
    { ...lead, rosterName: pick((m) => m.rosterName, sameName), shortName: pick((m) => m.shortName, (a, b) => a === b), aliasFirstPos: aliases },
    pos,
    spoilers,
    ambiguous,
  );
}

export type Highlight = { nodes: ReadonlySet<number>; edges: ReadonlySet<number> };
export const noHighlight: Highlight = { nodes: new Set(), edges: new Set() };

/** A node the gate keeps, with its display name at the gate's chapter. */
export type VisibleNode = GraphNode & { display: string };
export type Gated = { nodes: VisibleNode[]; edges: GraphEdge[] };

/** `ambiguous`: the book's shared aliases (ambiguousAliases), for display names. */
export type GateOptions = { chapter: number; spoilers: boolean; facts: boolean; highlight: Highlight; ambiguous?: ReadonlySet<string> };

/**
 * The mockup's `visible()`: with spoilers off a node or edge counts only when its pos is ≤ the chapter's end, and a node only
 * when it has a display name (no alias mentioned yet → not drawn). Characters and Locations always; Facts and Events with the
 * "Show facts & events" toggle or when highlighted. Edges between kept nodes; a fact or event left without an edge is dropped
 * unless highlighted.
 * In a book with a timeline, "counts" is the spoiler rule in both modes (viewAt): an element passes on its own clocks,
 * and an edge is drawn only when it passes and both its ends do. Facts and Events hang on PARTICIPATED_IN, DISCOVERED and PERCEIVES
 * edges there (no ABOUT), and an IDENTICAL_TO edge that passes stays an edge between its two nodes, which then share a name.
 */
export function gate(payload: GraphPayload, o: GateOptions): Gated {
  const view = viewAt(payload, o.chapter, o.spoilers, o.ambiguous);
  const keep = new Map<number, VisibleNode>();
  for (const n of payload.nodes) {
    if (!view.node(n)) continue;
    if (!(isEntityLabel(n.label) || o.facts || o.highlight.nodes.has(n.id))) continue;
    const display = view.name(n);
    if (display === null) continue;
    keep.set(n.id, { ...n, display });
  }
  const edges = payload.edges.filter((e) => view.edge(e) && keep.has(e.source) && keep.has(e.target));
  const touched = new Set<number>();
  for (const e of edges) {
    touched.add(e.source);
    touched.add(e.target);
  }
  for (const [id, n] of keep) if (!isEntityLabel(n.label) && !touched.has(id) && !o.highlight.nodes.has(id)) keep.delete(id);
  return { nodes: [...keep.values()], edges: edges.filter((e) => keep.has(e.source) && keep.has(e.target)) };
}

/**
 * Whether a re-gating changed the visible set by more than `threshold` (20 %): nodes added plus nodes removed, over the
 * previous count. The first set (nothing before) counts as a change.
 */
export function bigChange(prev: ReadonlySet<number>, next: ReadonlySet<number>, threshold = 0.2): boolean {
  if (prev.size === 0) return next.size > 0;
  let diff = 0;
  for (const id of next) if (!prev.has(id)) diff++;
  for (const id of prev) if (!next.has(id)) diff++;
  return diff / prev.size > threshold;
}

/**
 * The largest connected component of the visible graph (by node count), from the visible edges. Components smaller than
 * `minSize` (isolated nodes, pairs) don't count; none big enough → null. Ties: the component found first (the gate's node order).
 */
export function largestComponent(gated: Gated, minSize = 3): Set<number> | null {
  const parent = new Map<number, number>(gated.nodes.map((n) => [n.id, n.id]));
  const find = (x: number): number => {
    let r = x;
    while (parent.get(r) !== r) r = parent.get(r)!;
    for (let y = x; parent.get(y) !== r;) {
      const next = parent.get(y)!;
      parent.set(y, r);
      y = next;
    }
    return r;
  };
  for (const e of gated.edges) {
    if (!parent.has(e.source) || !parent.has(e.target)) continue;
    const a = find(e.source);
    const b = find(e.target);
    if (a !== b) parent.set(b, a);
  }
  const groups = new Map<number, Set<number>>();
  for (const n of gated.nodes) {
    const root = find(n.id);
    const g = groups.get(root) ?? new Set<number>();
    g.add(n.id);
    groups.set(root, g);
  }
  let best: Set<number> | null = null;
  for (const g of groups.values()) if (g.size >= minSize && (!best || g.size > best.size)) best = g;
  return best;
}

/**
 * The automatic fit's target: the largest connected component whenever it has at least 3 nodes (an earlier "at least half the
 * visible nodes" rule fell back to Fit with facts off from ch. 3 on); null → Fit (graphs with no group of 3, e.g. very early chapters).
 */
export function autoFitTarget(gated: Gated): Set<number> | null {
  return largestComponent(gated);
}

/** The 2D tab's zoom range. The floor was 0.3, too high for ch. 15 with facts on (Fit could not frame it). */
export const GRAPH_MIN_ZOOM = 0.05;
export const GRAPH_MAX_ZOOM = 4;

/** The zoom slider's 0–100 position ↔ zoom, on a log scale between `min` and `max`. */
export const zoomToSlider = (k: number, min: number, max: number) => Math.round(Math.max(0, Math.min(100, (100 * Math.log(k / min)) / Math.log(max / min))));
export const sliderToZoom = (v: number, min: number, max: number) => min * Math.pow(max / min, v / 100);

export type NodeRow = { id: number; text: string; detail: string };

/**
 * A node's revealed rows for the selection popover (the mockup's right rail, compact): people and places it is linked to
 * ("name · TYPE · ch. N"), then the facts and events it is part of ("text · ch. N"), by reveal; neighbours without a display
 * name are left out. At most `max`, with the total.
 * The same spoiler rule as the gate (viewAt). A PERCEIVES edge reads "kind · target" on the perceiver's card and
 * "perceiver · kind" on the target's. A Fact has no ABOUT edges: its subjects come from `subject` ("name · about"), and a character
 * or place lists the facts about it the same way, among its facts.
 */
export function nodeRows(
  payload: GraphPayload,
  nodeId: number,
  chapter: number,
  spoilers: boolean,
  max = 8,
  ambiguous?: ReadonlySet<string>,
): { rows: NodeRow[]; total: number } {
  const view = viewAt(payload, chapter, spoilers, ambiguous);
  const byId = new Map(payload.nodes.map((n) => [n.id, n]));
  const entities: (NodeRow & { at: number })[] = [];
  const facts: (NodeRow & { at: number })[] = [];
  const chapterText = (at: number | null | undefined) => {
    const ch = chapterOf(payload.chapters, at ?? null);
    return ch ? `ch. ${ch}` : "—";
  };
  for (const e of payload.edges) {
    if (e.source !== nodeId && e.target !== nodeId) continue;
    if (!view.edge(e)) continue;
    const other = byId.get(e.source === nodeId ? e.target : e.source);
    if (!other || !view.node(other)) continue;
    const name = view.name(other);
    if (name === null) continue;
    const perceives = e.type === "PERCEIVES" && !!e.kind;
    const row = {
      id: other.id,
      text: perceives && e.source === nodeId ? `${e.kind} · ${name}` : name,
      detail: (perceives && e.source !== nodeId ? `${e.kind} · ` : isEntityLabel(other.label) ? `${e.type} · ` : "") + chapterText(e.pos),
      at: e.pos ?? 0,
    };
    (isEntityLabel(other.label) ? entities : facts).push(row);
  }
  // A Fact's subjects are a property of the Fact node, read from both sides (the Fact's card, and its subject's).
  const self = byId.get(nodeId);
  if (self && view.node(self)) {
    const about = (fact: GraphNode, shown: GraphNode) => {
      if (!view.node(fact) || !view.node(shown)) return;
      const name = view.name(shown);
      if (name === null) return;
      const at = fact.revealedPos ?? fact.pos;
      (isEntityLabel(shown.label) ? entities : facts).push({ id: shown.id, text: name, detail: (shown === fact ? "" : "about · ") + chapterText(at), at: at ?? 0 });
    };
    if (self.label === "Fact") {
      for (const id of self.subject ?? []) if (byId.has(id)) about(self, byId.get(id)!);
    } else {
      for (const n of payload.nodes) if (n.label === "Fact" && n.subject?.includes(nodeId)) about(n, n);
    }
  }
  const all = [...entities.sort((a, b) => a.at - b.at), ...facts.sort((a, b) => a.at - b.at)];
  return { rows: all.slice(0, max).map(({ id, text, detail }) => ({ id, text, detail })), total: all.length };
}

/** A result column carrying ids (…Id / …Ids, edgeId / edgeIds included), hidden in the Graph Query grid unless "Show ids". */
export function isIdColumn(name: string): boolean {
  return /[a-z0-9]Ids?$/.test(name) || name === "Id" || name === "Ids";
}

/**
 * The graph highlight of a Graph Query result, as a chat lookup's: the ids in its id columns, edgeId/edgeIds as edges and
 * every other …Id/…Ids column as nodes. Values come rendered as text ("12", lists "6, 7"); anything not an integer is skipped.
 */
export function resultHighlight(columns: readonly string[], rows: readonly (readonly (string | null)[])[]): { nodeIds: number[]; edgeIds: number[] } {
  const nodeIds = new Set<number>();
  const edgeIds = new Set<number>();
  columns.forEach((c, j) => {
    if (!isIdColumn(c)) return;
    const into = /^edgeIds?$/.test(c) ? edgeIds : nodeIds;
    for (const r of rows)
      for (const part of (r[j] ?? "").split(/[^0-9-]+/))
        if (/^-?\d+$/.test(part)) into.add(Number(part));
  });
  return { nodeIds: [...nodeIds], edgeIds: [...edgeIds] };
}

export type HighlightAction = { type: "answer"; toolCalls: { nodeIds: number[]; edgeIds: number[] }[] } | { type: "clear" };

/** The highlight set: the last answer's tool-call ids (a new answer replaces it); "Clear" empties it. */
export function highlightReducer(_state: Highlight, action: HighlightAction): Highlight {
  if (action.type === "clear") return noHighlight;
  return { nodes: new Set(action.toolCalls.flatMap((c) => c.nodeIds)), edges: new Set(action.toolCalls.flatMap((c) => c.edgeIds)) };
}

const TITLES = new Set(["mr", "mrs", "miss", "ms", "dr", "sir", "lady", "lord", "the", "a", "an", "rear-admiral", "monsieur", "madame", "m", "mme", "mlle", "mademoiselle", "captain", "capt", "cap’n", "cap'n", "squire", "master", "doctor", "cardinal"]);
const PARTICLES = new Set(["de", "of", "la", "le", "du", "des", "von", "van"]);

/**
 * Initials for a Character disc, as the roster's avatars (ChatCharacters.DeriveInitials): the first letters of the first and last
 * words without titles and particles ("Mr. Sherlock Holmes" → "SH", "Dr. Watson" → "W", "M. de Tréville" → "T"); a name of titles
 * only → its first letter.
 */
export function nodeInitials(name: string): string {
  const words = name
    .split(" ")
    .map((w) => w.replace(/^[.,'"]+|[.,'"]+$/g, ""))
    .filter((w) => w.length > 0);
  let rest = words.filter((w) => !TITLES.has(w.toLowerCase()) && !PARTICLES.has(w.toLowerCase()));
  if (rest.length === 0) rest = words.filter((w) => !["the", "a", "an"].includes(w.toLowerCase()));
  if (rest.length === 0) rest = words;
  if (rest.length === 0) return "?";
  const first = rest[0][0].toUpperCase();
  return rest.length === 1 ? first : first + rest[rest.length - 1][0].toUpperCase();
}

/** Each node's number of edges in the whole graph (the Character disc's size, as in the mockup). */
export function degrees(payload: GraphPayload): Map<number, number> {
  const d = new Map<number, number>();
  for (const e of payload.edges) {
    d.set(e.source, (d.get(e.source) ?? 0) + 1);
    d.set(e.target, (d.get(e.target) ?? 0) + 1);
  }
  return d;
}

/** The mockup's radius by label: Character 9 + √degree·1.8 (≤ 23), Location 7 + √degree (≤ 15), Event 4.5, Fact 3.2 (the half side of its square). */
export function nodeRadius(label: string, degree: number): number {
  if (label === "Character") return 9 + Math.min(14, Math.sqrt(degree || 1) * 1.8);
  if (label === "Location") return 7 + Math.min(8, Math.sqrt(degree || 1));
  return label === "Event" ? 4.5 : 3.2;
}

/**
 * The neighbours the 3D fly-to frames with the selected node: its people and places (Characters and Locations) with the
 * most edges in the whole graph, at most `cap` (ties by id); facts and events are left out, so a hub's hundreds of them don't push the
 * camera back to the whole graph.
 */
export function flyTargets(
  neighbours: Iterable<number>,
  labelOf: (id: number) => string | undefined,
  degree: ReadonlyMap<number, number>,
  cap = 15,
): number[] {
  return [...neighbours]
    .filter((id) => {
      const l = labelOf(id);
      return l !== undefined && isEntityLabel(l);
    })
    .sort((a, b) => (degree.get(b) ?? 0) - (degree.get(a) ?? 0) || a - b)
    .slice(0, cap);
}

type P3 = { x: number; y: number; z: number };

/**
 * Where the 3D camera goes to show a selected node with its fly-to neighbours (flyTargets): centred on the node itself, the
 * frame's radius the median of their distances from it (plus the node's radius; at least 30), so the nearer half is in view. Framing all
 * of them barely moved in on a hub: with facts on, d'Artagnan's best-connected neighbours spread as far as the rest of the
 * graph. From the side the camera is already on, with 15 % to spare.
 */
export function frameAround(node: P3 & { r: number }, neighbours: P3[], camera: P3, fovDegrees: number): { position: P3; lookAt: P3 } {
  const ds = neighbours.map((p) => Math.hypot(p.x - node.x, p.y - node.y, p.z - node.z)).sort((a, b) => a - b);
  const median = ds.length === 0 ? 0 : ds.length % 2 ? ds[(ds.length - 1) / 2] : (ds[ds.length / 2 - 1] + ds[ds.length / 2]) / 2;
  const radius = Math.max(30, median + node.r);
  let d = { x: camera.x - node.x, y: camera.y - node.y, z: camera.z - node.z };
  const len = Math.hypot(d.x, d.y, d.z);
  d = len < 1e-6 ? { x: 0, y: 0, z: 1 } : { x: d.x / len, y: d.y / len, z: d.z / len };
  const dist = (radius / Math.sin((fovDegrees * Math.PI) / 360)) * 1.15;
  const c = { x: node.x, y: node.y, z: node.z };
  return { position: { x: c.x + d.x * dist, y: c.y + d.y * dist, z: c.z + d.z * dist }, lookAt: c };
}

// ---- The timeline slider's ticks and axis -----------------------------------------------------------------------

/**
 * A mark on the slider's track, `at` 0–1 along it. Chapter ticks sit on the slider's stops. `shown`: the gate lets the tick's event
 * through at the slider's chapter (a chapter tick: the chapter is reached); `label` (the event's summary, the chapter's title) is
 * null until then, so a tick's tooltip never tells what the graph is still hiding. `referenced`: an Event of kind "referenced"
 * (drawn hollow; narrated ones filled).
 */
export type TimelineTick = { key: string; kind: "chapter" | "event"; refId: number | null; at: number; referenced: boolean; shown: boolean; label: string | null };
export type TimelineLayout = {
  /** "text": the track is the book's text, by offset (Spoilers off). "story": it is story time (Spoilers on). */
  axis: "text" | "story";
  ticks: TimelineTick[];
  /** The slider's stops, one per chapter, in chapter order. */
  stops: { chapter: number; at: number }[];
  /** Where the thumb sits: the selected chapter's stop. */
  thumb: number;
  /** story(pos) at the selected chapter (ISO), on either axis; null when the timeline has no date by then. */
  story: string | null;
  /** The anchor row's note: the one assumed date, which the slider shows labelled as an assumption. */
  note: string | null;
};

const dayOf = (iso: string | null | undefined): number | null => {
  const t = iso ? Date.parse(iso.slice(0, 10)) : NaN;
  return Number.isNaN(t) ? null : Math.round(t / 864e5);
};

/**
 * The story-time axis: ISO date → 0–1, over every date in `dates`, earliest to latest and in order. It is linear in days, except that
 * a gap between two neighbouring dates longer than a twentieth of the narrated span (`narrated`: the chapters' dates; at least a day)
 * is drawn at that length. Without it one old date would take the whole track: The Hound's manuscript is dated 1742 and its chapters
 * all fall within a few weeks of 1889, which would then share the last pixel.
 */
export function storyScale(dates: readonly (string | null | undefined)[], narrated: readonly (string | null | undefined)[]): (iso: string | null | undefined) => number {
  const days = [...new Set(dates.map(dayOf).filter((d): d is number => d !== null))].sort((a, b) => a - b);
  const told = narrated.map(dayOf).filter((d): d is number => d !== null);
  const cap = Math.max(1, told.length > 0 ? (Math.max(...told) - Math.min(...told)) / 20 : 0);
  const x = [0];
  for (let i = 1; i < days.length; i++) x.push(x[i - 1] + Math.min(days[i] - days[i - 1], cap));
  const total = x[x.length - 1] || 1;
  return (iso) => {
    const d = dayOf(iso);
    if (d === null || days.length === 0 || d <= days[0]) return 0;
    if (d >= days[days.length - 1]) return days.length > 1 ? 1 : 0;
    let i = 1;
    while (days[i] < d) i++;
    return (x[i - 1] + ((d - days[i - 1]) / (days[i] - days[i - 1])) * (x[i] - x[i - 1])) / total;
  };
}

/**
 * The slider of a full-schema book (isTemporal), from its timeline: a tick per chapter row and per event row, and a stop per chapter (the
 * chat and the Graph Query tab take a chapter, so events are ticks, not stops). Spoilers off: the track is the text, a chapter's stop
 * at its end offset (the gate's `pos`) and an event's tick at its `revealedPos`. Spoilers on: the track is story time (storyScale), a
 * chapter's stop at story(pos) of its end and an event's tick at its `storyFromLo`. On both, a tick at or left of the thumb is one the
 * rule has let in: the reader's position on the left, what is still to come on the right.
 */
export function timelineLayout(payload: GraphPayload, chapter: number, spoilers: boolean): TimelineLayout {
  const timeline = payload.timeline ?? [];
  const rows = timeline.filter((r) => r.kind === "chapter" || r.kind === "event");
  const pos = chapterEnd(payload.chapters, chapter);
  const story = storyAt(timeline, pos);
  const byId = new Map(payload.nodes.map((n) => [n.id, n]));
  const titles = new Map(rows.filter((r) => r.kind === "chapter" && r.refId != null).map((r) => [r.refId!, r.label]));
  const length = Math.max(1, ...payload.chapters.map((c) => c.charEnd), ...rows.map((r) => r.revealedPos ?? 0));
  const scale = spoilers
    ? storyScale(
        timeline.flatMap((r) => [r.storyFromLo, r.storyFromHi]),
        rows.filter((r) => r.kind === "chapter").flatMap((r) => [r.storyFromLo, r.storyFromHi]),
      )
    : null;
  const stops = payload.chapters.map((c) => ({ chapter: c.number, at: scale ? scale(storyAt(timeline, c.charEnd)) : c.charEnd / length }));
  const ticks: TimelineTick[] = stops.map((s) => {
    const shown = s.chapter <= chapter;
    return { key: `c${s.chapter}`, kind: "chapter", refId: s.chapter, at: s.at, referenced: false, shown, label: shown ? (titles.get(s.chapter) ?? null) : null };
  });
  rows.forEach((r, i) => {
    if (r.kind !== "event" || (scale ? r.storyFromLo : r.revealedPos) == null) return;
    const event = r.refId == null ? undefined : byId.get(r.refId);
    const shown = visible(event ?? r, pos, spoilers, story);
    ticks.push({
      key: `e${r.refId ?? `row${i}`}`,
      kind: "event",
      refId: r.refId,
      at: scale ? scale(r.storyFromLo) : r.revealedPos! / length,
      referenced: event?.kind === "referenced",
      shown,
      label: shown ? r.label : null,
    });
  });
  return {
    axis: spoilers ? "story" : "text",
    ticks,
    stops,
    thumb: stops.find((s) => s.chapter === chapter)?.at ?? stops[stops.length - 1]?.at ?? 0,
    story,
    note: timeline.find((r) => r.kind === "anchor")?.note ?? null,
  };
}

/**
 * The stop a pointer at `at` (0–1 along the track) picks: the nearest; among stops at the same place (chapters in which story time
 * stands still), the one nearest the current chapter.
 */
export function nearestStop(stops: readonly { chapter: number; at: number }[], at: number, current: number): number {
  let best = current;
  let bestD = Infinity;
  for (const s of stops) {
    const d = Math.abs(s.at - at);
    if (d < bestD - 1e-9 || (Math.abs(d - bestD) <= 1e-9 && Math.abs(s.chapter - current) < Math.abs(best - current))) [best, bestD] = [s.chapter, d];
  }
  return best;
}

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
/** An ISO date as the slider shows it: "1889-10-16" → "16 Oct 1889"; anything else unchanged. */
export function formatStoryDate(iso: string): string {
  const m = /^(-?\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m && MONTHS[Number(m[2]) - 1] ? `${Number(m[3])} ${MONTHS[Number(m[2]) - 1]} ${m[1]}` : iso;
}

/** HTML-escaped text for the library's HTML tooltip. */
export function escapeHtml(s: string): string {
  return s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
}
