// @vitest-environment jsdom
import { act, StrictMode, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Book } from "../graphql.ts";
import type { GraphPayload } from "./model.ts";

// The 3D tab mounts one renderer (one WebGL context) and keeps it: no remount when the pane's size is measured as 0 or
// changes, none when the site's state (the member, the app status) or the chat's budget updates; a lost context shows a message
// with "Reload graph" instead of a blank canvas; and leaving the tab gives the context back. The library is replaced by a stand-in
// that counts its mounts and has a renderer with a canvas, as react-force-graph-3d does (there is no WebGL in the test DOM).

type FakeRenderer = { domElement: HTMLCanvasElement; calls: string[]; lost: boolean; dispose(): void; forceContextLoss(): void; getContext(): { isContextLost(): boolean } };
const gl = vi.hoisted(() => ({
  mounts: 0,
  unmounts: 0,
  renderers: [] as FakeRenderer[],
  props: null as { width: number; height: number } | null,
}));

vi.mock("react-force-graph-3d", async () => {
  const React = await import("react");
  const Fake = React.forwardRef((props: { width: number; height: number }, ref) => {
    const el = React.useRef<HTMLDivElement>(null);
    const renderer = React.useRef<FakeRenderer | null>(null);
    gl.props = props;
    React.useImperativeHandle(ref, () => ({ renderer: () => renderer.current, zoomToFit: () => {}, cameraPosition: () => {} }), []);
    React.useLayoutEffect(() => {
      const canvas = document.createElement("canvas");
      el.current!.appendChild(canvas);
      const calls: string[] = [];
      const r: FakeRenderer = {
        domElement: canvas,
        calls,
        lost: false,
        dispose: () => void calls.push("dispose"),
        forceContextLoss: () => void calls.push("forceContextLoss"),
        getContext: () => ({ isContextLost: () => r.lost }),
      };
      renderer.current = r;
      gl.renderers.push(r);
      gl.mounts++;
      return () => {
        gl.unmounts++;
      };
    }, []);
    return <div ref={el} data-fake-graph="" />;
  });
  return { default: Fake };
});
// The 2D tab draws on a 2D canvas, which the test DOM doesn't have either.
vi.mock("react-force-graph-2d", () => ({ default: () => <div data-fake-graph-2d="" /> }));

// The API, played: each query's answer, with the chat's models and budget held back until a test lets them through.
const answers = vi.hoisted(() => ({ models: null as null | ((v: unknown) => void), calls: [] as string[] }));
vi.mock("../graphql.ts", async (original) => {
  const real = await original<typeof import("../graphql.ts")>();
  const gql = (query: string) => {
    answers.calls.push(query);
    if (query.includes("graph(bookId")) return Promise.resolve({ graph: payload });
    if (query.includes("sampleQueries")) return Promise.resolve({ chapters: payload.chapters.map((c) => ({ number: c.number, title: null })), characters: ["Jim Hawkins"], sampleQueries: [] });
    if (query.includes("chatCharacters")) return Promise.resolve({ chatCharacters: [{ name: "Jim Hawkins", displayName: "Jim Hawkins", initials: "JH", nodeId: 1, avatar: null }] });
    if (query.includes("chatModels")) return new Promise((resolve) => (answers.models = resolve));
    if (query.includes("chatAvailable")) return Promise.resolve({ chatAvailable: true });
    return Promise.reject(new Error(`unexpected query: ${query}`));
  };
  return { ...real, gql };
});

const payload: GraphPayload = {
  nodes: [
    { id: 1, label: "Character", name: "Jim Hawkins", text: null, pos: 10, aliasFirstPos: [] },
    { id: 2, label: "Character", name: "Long John Silver", text: null, pos: 20, aliasFirstPos: [] },
    { id: 3, label: "Location", name: "Hispaniola", text: null, pos: 30, aliasFirstPos: [] },
    { id: 4, label: "Fact", name: null, text: "Jim found the map", pos: 40, aliasFirstPos: [] },
  ],
  edges: [
    { id: 1, source: 1, target: 2, type: "MET", pos: 20 },
    { id: 2, source: 4, target: 1, type: "ABOUT", pos: 40 },
  ],
  chapters: Array.from({ length: 34 }, (_, i) => ({ number: i + 1, charEnd: (i + 1) * 1000 })),
};
const book: Book = { id: "treasure-island", title: "Treasure Island", author: "Robert Louis Stevenson", hero: null };

// ── the test page ─────────────────────────────────────────────────────────────────────────────────────────────────────────

type Observer = { target: Element; callback: ResizeObserverCallback };
const observers: Observer[] = [];
let host: HTMLDivElement;
let root: Root;

const wait = (ms = 5) => act(() => new Promise<void>((done) => setTimeout(done, ms)));
async function until(what: () => boolean, name: string) {
  // By the clock, not a count of waits: a busy machine stretches each wait.
  for (const end = Date.now() + 4000; Date.now() < end && !what(); ) await wait();
  if (!what()) throw new Error(`never happened: ${name}`);
}
const render = (node: ReactNode) => act(async () => root.render(node));
const pane = () => host.querySelector<HTMLElement>("[data-testid='graph-3d']");
/** The pane measured at this size, as the browser's ResizeObserver reports it. */
const measure = (width: number, height: number) =>
  act(async () => {
    for (const o of observers) if (o.target === pane()) o.callback([{ contentRect: { width, height } } as ResizeObserverEntry], null as never);
  });
const stopped = () => host.querySelector("[role='alert']")?.textContent ?? null;
const button = (name: string) => [...host.querySelectorAll("button")].find((b) => b.textContent === name) ?? null;
const click = (el: Element | null) => act(async () => el!.dispatchEvent(new MouseEvent("click", { bubbles: true })));
const lose = (r: FakeRenderer, type = "webglcontextlost") =>
  act(async () => {
    r.lost = type === "webglcontextlost";
    r.domElement.dispatchEvent(new Event(type));
  });
/** Renderers whose context the app has not given back. */
const held = () => gl.renderers.filter((r) => !r.calls.includes("forceContextLoss"));

beforeEach(() => {
  Object.assign(gl, { mounts: 0, unmounts: 0, renderers: [], props: null });
  Object.assign(answers, { models: null, calls: [] });
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
  Element.prototype.scrollTo = () => {};
  host = document.body.appendChild(document.createElement("div"));
  root = createRoot(host);
});

afterEach(async () => {
  await act(async () => root.unmount());
  host.remove();
  vi.unstubAllGlobals();
  vi.resetModules();
});

async function graph3d(extra: Record<string, unknown> = {}) {
  const { default: Graph3D } = await import("./Graph3D.tsx");
  const { degrees, gate, noHighlight } = await import("./model.ts");
  const gated = gate(payload, { chapter: 25, spoilers: false, facts: true, highlight: noHighlight });
  return <Graph3D payload={payload} gated={gated} degree={degrees(payload)} highlight={noHighlight} selectedId={null} onSelect={() => {}} {...extra} />;
}

describe("One renderer for the 3D tab", () => {
  it("mounts once the pane has a size, and a later measurement of 0 never unmounts it", async () => {
    await render(await graph3d());
    expect(gl.mounts).toBe(0); // no size yet: nothing to draw into
    await measure(1502, 865);
    expect(gl.mounts).toBe(1);
    expect(gl.props).toMatchObject({ width: 1502, height: 865 });

    for (const [w, h] of [[0, 0], [0, 865], [1502, 0], [0, 0]]) await measure(w, h);
    expect([gl.mounts, gl.unmounts]).toEqual([1, 0]);
    expect(gl.props).toMatchObject({ width: 1502, height: 865 }); // the last real size is kept
    expect(gl.renderers[0].domElement.isConnected).toBe(true);
  });

  it("a resize (the chat opening, the window) resizes the same renderer", async () => {
    await render(await graph3d());
    await measure(1502, 865);
    for (const [w, h] of [[1142, 865], [640, 480], [0, 0], [1502, 865], [1502, 400]]) await measure(w, h);
    expect([gl.mounts, gl.unmounts]).toEqual([1, 0]);
    expect(gl.props).toMatchObject({ width: 1502, height: 400 });
  });

  it("new props (the chapter, the highlight, the selection) keep the same renderer", async () => {
    await render(await graph3d());
    await measure(800, 600);
    for (let i = 0; i < 5; i++) await render(await graph3d({ selectedId: i % 2 ? 1 : null, autoRotate: i % 2 === 0, fitRequest: i }));
    expect([gl.mounts, gl.unmounts]).toEqual([1, 0]);
  });

  it("StrictMode's extra cleanup on mount gives no context back: the graph is still in the page", async () => {
    await render(<StrictMode>{await graph3d()}</StrictMode>);
    await measure(800, 600);
    await wait(20);
    expect(gl.renderers.filter((r) => r.domElement.isConnected).length).toBeGreaterThan(0);
    expect(gl.renderers.flatMap((r) => r.calls)).toEqual([]);
  });
});

describe("A lost context", () => {
  it("shows the message and Reload graph, never a blank canvas; a restored context takes the message away", async () => {
    await render(await graph3d());
    await measure(800, 600);
    expect(stopped()).toBeNull();

    await lose(gl.renderers[0]);
    expect(stopped()).toContain("The 3D view stopped");
    expect(button("Reload graph")).not.toBeNull();
    // The panel covers the whole pane in the page colour (the kit's token), over the canvas.
    expect(host.querySelector("[role='alert']")!.className).toMatch(/absolute inset-0.*bg-page/);
    expect(gl.mounts).toBe(1);

    await lose(gl.renderers[0], "webglcontextrestored");
    expect(stopped()).toBeNull();
    expect([gl.mounts, gl.unmounts]).toEqual([1, 0]);
  });

  it("Reload graph mounts a new renderer and disposes the lost one", async () => {
    await render(await graph3d());
    await measure(800, 600);
    await lose(gl.renderers[0]);
    await click(button("Reload graph"));
    await wait(20);
    expect(stopped()).toBeNull();
    expect([gl.mounts, gl.unmounts]).toEqual([2, 1]);
    expect(gl.props).toMatchObject({ width: 800, height: 600 }); // the new one at the held size, with no new measurement
    expect(gl.renderers[0].calls).toEqual(["dispose"]); // its context is already lost: disposed, nothing more to give up
    expect(gl.renderers[0].domElement.isConnected).toBe(false);
    expect(gl.renderers[1].calls).toEqual([]);
    // The old canvas no longer reports to the pane.
    await lose(gl.renderers[0]);
    expect(stopped()).toBeNull();
    // The new one does.
    await lose(gl.renderers[1]);
    expect(stopped()).toContain("The 3D view stopped");
  });
});

describe("Leaving the 3D tab gives its context back", () => {
  async function tabs(mode: "2d" | "3d") {
    const { default: GraphPane } = await import("./GraphPane.tsx");
    const { noHighlight } = await import("./model.ts");
    return (
      <GraphPane mode={mode} payload={payload} chapter={25} spoilers={false} facts onFactsChange={() => {}} highlight={noHighlight} selectedId={null} onSelect={() => {}} />
    );
  }

  it("30 switches 2D ↔ 3D: never more than one context held, and every renderer left behind is released", async () => {
    await render(await tabs("3d"));
    await until(() => pane() !== null, "the 3D tab loaded");
    await measure(1502, 865);
    expect(gl.mounts).toBe(1);
    let most = held().length;
    for (let i = 0; i < 30; i++) {
      await render(await tabs("2d"));
      await wait();
      expect(held()).toEqual([]);
      await render(await tabs("3d"));
      await until(() => pane() !== null, "the 3D tab is back");
      await measure(1502, 865);
      most = Math.max(most, held().length);
    }
    expect(gl.mounts).toBe(31);
    expect(gl.unmounts).toBe(30);
    expect(most).toBe(1); // the browser's limit is 16
    for (const r of gl.renderers.slice(0, 30)) expect(r.calls).toEqual(["dispose", "forceContextLoss"]);
    expect(gl.renderers[30].calls).toEqual([]);
  });

  it("the page going away (another book) releases it too", async () => {
    await render(await tabs("3d"));
    await until(() => pane() !== null, "the 3D tab loaded");
    await measure(800, 600);
    await render(<div />);
    await wait();
    expect(gl.renderers.map((r) => r.calls)).toEqual([["dispose", "forceContextLoss"]]);
  });
});

describe("The site's state and the chat's budget don't remount the graph", () => {
  const site = "https://agentbullpen.test";
  const api = "https://api.agentbullpen.test";

  it("a member's page (3D, ch 25, facts, chat open): the allowance arriving used up, then the member and app status, keep one renderer", async () => {
    const { default: BookView } = await import("../BookView.tsx");
    const siteModule = await import("../site.ts");
    const route = { book: book.id, tab: "3d" as const, chapter: 25, spoilers: false, facts: true, chat: "jim-hawkins" };
    await render(<BookView book={book} route={route} onChangeBook={() => {}} />);
    await until(() => pane() !== null, "the 3D tab loaded");
    await measure(1502, 865);
    await until(() => host.querySelector("[aria-label='Chat']") !== null && answers.models !== null, "the chat opened and asked for its models");
    expect([gl.mounts, gl.unmounts]).toEqual([1, 0]);

    // The chat's models and the member's allowance arrive: used up.
    await act(async () =>
      answers.models!({
        chatModels: [{ id: "openai:gpt-4o-mini", displayName: "OpenAI: GPT-4o-mini", provider: "openai", isDefault: true, available: true, unavailableUntilUtc: null }],
        budget: { level: "standard", dailyUsd: 1, remainingUsd: 0, unlimited: false, resetsAtUtc: new Date(Date.now() + 3600e3).toISOString() },
      }),
    );
    await until(() => /used today’s \$1\.00/.test(host.textContent ?? ""), "the used-up notice");
    expect(host.textContent).toContain("$0.00 of $1.00 left today");
    expect([gl.mounts, gl.unmounts]).toEqual([1, 0]);
    // The chat's messages changing the layout around the pane: measured again, the same renderer.
    await measure(1502, 865);
    await measure(1142, 840);
    expect([gl.mounts, gl.unmounts]).toEqual([1, 0]);

    // The site's answers arrive (the member for the user menu, the demo's status, the other demos).
    const played: Record<string, unknown> = {
      "/api/me": { firstName: "Kim", lastName: "Evans", initials: "KE", email: "kim@example.test" },
      "/api/apps/temporal/status": { status: "live" },
      "/api/apps": [{ id: "covers", name: "Covers", url: "https://covers.agentbullpen.test", status: "live" }],
    };
    await act(async () => {
      await siteModule.startSite({
        gateway: true,
        siteUrl: site,
        apiBase: api,
        fetch: async (url) => new Response(JSON.stringify(played[url.slice(api.length)]), { status: 200 }),
        redirect: () => {},
        here: () => "http://localhost:5174/",
      });
    });
    await until(() => host.querySelector("[aria-label='User menu for Kim Evans']") !== null, "the user menu");
    expect(host.textContent).toContain("Other demos");
    expect([gl.mounts, gl.unmounts]).toEqual([1, 0]);
    expect(gl.renderers[0].calls).toEqual([]);
    // Nothing asked the API twice: no render loop behind the budget.
    expect(answers.calls.filter((q) => q.includes("chatModels")).length).toBe(1);
    expect(answers.calls.filter((q) => q.includes("graph(bookId")).length).toBe(1);
  });
});
