import { describe, expect, it } from "vitest";
import {
  aliasWords,
  ambiguousAliases,
  autoFitTarget,
  bigChange,
  chapterOf,
  displayName,
  flyTargets,
  frameAround,
  isIdColumn,
  resultHighlight,
  sameName,
  gate,
  highlightReducer,
  largestComponent,
  nodeInitials,
  nodeRows,
  noHighlight,
  sliderToZoom,
  zoomToSlider,
  chapterEnd,
  edgeClocks,
  formatStoryDate,
  graphQuery,
  isTemporal,
  nearestStop,
  storyAt,
  storyScale,
  timelineLayout,
  viewAt,
  visible,
  type TimelineRow,
  type Gated,
  type GraphEdge,
  type GraphNode,
  type GraphPayload,
  type VisibleNode,
} from "./model.ts";

const entity = (id: number, label: string, name: string, ...aliases: [string, number][]): GraphNode => ({
  id,
  label,
  name,
  text: null,
  pos: aliases.length ? Math.min(...aliases.map((a) => a[1])) : null,
  aliasFirstPos: aliases.map(([alias, pos]) => ({ alias, pos })),
});
const fact = (id: number, text: string, pos: number): GraphNode => ({ id, label: "Fact", name: null, text, pos, aliasFirstPos: [] });

// The late-name fixture (ChatTests): "the lady" (100), "Miss Stapleton" (5000), "Beryl Garcia" (90000).
const lady = entity(1, "Character", "Beryl Garcia", ["the lady", 100], ["Miss Stapleton", 5000], ["Beryl Garcia", 90000]);

describe("displayName (the chat tools' rule)", () => {
  it.each([
    [2000, false, "the lady"],
    [20000, false, "the lady"],
    [2000, true, "Beryl Garcia"],
  ])("pos %d, spoilers %s → %s", (pos, spoilers, expected) => expect(displayName(lady, pos, spoilers)).toBe(expected));

  it("uses only aliases mentioned by pos, ties by length, and hides a node no alias of which is mentioned yet", () => {
    const later = entity(1, "Character", "Beryl Garcia", ["the lady", 30000], ["Miss Stapleton", 5000], ["Beryl Garcia", 90000]);
    expect(displayName(later, 20000, false)).toBe("Miss Stapleton");
    expect(displayName(later, 89999, false)).toBe("Miss Stapleton");
    expect(displayName(lady, 50, false)).toBeNull(); // not mentioned by any name: not drawn
    expect(displayName(entity(2, "Character", "X", ["Stapleton", 50], ["Mr. Stapleton", 50]), 2000, false)).toBe("Mr. Stapleton");
    expect(displayName(entity(3, "Character", "Sherlock Holmes"), 0, false)).toBe("Sherlock Holmes"); // no aliasFirstPos: its name
    expect(displayName(fact(4, "A fact.", 10), 0, false)).toBe("A fact.");
  });

  it("shows a character's short name once the reader has met it, and always with spoilers on", () => {
    // Athos: "Athos" first at 500, "Monsieur Athos" (the canonical name) at 900.
    const athos = { ...entity(5, "Character", "Monsieur Athos", ["Athos", 500], ["Monsieur Athos", 900]), shortName: "Athos" };
    expect(displayName(athos, 1000, false)).toBe("Athos");
    expect(displayName(athos, 1000, true)).toBe("Athos");
    // Short name not yet mentioned: the usual rule (earliest alias), never the later short name.
    const late = { ...entity(6, "Character", "Beryl Garcia", ["the lady", 100], ["Beryl", 9000]), shortName: "Beryl" };
    expect(displayName(late, 2000, false)).toBe("the lady");
    expect(displayName(late, 2000, true)).toBe("Beryl");
  });
});

// Chapter 1 is [0, 1000), chapter 2 [1000, 3000).
const payload: GraphPayload = {
  nodes: [
    entity(1, "Character", "Stapleton", ["Stapleton", 50]),
    entity(2, "Character", "Beryl Garcia", ["Beryl", 1500]),
    entity(3, "Location", "Merripit House", ["Merripit House", 200]),
    fact(10, "She lives with her brother.", 150),
    fact(11, "A later fact.", 2500),
    fact(12, "An isolated fact.", 300),
  ],
  edges: [
    { id: 20, source: 10, target: 1, type: "ABOUT", pos: 150 },
    { id: 21, source: 10, target: 2, type: "ABOUT", pos: 150 },
    { id: 22, source: 1, target: 3, type: "LOCATED_AT", pos: 210 },
    { id: 23, source: 1, target: 2, type: "RELATED_TO", pos: 1600 },
    { id: 24, source: 11, target: 1, type: "ABOUT", pos: 2500 },
  ],
  chapters: [
    { number: 1, charEnd: 1000 },
    { number: 2, charEnd: 3000 },
  ],
};
const ids = (g: { nodes: { id: number }[] }) => g.nodes.map((n) => n.id).sort((a, b) => a - b);

describe("gate", () => {
  it("keeps entities revealed by the chapter, with display names; facts only with the toggle", () => {
    const ch1 = gate(payload, { chapter: 1, spoilers: false, facts: false, highlight: noHighlight });
    expect(ids(ch1)).toEqual([1, 3]); // Beryl (1500) not yet; facts off
    expect(ch1.edges.map((e) => e.id)).toEqual([22]);
    const ch1Facts = gate(payload, { chapter: 1, spoilers: false, facts: true, highlight: noHighlight });
    expect(ids(ch1Facts)).toEqual([1, 3, 10]); // fact 12 has no edge: dropped; fact 11 is later
    expect(ch1Facts.edges.map((e) => e.id)).toEqual([20, 22]); // edge 21 goes to a node not drawn yet
  });

  it("re-gates by chapter and spoilers", () => {
    const ch2 = gate(payload, { chapter: 2, spoilers: false, facts: true, highlight: noHighlight });
    expect(ids(ch2)).toEqual([1, 2, 3, 10, 11]);
    expect(ch2.nodes.find((n) => n.id === 2)!.display).toBe("Beryl");
    const on = gate(payload, { chapter: 1, spoilers: true, facts: true, highlight: noHighlight });
    expect(ids(on)).toEqual([1, 2, 3, 10, 11]);
    expect(on.nodes.find((n) => n.id === 2)!.display).toBe("Beryl Garcia");
  });

  it("shows a highlighted fact with facts off, even without an edge", () => {
    const g = gate(payload, { chapter: 1, spoilers: false, facts: false, highlight: { nodes: new Set([10, 12]), edges: new Set([20]) } });
    expect(ids(g)).toEqual([1, 3, 10, 12]);
  });
});

describe("nodeRows", () => {
  it("lists linked people and places, then facts, revealed by the chapter, with display names", () => {
    expect(nodeRows(payload, 1, 1, false)).toEqual({
      rows: [
        { id: 3, text: "Merripit House", detail: "LOCATED_AT · ch. 1" },
        { id: 10, text: "She lives with her brother.", detail: "ch. 1" },
      ],
      total: 2,
    });
    const ch2 = nodeRows(payload, 1, 2, false, 2);
    expect(ch2.total).toBe(4);
    expect(ch2.rows.map((r) => r.text)).toEqual(["Merripit House", "Beryl"]);
  });
});

describe("highlightReducer", () => {
  it("keeps the last answer's ids and clears", () => {
    const one = highlightReducer(noHighlight, {
      type: "answer",
      toolCalls: [
        { nodeIds: [1, 10], edgeIds: [20] },
        { nodeIds: [3], edgeIds: [] },
      ],
    });
    expect([...one.nodes]).toEqual([1, 10, 3]);
    expect([...one.edges]).toEqual([20]);
    const two = highlightReducer(one, { type: "answer", toolCalls: [{ nodeIds: [2], edgeIds: [23] }] });
    expect([...two.nodes]).toEqual([2]); // replaced, not merged
    expect(highlightReducer(two, { type: "clear" }).nodes.size).toBe(0);
    expect(highlightReducer(two, { type: "answer", toolCalls: [] }).nodes.size).toBe(0);
  });
});

describe("helpers", () => {
  it("initials and chapters", () => {
    expect(nodeInitials("Mr. Sherlock Holmes")).toBe("SH");
    expect(nodeInitials("Dr. Watson")).toBe("W");
    // The roster's rule (ChatCharacters.DeriveInitials): first and last word, titles and particles dropped.
    expect(nodeInitials("Sir Henry Baskerville")).toBe("HB");
    expect(nodeInitials("Mme. Bonacieux")).toBe("B");
    expect(nodeInitials("Monsieur Athos")).toBe("A");
    expect(nodeInitials("M. de Tréville")).toBe("T");
    expect(nodeInitials("Anne of Austria")).toBe("AA");
    expect(nodeInitials("D’Artagnan")).toBe("D");
    expect(nodeInitials("the lady")).toBe("L");
    expect(nodeInitials("Captain Smollett")).toBe("S");
    expect(nodeInitials("Cap’n Silver")).toBe("S");
    expect(chapterOf(payload.chapters, 999)).toBe(1);
    expect(chapterOf(payload.chapters, 1000)).toBe(2);
    expect(chapterOf(payload.chapters, null)).toBeNull();
  });
});

describe("ambiguous aliases", () => {
  const henry = entity(1, "Character", "Sir Henry Baskerville", ["Baskerville", 100], ["Mr. Henry Baskerville", 300], ["Sir Henry Baskerville", 5000]);
  const charles = entity(2, "Character", "Sir Charles Baskerville", ["Sir Charles Baskerville", 50], ["Sir Charles", 60]);
  const hugo = entity(3, "Character", "Hugo Baskerville", ["Baskerville", 20], ["Hugo", 40]);
  const book: GraphPayload = { nodes: [henry, charles, hugo, { ...lady, id: 9 }], edges: [], chapters: [{ number: 1, charEnd: 10000 }] };

  it("finds aliases shared with another entity (the same alias, or its words inside another entity's alias)", () => {
    expect([...ambiguousAliases(book)]).toEqual(["Baskerville"]);
    expect(aliasWords("Baskerville’s hound")).toEqual(["baskerville", "s", "hound"]);
  });

  it("skips them unless nothing else is revealed, like the chat tools", () => {
    const amb = ambiguousAliases(book);
    expect(displayName(henry, 200, false, amb)).toBe("Baskerville"); // the only one revealed
    expect(displayName(henry, 300, false, amb)).toBe("Mr. Henry Baskerville");
    expect(displayName(hugo, 300, false, amb)).toBe("Hugo");
    expect(displayName(henry, 300, false)).toBe("Baskerville"); // without the set: the first-mention rule
    expect(displayName(lady, 20000, false, amb)).toBe("the lady"); // the late-name fixture is unchanged
    const g = gate(book, { chapter: 1, spoilers: false, facts: false, highlight: noHighlight, ambiguous: amb });
    expect(g.nodes.find((n) => n.id === 1)!.display).toBe("Mr. Henry Baskerville");
  });
});

describe("bigChange (re-fit after a re-gating that changes the visible set by more than 20 %)", () => {
  it("counts nodes added and removed over the previous count", () => {
    const ten = new Set([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    expect(bigChange(new Set(), ten)).toBe(true); // the first layout
    expect(bigChange(ten, new Set([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]))).toBe(false); // 2 of 10: 20 %, not more
    expect(bigChange(ten, new Set([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]))).toBe(true); // 30 %
    expect(bigChange(ten, new Set([1, 2, 3, 4, 5, 6, 7, 8, 11]))).toBe(true); // 2 removed + 1 added
  });
});

describe("largestComponent / autoFitTarget", () => {
  const node = (id: number): VisibleNode => ({ ...entity(id, "Character", `N${id}`, [`N${id}`, 1]), display: `N${id}` });
  const edge = (id: number, source: number, target: number): GraphEdge => ({ id, source, target, type: "MET", pos: 1 });
  it("finds the biggest component from the visible edges, ignoring strays and tiny groups", () => {
    // 1-2-3-4 (4 nodes), 5-6-7 (3), 8-9 (pair), 10 (isolated)
    const g: Gated = {
      nodes: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(node),
      edges: [edge(1, 1, 2), edge(2, 2, 3), edge(3, 3, 4), edge(4, 5, 6), edge(5, 6, 7), edge(6, 8, 9)],
    };
    expect([...largestComponent(g)!].sort((a, b) => a - b)).toEqual([1, 2, 3, 4]);
    expect(largestComponent({ nodes: [1, 2, 3].map(node), edges: [edge(1, 1, 2)] })).toBeNull(); // only a pair and a stray
    expect(largestComponent({ nodes: [1, 2, 3].map(node), edges: [edge(1, 1, 2)] }, 2)!.size).toBe(2);
    // The auto-fit focuses whenever the largest group has at least 3 nodes, even 4 of 10; no such group → Fit (null).
    expect([...autoFitTarget(g)!].sort((a, b) => a - b)).toEqual([1, 2, 3, 4]);
    expect(autoFitTarget({ nodes: [1, 2, 3].map(node), edges: [edge(1, 1, 2)] })).toBeNull();
  });

  it("maps the zoom slider on a log scale", () => {
    expect(zoomToSlider(0.05, 0.05, 4)).toBe(0);
    expect(zoomToSlider(4, 0.05, 4)).toBe(100);
    expect(sliderToZoom(50, 0.05, 4)).toBeCloseTo(Math.sqrt(0.05 * 4), 6);
    expect(zoomToSlider(sliderToZoom(37, 0.05, 4), 0.05, 4)).toBe(37);
    expect(zoomToSlider(99, 0.05, 4)).toBe(100); // clamped
  });
});

describe("roster spelling", () => {
  // Jim: "Jim" first at 100, "Jim Hawkins" at 5000; short name "Jim", roster "Jim Hawkins".
  const jim = { ...entity(10, "Character", "Jim Hawkins", ["Jim", 100], ["Jim Hawkins", 5000]), shortName: "Jim", rosterName: "Jim Hawkins" };
  it("wins once the text has used it, always with spoilers on", () => {
    expect(displayName(jim, 2000, false)).toBe("Jim");
    expect(displayName(jim, 5000, false)).toBe("Jim Hawkins");
    expect(displayName(jim, 2000, true)).toBe("Jim Hawkins");
  });
  it("matches ignoring case and the apostrophe's shape", () => {
    const dart = { ...entity(12, "Character", "M. d’Artagnan the younger", ["D’Artagnan", 50]), shortName: "D’Artagnan", rosterName: "d'Artagnan" };
    expect(displayName(dart, 100, false)).toBe("d'Artagnan");
    expect(sameName(" D’Artagnan", "d'artagnan")).toBe(true);
    expect(sameName("Athos", "Aramis")).toBe(false);
  });
});

describe("Graph Query results light the graph", () => {
  it("knows the id columns", () => {
    expect(["metId", "edgeIds", "factId", "aboutIds", "edgeId"].every(isIdColumn)).toBe(true);
    expect(["met", "revealed", "id", "paid", "Idaho"].some(isIdColumn)).toBe(false);
  });
  it("takes edge ids from edgeId/edgeIds and node ids from the other id columns", () => {
    const r = resultHighlight(
      ["met", "metId", "edgeIds", "aboutIds"],
      [
        ["Watson", "3", "6, 7", "1, 3"],
        ["Mortimer", "2", "8", null],
      ],
    );
    expect(r.nodeIds.sort((a, b) => a - b)).toEqual([1, 2, 3]);
    expect(r.edgeIds).toEqual([6, 7, 8]);
    expect(resultHighlight(["character", "facts"], [["Holmes", "12"]])).toEqual({ nodeIds: [], edgeIds: [] });
  });
});

describe("flyTargets (a hub is framed with its best-connected people and places)", () => {
  it("keeps entity neighbours only, by degree, at most the cap", () => {
    const labels = new Map<number, string>([
      [1, "Character"], [2, "Location"], [3, "Fact"], [4, "Event"], [5, "Character"], [6, "Character"],
    ]);
    const degree = new Map([[1, 5], [2, 9], [3, 50], [4, 40], [5, 9], [6, 1]]);
    expect(flyTargets([1, 2, 3, 4, 5, 6], (id) => labels.get(id), degree, 3)).toEqual([2, 5, 1]);
    expect(flyTargets([3, 4], (id) => labels.get(id), degree)).toEqual([]);
  });
});

describe("frameAround (a hub framed around itself, the nearer half of its neighbours in view)", () => {
  it("centres on the node, the radius the median neighbour distance plus the node's radius", () => {
    const node = { x: 0, y: 0, z: 0, r: 10 };
    const near = [{ x: 100, y: 0, z: 0 }, { x: 0, y: 200, z: 0 }, { x: 0, y: 0, z: 900 }];
    const { position, lookAt } = frameAround(node, near, { x: 0, y: 0, z: 1000 }, 60);
    expect(lookAt).toEqual({ x: 0, y: 0, z: 0 });
    // Median 200, + 10 = 210; sin(30°) = 0.5: 420 × 1.15 back along +z.
    expect(position.z).toBeCloseTo(483);
  });
  it("keeps a minimum frame without neighbours", () => {
    expect(frameAround({ x: 5, y: 5, z: 5, r: 3 }, [], { x: 5, y: 5, z: 5 }, 60).position.z).toBeCloseTo(5 + 69);
  });
});

// ---- The bi-temporal schema ------------------------------------------------------------------------------------------
// A small book on it. Chapter 1 is [0, 1000), chapter 2 [1000, 3000), chapter 3 [3000, 6000); the story runs 1–5 October 1889, with
// a legend of 1742 told in chapter 1. Stapleton and Vandeleur are one man, which the reader (and the story) learn in chapter 3.
const D1 = "1889-10-01";
const D2 = "1889-10-02";
const D5 = "1889-10-05";
const clocked = <T extends object>(el: T, revealedPos: number, storyFromLo: string, more: Partial<GraphNode> = {}) => ({ ...el, revealedPos, storyFromLo, ...more });
const event = (id: number, kind: string, text: string, revealedPos: number, storyFromLo: string): GraphNode =>
  clocked({ id, label: "Event", name: null, text, pos: revealedPos, aliasFirstPos: [], kind }, revealedPos, storyFromLo);
const link = (id: number, source: number, target: number, type: string, pos: number, storyFromLo: string, more: Partial<GraphEdge> = {}): GraphEdge => ({
  id,
  source,
  target,
  type,
  pos,
  storyFromLo,
  ...more,
});
const row = (kind: string, refId: number | null, label: string | null, revealedPos: number | null, lo: string | null, hi = lo, more: Partial<TimelineRow> = {}): TimelineRow => ({
  kind,
  refId,
  label,
  revealedPos,
  storyFromLo: lo,
  storyFromHi: hi,
  ...more,
});

const hound: GraphPayload = {
  nodes: [
    clocked(entity(1, "Character", "Stapleton", ["Stapleton", 300]), 300, D1),
    clocked(entity(2, "Character", "Vandeleur", ["Vandeleur", 2200]), 2200, "1880-01-01"), // the name he used years before
    clocked(entity(3, "Character", "Dr. Watson", ["Watson", 10]), 10, D1),
    clocked(entity(4, "Location", "Merripit House", ["Merripit House", 350]), 350, D1),
    event(20, "narrated", "Watson meets Stapleton on the moor.", 400, D1),
    event(21, "referenced", "Hugo Baskerville is killed by a hound.", 500, "1742-01-01"), // the legend, read out in chapter 1
    event(22, "narrated", "Watson sees a man on the tor.", 2000, D2),
    event(23, "narrated", "Holmes names Stapleton.", 5200, D5),
    event(24, "referenced", "Stapleton lets the hound loose.", 5500, D1), // told in chapter 3, happened on the first day
    clocked({ ...fact(30, "Stapleton is a naturalist.", 450), subject: [1] }, 450, D1),
    clocked({ ...fact(31, "The man on the tor is a stranger.", 2100), subject: [3] }, 2100, D2, { retractedPos: 5300 }), // shown false in chapter 3
    clocked({ ...fact(32, "Stapleton is Vandeleur.", 5200), subject: [1, 2] }, 5200, "1880-01-01"), // true long before it is told
  ],
  edges: [
    link(100, 3, 20, "PARTICIPATED_IN", 400, D1),
    link(101, 20, 30, "DISCOVERED", 450, D1, { kind: "witnessed" }),
    link(102, 3, 30, "PERCEIVES", 450, D1, { kind: "believes" }),
    link(103, 3, 1, "PERCEIVES", 600, D1, { kind: "trusts", retractedPos: 5200, storyUntilLo: D5 }), // closed when Holmes names him
    link(104, 3, 1, "PERCEIVES", 5200, D5, { kind: "distrusts" }),
    link(105, 1, 2, "IDENTICAL_TO", 5200, D5),
    link(106, 1, 4, "LOCATED_AT", 360, D1),
    link(107, 21, 20, "MENTIONED_IN", 500, D1),
    link(108, 3, 22, "PARTICIPATED_IN", 2000, D2),
    link(109, 22, 31, "DISCOVERED", 2100, D2, { kind: "inferred" }),
    link(110, 3, 31, "PERCEIVES", 2100, D2, { kind: "believes", retractedPos: 5300, storyUntilLo: D5 }),
    link(111, 3, 23, "PARTICIPATED_IN", 5200, D5),
    link(112, 23, 32, "DISCOVERED", 5200, D5, { kind: "told" }),
    link(113, 24, 23, "MENTIONED_IN", 5500, D5),
    link(114, 1, 24, "PARTICIPATED_IN", 5500, D1),
  ],
  chapters: [
    { number: 1, charEnd: 1000 },
    { number: 2, charEnd: 3000 },
    { number: 3, charEnd: 6000 },
  ],
  timeline: [
    row("anchor", null, null, null, D1, D1, { note: "Chapter 1 is taken to open on 1 October 1889; the book gives no year." }),
    row("chapter", 1, "Mr. Sherlock Holmes", 0, D1, D1, { revealedPosEnd: 1000 }),
    row("chapter", 2, "The Man on the Tor", 1000, D2, D2, { revealedPosEnd: 3000 }),
    row("chapter", 3, "The Net Closes", 3000, D5, D5, { revealedPosEnd: 6000 }),
    row("event", 20, "Watson meets Stapleton on the moor.", 400, D1),
    row("event", 21, "Hugo Baskerville is killed by a hound.", 500, "1742-01-01", "1742-12-31"),
    row("event", 22, "Watson sees a man on the tor.", 2000, D2),
    row("event", 23, "Holmes names Stapleton.", 5200, D5),
    row("event", 24, "Stapleton lets the hound loose.", 5500, D1),
  ],
};
const houndChapters = hound.chapters.map((c) => c.number);
const edgeIds = (g: Gated) => g.edges.map((e) => e.id).sort((a, b) => a - b);
const named = (g: Gated, id: number) => g.nodes.find((n) => n.id === id)?.display;
const at = (chapter: number, spoilers: boolean, facts = true) => gate(hound, { chapter, spoilers, facts, highlight: noHighlight });

describe("story(pos) and the rule (as C# SpoilerGate)", () => {
  it("story(pos) is the upper bound of the row that starts latest among those shown", () => {
    expect(isTemporal(hound)).toBe(true);
    expect(storyAt(hound.timeline!, 0)).toBeNull(); // nothing read yet
    expect(storyAt(hound.timeline!, 399)).toBeNull(); // inside chapter 1, before its first event
    expect(storyAt(hound.timeline!, 400)).toBe(D1);
    expect(storyAt(hound.timeline!, 1000)).toBe(D1); // the legend of 1742 told in chapter 1 doesn't turn the clock back
    expect(storyAt(hound.timeline!, 3000)).toBe(D2);
    expect(storyAt(hound.timeline!, 6000)).toBe(D5); // event 24 (1 October, told late) doesn't either
    expect(storyAt([], 6000)).toBeNull();
  });

  it("an event row counts from its offset; a chapter row once the chapter is read to its end (the end of chapter N is the start of N + 1)", () => {
    const chapter2 = row("chapter", 2, null, 1000, D2, D2, { revealedPosEnd: 3000 });
    expect([1000, 1500, 2999, 3000].map((pos) => storyAt([chapter2], pos))).toEqual([null, null, null, D2]);
    expect(storyAt(hound.timeline!, 1000)).toBe(D1); // chapter 2 starts at 1000 and has not been read
    expect(storyAt(hound.timeline!, 1500)).toBe(D1);
    expect(storyAt(hound.timeline!, 2000)).toBe(D2); // inside chapter 2 its events carry the clock
    expect(storyAt([row("chapter", 1, null, 500, D1)], 500)).toBe(D1); // no end offset: the row's own offset
    expect(storyAt([row("event", 9, null, 400, D2)], 400)).toBe(D2);
    expect(storyAt([row("event", 9, null, 400, D2)], 399)).toBeNull();
  });

  // The C# reference: tests/TemporalGraph.Query.Tests/FullSchemaQueryTests.cs, Story_of_pos_is_the_latest_story_time_the_reader_has_been_shown.
  it("a flashback told late doesn't turn the clock back, and its year-wide upper bound doesn't push it on", () => {
    const book = [
      row("chapter", 1, null, 0, D1, D1, { revealedPosEnd: 1000 }),
      row("chapter", 2, null, 1000, D1, D2, { revealedPosEnd: 3000 }),
      row("event", 5, null, 2010, D2),
      row("event", 6, null, 2020, "1884-01-01", "1884-12-31"),
    ];
    expect([0, 1000, 1500, 2010, 3000].map((pos) => storyAt(book, pos))).toEqual([null, D1, D1, D2, D2]);
  });

  it("a year-precise row doesn't carry the clock to the year's end; ties go to the greater upper bound; rows without both bounds and the anchor don't count", () => {
    const year = row("event", 1, null, 10, "1889-01-01", "1889-12-31");
    expect(storyAt([year], 100)).toBe("1889-12-31"); // the only row: its upper bound
    expect(storyAt([year, row("event", 2, null, 20, D2)], 100)).toBe(D2); // a day-precise row starting later wins
    expect(storyAt([row("event", 2, null, 20, D2), year], 100)).toBe(D2); // whatever the order
    expect(storyAt([row("event", 1, null, 10, D1, D1), row("event", 2, null, 20, D1, D5)], 100)).toBe(D5);
    expect(storyAt([row("event", 1, null, 10, D1), row("event", 2, null, 20, D5, null), row("event", 3, null, 30, null, D5)], 100)).toBe(D1);
    expect(storyAt([row("anchor", null, null, 0, D5, D5, { note: "assumed" })], 100)).toBeNull();
  });

  it("spoilers off reads the reader's clock, spoilers on the story's; a missing clock never hides", () => {
    const el = { revealedPos: 500, retractedPos: 900, storyFromLo: D2, storyUntilLo: D5 };
    expect([499, 500, 899, 900].map((pos) => visible(el, pos, false, null))).toEqual([false, true, true, false]);
    expect([D1, D2, "1889-10-04", D5].map((story) => visible(el, 0, true, story))).toEqual([false, true, true, false]);
    expect(visible(el, 0, true, null)).toBe(true); // no story date yet: nothing is hidden by it
    expect(visible({}, 0, false, null)).toBe(true);
    expect(visible({}, 0, true, D1)).toBe(true);
    expect(visible({ storyFromLo: D5 }, 0, false, D1)).toBe(true); // the story clock is not read with spoilers off
    expect(visible({ revealedPos: 500 }, 0, true, D1)).toBe(true); // nor the reader's with them on
    expect(edgeClocks(hound.edges[3])).toEqual({ revealedPos: 600, retractedPos: 5200, storyFromLo: D1, storyUntilLo: D5 });
  });
});

describe("leak tests, at every chapter boundary", () => {
  it.each([true, false])("spoilers off, facts %s: nothing the gate returns is revealed after pos, or already retracted", (facts) => {
    for (const chapter of houndChapters) {
      const pos = chapterEnd(hound.chapters, chapter);
      const g = at(chapter, false, facts);
      expect(g.nodes.length).toBeGreaterThan(0);
      for (const n of g.nodes) {
        expect(n.revealedPos!, `node ${n.id} at ch. ${chapter}`).toBeLessThanOrEqual(pos);
        if (n.retractedPos != null) expect(n.retractedPos, `node ${n.id} at ch. ${chapter}`).toBeGreaterThan(pos);
      }
      const kept = new Set(ids(g));
      for (const e of g.edges) {
        expect(e.pos!, `edge ${e.id} at ch. ${chapter}`).toBeLessThanOrEqual(pos);
        if (e.retractedPos != null) expect(e.retractedPos, `edge ${e.id} at ch. ${chapter}`).toBeGreaterThan(pos);
        expect(kept.has(e.source) && kept.has(e.target), `edge ${e.id}: both ends drawn`).toBe(true);
      }
    }
  });

  it.each([true, false])("spoilers on, facts %s: nothing the gate returns starts after story(pos), or has already ended", (facts) => {
    for (const chapter of houndChapters) {
      const story = storyAt(hound.timeline!, chapterEnd(hound.chapters, chapter))!;
      const g = at(chapter, true, facts);
      expect(g.nodes.length).toBeGreaterThan(0);
      const kept = new Set(ids(g));
      for (const el of [...g.nodes, ...g.edges]) {
        expect(el.storyFromLo! <= story, `${el.id} at ch. ${chapter}: starts ${el.storyFromLo}, story ${story}`).toBe(true);
        if (el.storyUntilLo != null) expect(el.storyUntilLo > story, `${el.id} at ch. ${chapter}: ended ${el.storyUntilLo}`).toBe(true);
      }
      for (const e of g.edges) expect(kept.has(e.source) && kept.has(e.target), `edge ${e.id}: both ends drawn`).toBe(true);
    }
  });

  it("the two modes show what they should (the fixture is not passing by showing nothing)", () => {
    expect(ids(at(1, false))).toEqual([1, 3, 4, 20, 21, 30]);
    expect(edgeIds(at(1, false))).toEqual([100, 101, 102, 103, 106, 107]);
    expect(ids(at(2, false))).toEqual([1, 2, 3, 4, 20, 21, 22, 30, 31]);
    // Spoilers on at chapter 1: what is true on 1 October, whenever the book tells it. Vandeleur (1880), the fact that they are one
    // man (1880; nothing links it yet, so it is dropped like any fact without an edge) and the hound let loose (told in chapter 3)
    // count; chapter 2's events don't.
    expect(ids(at(1, true))).toEqual([1, 2, 3, 4, 20, 21, 24, 30]);
    expect(edgeIds(at(1, true))).toEqual([100, 101, 102, 103, 106, 107, 114]);
    // A referenced event: with spoilers off hidden until it is told (chapter 3); with them on, there from its own story time.
    expect([1, 2, 3].map((ch) => ids(at(ch, false)).includes(24))).toEqual([false, false, true]);
    expect([1, 2, 3].map((ch) => ids(at(ch, true)).includes(24))).toEqual([true, true, true]);
    // Facts and events off: people and places, and the edges between them (a perception of a person stays).
    expect(ids(at(2, false, false))).toEqual([1, 2, 3, 4]);
    expect(edgeIds(at(2, false, false))).toEqual([103, 106]);
  });

  it("a retracted element disappears at its retractedPos (spoilers off)", () => {
    expect([1, 2, 3].map((ch) => ids(at(ch, false)).includes(31))).toEqual([false, true, false]); // revealed 2100, retracted 5300
    expect([1, 2, 3].map((ch) => edgeIds(at(ch, false)).includes(110))).toEqual([false, true, false]);
    expect([1, 2, 3].map((ch) => edgeIds(at(ch, false)).includes(103))).toEqual([true, true, false]); // trusts, until Holmes names him
    expect(edgeIds(at(3, false))).toContain(104); // distrusts, from then
    // The rule itself, at the offsets either side.
    expect([5299, 5300].map((pos) => visible(hound.nodes.find((n) => n.id === 31)!, pos, false, null))).toEqual([true, false]);
  });

  it("a closed perception edge disappears at its storyUntilLo (spoilers on)", () => {
    expect([1, 2, 3].map((ch) => edgeIds(at(ch, true)).includes(103))).toEqual([true, true, false]); // until 5 October
    expect([1, 2, 3].map((ch) => edgeIds(at(ch, true)).includes(104))).toEqual([false, false, true]);
    expect([1, 2, 3].map((ch) => edgeIds(at(ch, true)).includes(110))).toEqual([false, true, false]);
    expect(["1889-10-04", D5].map((story) => visible(edgeClocks(hound.edges.find((e) => e.id === 103)!), 0, true, story))).toEqual([true, false]);
  });
});

describe("identity: IDENTICAL_TO is an edge under the same rule", () => {
  it.each([false, true])("spoilers %s: while the edge is hidden the two are unrelated and don't share a name", (spoilers) => {
    for (const chapter of [1, 2]) {
      const g = at(chapter, spoilers);
      expect(edgeIds(g)).not.toContain(105);
      const [a, b] = [named(g, 1), named(g, 2)];
      expect(a).toBe("Stapleton");
      if (b !== undefined) expect(b).toBe("Vandeleur"); // drawn or not, never under the other's name
      expect(a).not.toBe(b);
      // No visible edge joins them at all.
      expect(g.edges.some((e) => (e.source === 1 && e.target === 2) || (e.source === 2 && e.target === 1))).toBe(false);
    }
    expect(named(at(2, spoilers), 2)).toBe("Vandeleur"); // by chapter 2 he is drawn in both modes, as someone else
  });

  it.each([false, true])("spoilers %s: once it is visible both show the name the reader saw first, as two nodes joined by the edge", (spoilers) => {
    const g = at(3, spoilers);
    expect(edgeIds(g)).toContain(105);
    expect(ids(g)).toEqual(expect.arrayContaining([1, 2])); // never merged
    expect([named(g, 1), named(g, 2)]).toEqual(["Stapleton", "Stapleton"]);
    expect([1, 2].map((id) => viewAt(hound, 3, spoilers).name(hound.nodes.find((n) => n.id === id)!))).toEqual(["Stapleton", "Stapleton"]);
  });

  it("takes the roster and short names over the union, once the text has used them", () => {
    // Vandeleur's node carries the roster spelling "Jack Stapleton", first used at 5600 (after the reveal at 5200).
    const book: GraphPayload = {
      ...hound,
      nodes: hound.nodes.map((n) => (n.id === 2 ? { ...n, rosterName: "Jack Stapleton", aliasFirstPos: [...n.aliasFirstPos, { alias: "Jack Stapleton", pos: 5600 }] } : n)),
      chapters: [...hound.chapters.slice(0, 2), { number: 3, charEnd: 5400 }, { number: 4, charEnd: 6000 }],
    };
    const names = (chapter: number, spoilers: boolean) => {
      const g = gate(book, { chapter, spoilers, facts: false, highlight: noHighlight });
      return [named(g, 1), named(g, 2)];
    };
    expect(names(2, false)).toEqual(["Stapleton", "Vandeleur"]);
    expect(names(3, false)).toEqual(["Stapleton", "Stapleton"]); // joined; the roster spelling not used yet
    expect(names(4, false)).toEqual(["Jack Stapleton", "Jack Stapleton"]);
    expect(names(3, true)).toEqual(["Jack Stapleton", "Jack Stapleton"]); // spoilers on: the roster spelling, as displayName
    expect(names(1, true)).toEqual(["Stapleton", "Jack Stapleton"]); // not joined: each its own
  });
});

describe("a book indexed before the full schema (no timeline, no clocks) is gated as before", () => {
  it("an empty timeline changes nothing, and Spoilers on hides nothing", () => {
    const empty: GraphPayload = { ...payload, timeline: [] };
    expect(isTemporal(payload)).toBe(false);
    expect(isTemporal(empty)).toBe(false);
    for (const chapter of [1, 2])
      for (const spoilers of [false, true])
        for (const facts of [false, true]) {
          const o = { chapter, spoilers, facts, highlight: noHighlight };
          expect(gate(empty, o)).toEqual(gate(payload, o));
        }
    expect(nodeRows(empty, 1, 2, false)).toEqual(nodeRows(payload, 1, 2, false));
    expect(ids(gate(empty, { chapter: 1, spoilers: true, facts: true, highlight: noHighlight }))).toEqual([1, 2, 3, 10, 11]);
    expect(viewAt(payload, 1, false).story).toBeNull();
  });

  it("asks the API for the clocks, the subjects and the timeline", () => {
    for (const fields of [
      "kind revealedPos retractedPos storyFrom storyFromLo storyUntilLo subject",
      "edges { id source target type pos kind retractedPos",
      "timeline { kind refId label revealedPos revealedPosEnd storyFrom storyUntil storyFromLo storyFromHi storyUntilLo storyUntilHi fabulaSeq note }",
      "chapters { number charEnd }",
    ])
      expect(graphQuery).toContain(fields);
  });
});

describe("nodeRows on the full-schema shape", () => {
  it("a character's card: a PERCEIVES row reads kind · target; events and facts follow", () => {
    expect(nodeRows(hound, 3, 1, false)).toEqual({
      rows: [
        { id: 1, text: "trusts · Stapleton", detail: "PERCEIVES · ch. 1" },
        { id: 20, text: "Watson meets Stapleton on the moor.", detail: "ch. 1" },
        { id: 30, text: "believes · Stapleton is a naturalist.", detail: "ch. 1" },
      ],
      total: 3,
    });
    // Chapter 3: the trust is closed and the distrust shown; the retracted fact and its perception are gone.
    const ch3 = nodeRows(hound, 3, 3, false, 20).rows.map((r) => r.text);
    expect(ch3).toContain("distrusts · Stapleton");
    expect(ch3).not.toContain("trusts · Stapleton");
    expect(ch3.some((t) => t.includes("stranger"))).toBe(false);
  });

  it("a Fact's card lists who perceives it and its subjects (there is no ABOUT edge); its subject lists the fact", () => {
    expect(nodeRows(hound, 30, 1, false)).toEqual({
      rows: [
        { id: 3, text: "Watson", detail: "believes · ch. 1" },
        { id: 1, text: "Stapleton", detail: "about · ch. 1" },
        { id: 20, text: "Watson meets Stapleton on the moor.", detail: "ch. 1" },
      ],
      total: 3,
    });
    expect(nodeRows(hound, 1, 1, false).rows).toContainEqual({ id: 30, text: "Stapleton is a naturalist.", detail: "ch. 1" });
    // "Stapleton is Vandeleur." (true since 1880): with spoilers on at chapter 1 its card names both, since both are in the story
    // by then; with spoilers off at chapter 2 the fact itself is not out yet, and Vandeleur's card says nothing of it.
    expect(nodeRows(hound, 32, 1, true).rows.map((r) => r.text)).toEqual(["Stapleton", "Vandeleur"]);
    expect(nodeRows(hound, 32, 2, false)).toEqual({ rows: [], total: 0 });
    expect(nodeRows(hound, 2, 2, false).rows).toEqual([]);
  });
});

describe("the timeline slider's layout", () => {
  const tick = (l: ReturnType<typeof timelineLayout>, key: string) => l.ticks.find((t) => t.key === key)!;

  it("spoilers off: the track is the text; a tick per chapter (on its stop) and per event; labels only for what is shown", () => {
    const l = timelineLayout(hound, 2, false);
    expect(l.axis).toBe("text");
    expect(l.stops).toEqual([
      { chapter: 1, at: 1000 / 6000 },
      { chapter: 2, at: 0.5 },
      { chapter: 3, at: 1 },
    ]);
    expect(l.thumb).toBe(0.5);
    expect(l.ticks.map((t) => t.key)).toEqual(["c1", "c2", "c3", "e20", "e21", "e22", "e23", "e24"]);
    expect(tick(l, "c2")).toMatchObject({ kind: "chapter", at: 0.5, shown: true, label: "The Man on the Tor" });
    expect(tick(l, "c3")).toMatchObject({ shown: false, label: null }); // a chapter not reached keeps its title to itself
    expect(tick(l, "e20")).toMatchObject({ kind: "event", refId: 20, at: 400 / 6000, referenced: false, shown: true, label: "Watson meets Stapleton on the moor." });
    expect(tick(l, "e21")).toMatchObject({ referenced: true, shown: true }); // the legend: referenced
    expect(tick(l, "e23")).toMatchObject({ at: 5200 / 6000, shown: false, label: null }); // not told yet: no tooltip
    expect(tick(l, "e24")).toMatchObject({ referenced: true, shown: false, label: null });
    // A tick is shown exactly when it is at or left of the thumb.
    for (const t of l.ticks) expect(t.shown, t.key).toBe(t.at <= l.thumb);
    expect(l.note).toContain("taken to open on 1 October 1889");
    expect(l.story).toBe(D2);
  });

  it("spoilers on: the track is story time; the thumb sits at story(pos) and the ticks at their story dates", () => {
    const l = timelineLayout(hound, 2, true);
    expect(l.axis).toBe("story");
    expect(l.story).toBe(D2);
    // The dates: 1742-01-01, 1742-12-31, 1 Oct, 2 Oct, 5 Oct. The narrated span is 4 days, so every gap is drawn as a day at most.
    expect(l.stops).toEqual([
      { chapter: 1, at: 0.5 },
      { chapter: 2, at: 0.75 },
      { chapter: 3, at: 1 },
    ]);
    expect(l.thumb).toBe(0.75);
    expect(tick(l, "e21").at).toBe(0); // 1742: the far left, not 147 years of empty track
    expect(tick(l, "e22").at).toBe(0.75);
    expect(tick(l, "e24")).toMatchObject({ at: 0.5, shown: true, label: "Stapleton lets the hound loose." }); // told late, happened early
    expect(tick(l, "e23")).toMatchObject({ at: 1, shown: false, label: null });
    for (const t of l.ticks.filter((t) => t.kind === "event")) expect(t.shown, t.key).toBe(t.at <= l.thumb);
    expect(timelineLayout(hound, 3, true)).toMatchObject({ thumb: 1, story: D5 });
  });

  it("the story scale keeps dates in order, linear inside a gap, with long gaps drawn short", () => {
    const scale = storyScale(["1742-01-01", "1889-10-01", "1889-10-21", "1889-11-10", null, "not a date"], ["1889-10-01", "1889-11-10"]);
    // Narrated span 40 days: a gap is at most 2 days long on the track. 1742 → 1 Oct: 2; → 21 Oct: 2; → 10 Nov: 2.
    expect(scale("1742-01-01")).toBe(0);
    expect(scale("1889-10-01")).toBeCloseTo(1 / 3);
    expect(scale("1889-10-11")).toBeCloseTo(0.5);
    expect(scale("1889-11-10")).toBe(1);
    expect(scale("1700-01-01")).toBe(0);
    expect(scale("1900-01-01")).toBe(1);
    expect(scale(null)).toBe(0);
    expect(storyScale([D1], [D1])(D1)).toBe(0);
    // Gaps under the cap stay proportional: 1, 2 and 4 October, over a 40-day narrated span (cap 2 days).
    const days = storyScale([D1, D2, "1889-10-04", "1889-11-10"], [D1, "1889-11-10"]);
    expect(days("1889-10-04") - days(D2)).toBeCloseTo(2 * (days(D2) - days(D1)));
    expect(days("1889-10-03")).toBeCloseTo((days(D2) + days("1889-10-04")) / 2);
  });

  it("a pointer picks the nearest stop; among stops at the same place, the one nearest the current chapter", () => {
    const stops = [
      { chapter: 1, at: 0 },
      { chapter: 2, at: 0.5 },
      { chapter: 3, at: 0.5 },
      { chapter: 4, at: 1 },
    ];
    expect(nearestStop(stops, 0.1, 4)).toBe(1);
    expect(nearestStop(stops, 0.9, 1)).toBe(4);
    expect(nearestStop(stops, 0.5, 1)).toBe(2);
    expect(nearestStop(stops, 0.5, 4)).toBe(3);
    expect(nearestStop(stops, 0.5, 3)).toBe(3);
    expect(nearestStop([], 0.5, 3)).toBe(3);
  });

  it("shows a story date as day month year", () => {
    expect(formatStoryDate("1889-10-05")).toBe("5 Oct 1889");
    expect(formatStoryDate("1742-01-01")).toBe("1 Jan 1742");
    expect(formatStoryDate("1889")).toBe("1889");
  });
});
