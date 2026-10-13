// @vitest-environment jsdom
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";

// The first screen while the books query is on its way (TGLoading.dc.html): the status line over two skeleton cards, the
// "it sleeps" line after 10 s, the books, or the alert with "Try again" when the query failed or 150 s passed. A link straight
// to a book shows the same states. The network is played (fetch), and the clock is the test's: nothing here waits.

const site = "https://agentbullpen.test";
const api = "https://api.agentbullpen.test";
const books = [
  { id: "hound-of-the-baskervilles", title: "The Hound of the Baskervilles", author: "Arthur Conan Doyle", hero: "books/hound/hero.jpg" },
  { id: "three-musketeers", title: "The Three Musketeers", author: "Alexandre Dumas", hero: "books/musketeers/hero.jpg" },
];

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/** The books queries the app has sent and not yet had an answer to, oldest first. */
let asked: { url: string; answer: (r: Response) => void; fail: (e: unknown) => void }[];
let calls: string[];
let redirects: string[];
let host: HTMLDivElement;
let root: Root;

/** Lets the clock run `ms` and everything that follows from it settle. */
const pass = (ms = 0) => act(async () => void (await vi.advanceTimersByTimeAsync(ms)));
/** Answers the oldest open books query. */
async function answer(how: Response | Error) {
  const q = asked.shift()!;
  if (how instanceof Error) q.fail(how);
  else q.answer(how);
  await pass();
}

const text = () => host.textContent ?? "";
const status = () => host.querySelector<HTMLElement>("[role='status']");
const alert = () => host.querySelector<HTMLElement>("[role='alert']");
const skeletons = () => host.querySelectorAll("main [aria-hidden='true']");
const cards = () => [...host.querySelectorAll("main button")].map((b) => b.textContent);
const tryAgain = () => [...host.querySelectorAll("button")].find((b) => b.textContent?.trim() === "Try again")!;
const click = (el: Element) => act(async () => void el.dispatchEvent(new MouseEvent("click", { bubbles: true })));

const starting = "Starting the temporal graph…";
const sleeps = "It sleeps when nobody is using it. The first visit after a quiet spell takes up to a minute.";
const failed = "The temporal graph didn't start. Try again in a moment.";

/**
 * Opens the app at `path` (a fresh copy of its modules). `gateway`: behind the site, with a confirmed member on a live demo;
 * else standalone. The books query stays open until the test answers it.
 */
async function open(path = "/", gateway = false) {
  window.history.replaceState(null, "", path);
  if (gateway) {
    vi.stubEnv("VITE_API_BASE_URL", api);
    vi.stubEnv("VITE_SITE_URL", site);
  }
  vi.resetModules();
  const siteAnswers: Record<string, unknown> = {
    "/api/me": { firstName: "Kim", lastName: "Evans", initials: "KE", email: "kim@example.test" },
    "/api/apps/temporal/status": { status: "live" },
    "/api/apps": [],
    "/api/antiforgery/token": { token: "t" },
  };
  vi.stubGlobal("fetch", (url: string, init?: RequestInit) => {
    calls.push(url);
    if (!url.endsWith("/graphql")) return Promise.resolve(json(siteAnswers[url.slice(api.length)]));
    return new Promise<Response>((answer, fail) => {
      asked.push({ url, answer, fail });
      init?.signal?.addEventListener("abort", () => fail(init.signal!.reason));
    });
  });
  (await import("./site.ts")).setRedirect((u) => redirects.push(u));
  const { default: App } = await import("./App.tsx");
  await act(async () => root.render(<App />));
  await pass();
}

// The app's first import compiles the whole book view; that is paid here, once, not inside the first test's time.
beforeAll(async () => void (await import("./App.tsx")), 120_000);

beforeEach(() => {
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  vi.useFakeTimers();
  // AbortSignal.timeout keeps its own clock; here it runs on the test's.
  vi.spyOn(AbortSignal, "timeout").mockImplementation((ms) => {
    const c = new AbortController();
    setTimeout(() => c.abort(new DOMException("The operation timed out.", "TimeoutError")), ms);
    return c.signal;
  });
  asked = [];
  calls = [];
  redirects = [];
  host = document.body.appendChild(document.createElement("div"));
  root = createRoot(host);
});

afterEach(async () => {
  await act(async () => root.unmount());
  host.remove();
  window.history.replaceState(null, "", "/");
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.resetModules();
});

describe("Pick a book waits for the API to start", () => {
  it("loading: the status line and two skeleton cards, no books and no \"More books later\"", async () => {
    await open();
    expect(calls).toEqual(["/graphql"]);
    expect(status()!.textContent).toBe(starting);
    expect(status()!.getAttribute("aria-live")).toBe("polite");
    expect(skeletons()).toHaveLength(2);
    expect(cards()).toEqual([]);
    expect(alert()).toBeNull();
    expect(text()).toContain("Pick a book");
    expect(text()).not.toContain("More books later");
  });

  it("slow: after 10 s the status also says why it takes this long", async () => {
    await open();
    await pass(9_999);
    expect(status()!.textContent).toBe(starting);
    await pass(1);
    expect(status()!.textContent).toBe(starting + sleeps);
    expect(skeletons()).toHaveLength(2);
    expect(alert()).toBeNull();
  });

  it("ready: the books and \"More books later\", and the status and the skeletons are gone", async () => {
    await open();
    await pass(12_000);
    await answer(json({ data: { books } }));
    expect(cards()).toEqual(["The Hound of the BaskervillesArthur Conan Doyle", "The Three MusketeersAlexandre Dumas"]);
    expect(text()).toContain("More books later");
    expect(status()).toBeNull();
    expect(skeletons()).toHaveLength(0);
    expect(alert()).toBeNull();
    // Nothing changes later: the query was answered, so neither the slow line nor the timeout applies.
    await pass(200_000);
    expect(alert()).toBeNull();
    expect(status()).toBeNull();
    expect(calls).toEqual(["/graphql"]);
  });

  it.each<[string, () => Response | Error]>([
    ["the API can't be reached", () => new TypeError("Failed to fetch")],
    ["the API answers with an error", () => json({ errors: [{ message: "boom" }] })],
    ["the answer is no GraphQL answer (a page, a 502)", () => new Response("<!doctype html>", { status: 502 })],
    ["standalone, a 401 (there is no sign-in page to go to)", () => new Response("", { status: 401 })],
  ])("timed-out when %s: the alert, no cards, and Try again asks again", async (_, how) => {
    await open();
    await answer(how());
    expect(alert()!.textContent).toBe(failed + "Try again");
    expect(status()).toBeNull();
    expect(skeletons()).toHaveLength(0);
    expect(cards()).toEqual(["Try again"]);
    expect(text()).not.toContain("More books later");
    expect(redirects).toEqual([]);

    await click(tryAgain());
    expect(calls).toEqual(["/graphql", "/graphql"]);
    expect(alert()).toBeNull();
    expect(status()!.textContent).toBe(starting);
    expect(skeletons()).toHaveLength(2);
    // The second wait has its own 10 s.
    await pass(10_000);
    expect(status()!.textContent).toBe(starting + sleeps);
    await answer(json({ data: { books } }));
    expect(cards()).toHaveLength(2);
    expect(text()).toContain("More books later");
    expect(alert()).toBeNull();
  });

  it("timed-out after 150 s without an answer: the call is given up and the alert shows", async () => {
    await open();
    expect(AbortSignal.timeout).toHaveBeenCalledWith(150_000);
    await pass(149_999);
    expect(alert()).toBeNull();
    expect(status()!.textContent).toBe(starting + sleeps);
    await pass(1);
    expect(alert()!.textContent).toContain(failed);
    expect(status()).toBeNull();
    expect(skeletons()).toHaveLength(0);

    await click(tryAgain());
    expect(calls).toEqual(["/graphql", "/graphql"]);
    expect(AbortSignal.timeout).toHaveBeenCalledTimes(2);
    asked.shift(); // the call that was given up
    await answer(json({ data: { books } }));
    expect(cards()).toHaveLength(2);
  });
});

describe("A link straight to a book waits the same way", () => {
  const link = "/book/no-such-book/query?ch=4";

  it("loading, then slow, then the page the link names", async () => {
    await open(link);
    expect(status()!.textContent).toBe(starting);
    expect(text()).not.toContain("Pick a book");
    expect(alert()).toBeNull();
    await pass(10_000);
    expect(status()!.textContent).toBe(starting + sleeps);
    await answer(json({ data: { books } }));
    expect(status()).toBeNull();
    expect(text()).toContain('There is no book "no-such-book".');
    expect(window.location.pathname + window.location.search).toBe(link);
  });

  it("timed-out when the query fails or 150 s pass, and Try again asks again", async () => {
    await open(link);
    await answer(new TypeError("Failed to fetch"));
    expect(alert()!.textContent).toBe(failed + "Try again");
    expect(status()).toBeNull();

    await click(tryAgain());
    expect(alert()).toBeNull();
    expect(status()!.textContent).toBe(starting);
    await pass(150_000);
    expect(alert()!.textContent).toContain(failed);

    await click(tryAgain());
    expect(calls).toEqual(["/graphql", "/graphql", "/graphql"]);
    asked.shift(); // the call that was given up
    await answer(json({ data: { books } }));
    expect(alert()).toBeNull();
    expect(text()).toContain('There is no book "no-such-book".');
  });
});

describe("Behind the site the errors keep their meaning", () => {
  const graphql = `${api}/temporal/graphql`;
  const before = [`${api}/api/me`, `${api}/api/apps/temporal/status`, `${api}/api/apps`, `${api}/api/antiforgery/token`, graphql];

  it("the same states, through the gateway", async () => {
    await open("/", true);
    expect(calls).toEqual(before);
    expect(status()!.textContent).toBe(starting);
    expect(skeletons()).toHaveLength(2);
    await answer(json({ data: { books } }));
    expect(cards().slice(-2)).toEqual(["The Hound of the BaskervillesArthur Conan Doyle", "The Three MusketeersAlexandre Dumas"]);
    expect(text()).toContain("More books later");
    expect(redirects).toEqual([]);
  });

  it("a 401 goes to the site's sign-in page, to come back here; no alert", async () => {
    await open("/book/treasure-island/query?ch=8", true);
    await answer(new Response("", { status: 401 }));
    expect(redirects).toEqual([`${site}/signin?returnUrl=${encodeURIComponent(window.location.href)}`]);
    expect(alert()).toBeNull();
    await pass(200_000);
    expect(alert()).toBeNull();
    expect(calls).toEqual(before);
  });

  it("app-unavailable goes to the site's under-construction page; no alert", async () => {
    await open("/", true);
    const problem = { type: "https://agentbullpen.ai/problems/app-unavailable", status: 503, underConstructionUrl: "/under-construction?app=temporal" };
    await answer(json(problem, 503));
    expect(redirects).toEqual([`${site}/under-construction?app=temporal`]);
    expect(alert()).toBeNull();
    await pass(200_000);
    expect(alert()).toBeNull();
    expect(calls).toEqual(before);
  });
});
