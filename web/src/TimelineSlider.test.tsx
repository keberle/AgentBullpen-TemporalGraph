// @vitest-environment jsdom
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import TimelineSlider from "./TimelineSlider.tsx";
import type { TimelineLayout } from "./graph/model.ts";

// The slider drawn from a book's timeline. Its stops are chapters; its ticks say what they are by shape (a chapter's
// bar, a narrated event's filled dot, a referenced event's hollow one) and keep quiet about what the reader has not reached.

const layout: TimelineLayout = {
  axis: "story",
  stops: [
    { chapter: 1, at: 0.5 },
    { chapter: 2, at: 0.75 },
    { chapter: 3, at: 1 },
  ],
  thumb: 0.75,
  story: "1889-10-02",
  note: "Chapter 1 is taken to open on 1 October 1889.",
  ticks: [
    { key: "c1", kind: "chapter", refId: 1, at: 0.5, referenced: false, shown: true, label: "Mr. Sherlock Holmes" },
    { key: "c2", kind: "chapter", refId: 2, at: 0.75, referenced: false, shown: true, label: null },
    { key: "c3", kind: "chapter", refId: 3, at: 1, referenced: false, shown: false, label: null },
    { key: "e20", kind: "event", refId: 20, at: 0.5, referenced: false, shown: true, label: "Watson meets Stapleton." },
    { key: "e21", kind: "event", refId: 21, at: 0, referenced: true, shown: true, label: "Hugo is killed by a hound." },
    { key: "e23", kind: "event", refId: 23, at: 1, referenced: false, shown: false, label: null },
  ],
};

let host: HTMLDivElement;
let root: Root;
let picked: number[];
const slider = () => host.querySelector<HTMLElement>("[role='slider']")!;
const ticks = (kind: string) => [...host.querySelectorAll<HTMLElement>(`[data-tick='${kind}']`)];
const render = (chapter: number, l = layout) => act(async () => root.render(<TimelineSlider id="tl" layout={l} chapter={chapter} onChange={(n) => picked.push(n)} />));
const key = (k: string) => act(async () => slider().dispatchEvent(new KeyboardEvent("keydown", { key: k, bubbles: true })));

beforeEach(() => {
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  host = document.body.appendChild(document.createElement("div"));
  root = createRoot(host);
  picked = [];
});
afterEach(async () => {
  await act(async () => root.unmount());
  host.remove();
});

describe("TimelineSlider", () => {
  it("is a slider over the chapters, with the anchor's note labelled as an assumption", async () => {
    await render(2);
    const s = slider();
    expect(s.id).toBe("tl");
    expect([s.getAttribute("aria-valuemin"), s.getAttribute("aria-valuemax"), s.getAttribute("aria-valuenow"), s.getAttribute("aria-valuetext")]).toEqual(["1", "3", "2", "Chapter 2"]);
    expect(s.title).toBe("Assumption: Chapter 1 is taken to open on 1 October 1889.");
    expect(s.getAttribute("aria-description")).toBe("Laid out by story time. Assumption: Chapter 1 is taken to open on 1 October 1889.");
    expect(s.dataset.axis).toBe("story");
    expect(host.querySelector<HTMLElement>("[data-thumb]")!.style.left).toBe("75%");
    await render(2, { ...layout, axis: "text", note: null });
    expect(slider().title).toBe("");
    expect(slider().getAttribute("aria-description")).toBe("Laid out by the text.");
  });

  it("draws a tick per chapter and a smaller one per event, narrated filled and referenced hollow", async () => {
    await render(2);
    expect(ticks("chapter").map((t) => [t.style.left, t.title])).toEqual([
      ["50%", "Chapter 1: Mr. Sherlock Holmes"],
      ["75%", "Chapter 2"],
      ["100%", "Chapter 3"],
    ]);
    const narrated = ticks("event-narrated");
    const referenced = ticks("event-referenced");
    expect([narrated.length, referenced.length]).toEqual([2, 1]);
    // Shape, not colour: a narrated event's dot is filled with no border; a referenced one's is a ring around nothing.
    for (const t of narrated) expect([t.style.background !== "transparent", t.style.borderStyle]).toEqual([true, "none"]);
    expect([referenced[0].style.background, referenced[0].style.borderStyle]).toEqual(["transparent", "solid"]);
    expect(referenced[0].title).toBe("Told about: Hugo is killed by a hound.");
    expect(narrated[0].title).toBe("Event: Watson meets Stapleton.");
    // An event the reader has not reached: fainter, and no tooltip to give it away.
    expect([narrated[1].title, narrated[1].style.opacity]).toEqual(["", "0.3"]);
  });

  it("the keys step by chapter and stay inside the book", async () => {
    await render(2);
    await key("ArrowRight");
    await key("ArrowLeft");
    await key("Home");
    await key("End");
    await key("a");
    expect(picked).toEqual([3, 1, 1, 3]);
    picked = [];
    await render(3);
    await key("ArrowRight"); // already at the last chapter
    await key("PageUp");
    await key("PageDown");
    expect(picked).toEqual([1]);
  });
});
