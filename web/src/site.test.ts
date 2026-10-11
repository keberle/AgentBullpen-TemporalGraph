import { afterEach, describe, expect, it, vi } from "vitest";
import { loadSite, onAppUnavailable, onSignedOut, signInUrl, signOut, underConstructionUrl, type SiteDeps } from "./site.ts";

// The site's app status, behind the gateway only. Standalone makes no call to the site at all.
// Behind the gateway the demo is a signed-in app (the site's /api/me, its sign-in page, its sign-out); standalone, none of it.

const site = "https://agentbullpen.test";
const api = "https://api.agentbullpen.test";
const apps = [
  { id: "temporal", name: "Temporal Graph", url: "https://temporal.agentbullpen.test", status: "live" },
  { id: "covers", name: "Covers", url: "https://covers.agentbullpen.test", status: "live" },
  { id: "next", name: "Next demo", url: "https://next.agentbullpen.test", status: "soon" },
];

const here = "http://localhost:5174/book/treasure-island/2d?ch=8&chat=jim";
const me = { id: "7", firstName: "Kim", lastName: "Evans", initials: "KE", email: "kim@example.test", roles: ["Admin"], status: "Approved" };
const signInPage = `${site}/signin?returnUrl=${encodeURIComponent(here)}`;

/** The site, played: an answer per path (a number = that HTTP status with no body, an Error = unreachable); /api/me signed in unless said. */
function deps(given: Record<string, unknown>, extra: Partial<SiteDeps> = {}) {
  const answers: Record<string, unknown> = { "/api/me": me, ...given };
  const calls: string[] = [];
  const requests: { url: string; init?: RequestInit }[] = [];
  const redirects: string[] = [];
  const d: SiteDeps = {
    gateway: true,
    siteUrl: site,
    apiBase: api,
    fetch: async (url, init) => {
      calls.push(url);
      requests.push({ url, init });
      const path = url.slice(api.length);
      if (!(path in answers)) return new Response("", { status: 404 });
      const a = answers[path];
      if (a instanceof Error) throw a;
      if (typeof a === "number") return new Response(null, { status: a });
      return new Response(JSON.stringify(a), { status: 200, headers: { "Content-Type": "application/json" } });
    },
    redirect: (u) => redirects.push(u),
    here: () => here,
    ...extra,
  };
  return { d, calls, requests, redirects };
}

describe("The site's app status", () => {
  it("standalone makes no call to the site and changes nothing, even with VITE_SITE_URL set", async () => {
    // Nor /api/me, a sign-in redirect, a user or a sign-out, though the site here would answer "signed out".
    const { d, calls, redirects } = deps({ "/api/me": 401, "/api/apps/temporal/status": { status: "paused" } }, { gateway: false, apiBase: "" });
    const s = await loadSite(d);
    expect(s).toMatchObject({ enabled: false, ready: true, signedOut: false, user: null, otherDemos: [], redirected: false });
    expect(onSignedOut(d)).toBe(false);
    expect(await signOut(d)).toBe(false);
    expect(calls).toEqual([]);
    expect(redirects).toEqual([]);
    // Nor does an app-unavailable problem mid-session redirect anywhere.
    expect(onAppUnavailable({ underConstructionUrl: `${site}/under-construction?app=temporal` }, site, false)).toBe(false);
  });

  it("not live, and the gateway wouldn't let this caller through: to the site's under-construction page", async () => {
    const { d, redirects, calls } = deps({
      "/api/apps/temporal/status": { status: "soon", underConstructionUrl: "/under-construction?app=temporal" },
      "/api/apps": apps,
    });
    const s = await loadSite(d);
    expect(redirects).toEqual([`${site}/under-construction?app=temporal`]);
    expect(s.redirected).toBe(true);
    expect(calls).toEqual([`${api}/api/me`, `${api}/api/apps/temporal/status`, `${api}/api/apps`]);
  });

  const fallback = `${site}/under-construction?app=temporal`;

  it("the fallback page when the site doesn't say where, and none without a site", () => {
    expect(underConstructionUrl(site, null)).toBe(fallback);
    expect(underConstructionUrl(site, "")).toBe(fallback);
    expect(underConstructionUrl("", null)).toBeNull();
    expect(underConstructionUrl("", "https://elsewhere.test/uc")).toBeNull();
  });

  it("an absolute address on another origin is not followed", () => {
    expect(underConstructionUrl(site, "https://elsewhere.test/uc")).toBe(fallback);
    expect(underConstructionUrl(site, `${site}.elsewhere.test/uc`)).toBe(fallback); // the site's name as a prefix
    expect(underConstructionUrl(site, `${site}@elsewhere.test/uc`)).toBe(fallback); // the site's name as a user name
    expect(underConstructionUrl(site, site.replace("https:", "http:") + "/uc")).toBe(fallback); // another scheme is another origin
  });

  it("an absolute address on the site's origin is used as given", () => {
    expect(underConstructionUrl(site, `${site}/soon?app=temporal`)).toBe(`${site}/soon?app=temporal`);
    expect(underConstructionUrl(`${site}/`, `${site.toUpperCase().replace("HTTPS", "https")}/soon`)).toBe(`${site.toUpperCase().replace("HTTPS", "https")}/soon`);
  });

  it("a relative path is on the site", () => {
    expect(underConstructionUrl(site, "/soon?app=temporal")).toBe(`${site}/soon?app=temporal`);
    expect(underConstructionUrl(site, "soon")).toBe(`${site}/soon`);
    expect(new URL(underConstructionUrl(site, "//elsewhere.test/uc")!).origin).toBe(new URL(site).origin);
    expect(new URL(underConstructionUrl(site, "javascript:alert(1)")!).origin).toBe(new URL(site).origin);
  });

  it("a malformed address falls back to the site's page", () => {
    expect(underConstructionUrl(site, "https://")).toBe(fallback);
    expect(underConstructionUrl(site, "http://[not-a-host/uc")).toBe(fallback);
    expect(underConstructionUrl("not a url", "https://elsewhere.test/uc")).toBe("not a url/under-construction?app=temporal");
  });

  it("live: no redirect; the header lists the other live demos only", async () => {
    const { d, redirects } = deps({ "/api/apps/temporal/status": { status: "live" }, "/api/apps": apps });
    const s = await loadSite(d);
    expect(redirects).toEqual([]);
    expect(s).toMatchObject({ enabled: true });
    expect(s.otherDemos.map((a) => a.id)).toEqual(["covers"]);
  });

  it("a caller the gateway lets through loads the app while it isn't live: no banner, and only the live demos", async () => {
    const { d, redirects } = deps({
      "/api/apps/temporal/status": { status: "paused", adminAccess: true },
      "/api/apps": apps.map((a) => ({ ...a, adminAccess: true })),
    });
    const s = await loadSite(d);
    expect(redirects).toEqual([]);
    expect(s).not.toHaveProperty("banner");
    expect(s).not.toHaveProperty("admin");
    expect(s.status).toEqual({ status: "paused" });
    expect(s.otherDemos.map((a) => a.name)).toEqual(["Covers"]);
  });

  it("access on the app list alone doesn't keep a caller on a closed demo (only the status call decides)", async () => {
    const { d, redirects } = deps({
      "/api/apps/temporal/status": { status: "soon" },
      "/api/apps": apps.map((a) => ({ ...a, adminAccess: true })),
    });
    expect((await loadSite(d)).redirected).toBe(true);
    expect(redirects).toEqual([`${site}/under-construction?app=temporal`]);
  });

  it("the site unreachable: the app loads as usual (the gateway is the real lock)", async () => {
    const down = new TypeError("Failed to fetch");
    const { d, redirects } = deps({ "/api/me": down, "/api/apps/temporal/status": down, "/api/apps": down });
    const s = await loadSite(d);
    expect(redirects).toEqual([]);
    expect(s).toMatchObject({ enabled: true, ready: true, signedOut: false, user: null, status: null, otherDemos: [] });
  });
});

describe("Behind the gateway the demo is a signed-in app", () => {
  const live = { "/api/apps/temporal/status": { status: "live" }, "/api/apps": apps };

  it("signed out (/api/me answers 401): to the site's sign-in page with this page as returnUrl, before any other call", async () => {
    const { d, calls, redirects } = deps({ ...live, "/api/me": 401 });
    const s = await loadSite(d);
    expect(redirects).toEqual([signInPage]);
    expect(signInPage).toBe(
      "https://agentbullpen.test/signin?returnUrl=http%3A%2F%2Flocalhost%3A5174%2Fbook%2Ftreasure-island%2F2d%3Fch%3D8%26chat%3Djim");
    expect(calls).toEqual([`${api}/api/me`]);   // not the status, not the demo list
    expect(s).toMatchObject({ enabled: true, ready: true, signedOut: true, redirected: true, user: null, otherDemos: [] });
  });

  it("signed in: the member's name, email and initials for the user menu, then the status check; no roles are kept", async () => {
    const { d, calls, requests, redirects } = deps(live);
    const s = await loadSite(d);
    expect(redirects).toEqual([]);
    expect(s).toMatchObject({ ready: true, signedOut: false, redirected: false });
    expect(s.user).toEqual({ name: "Kim Evans", email: "kim@example.test", initials: "KE" });
    expect(calls).toEqual([`${api}/api/me`, `${api}/api/apps/temporal/status`, `${api}/api/apps`]);
    expect(requests[0].init).toMatchObject({ credentials: "include" });
  });

  it("initials from the name when the site sends none", async () => {
    const { d } = deps({ ...live, "/api/me": { firstName: "Jim", lastName: "Hawkins", email: "jim@example.test" } });
    expect((await loadSite(d)).user).toEqual({ name: "Jim Hawkins", email: "jim@example.test", initials: "JH" });
  });

  it("a session that ended mid-use: the same sign-in page; without the site's address there is nowhere to send them", () => {
    const { d, redirects } = deps({});
    expect(onSignedOut(d)).toBe(true);
    expect(redirects).toEqual([signInPage]);
    expect(onSignedOut({ ...d, siteUrl: "" })).toBe(false);
    expect(redirects).toEqual([signInPage]);
    expect(signInUrl("", here)).toBeNull();
  });

  it("sign out: the site's token, a POST to its sign-out with the token and the cookie, then the site's home page", async () => {
    const { d, requests, redirects } = deps({ "/api/antiforgery/token": { token: "csrf-1" }, "/api/auth/signout": {} });
    expect(await signOut(d)).toBe(true);
    expect(requests.map((r) => [r.init?.method ?? "GET", r.url])).toEqual([
      ["GET", `${api}/api/antiforgery/token`],
      ["POST", `${api}/api/auth/signout`],
    ]);
    expect(requests[0].init).toMatchObject({ credentials: "include" });
    expect(requests[1].init).toMatchObject({ method: "POST", credentials: "include", headers: { "X-CSRF-TOKEN": "csrf-1" } });
    expect(redirects).toEqual([`${site}/`]);   // only after the POST answered
  });

  it("sign out when the session has already ended is still signed out; a refusal or an unreachable site leaves the visitor here", async () => {
    const ended = deps({ "/api/antiforgery/token": 401 });
    expect(await signOut(ended.d)).toBe(true);
    expect(ended.calls).toEqual([`${api}/api/antiforgery/token`]);
    expect(ended.redirects).toEqual([`${site}/`]);

    const refused = deps({ "/api/antiforgery/token": { token: "t" }, "/api/auth/signout": 400 });
    expect(await signOut(refused.d)).toBe(false);
    expect(refused.redirects).toEqual([]);

    const down = deps({ "/api/antiforgery/token": new TypeError("Failed to fetch") });
    expect(await signOut(down.d)).toBe(false);
    expect(down.redirects).toEqual([]);
  });
});

describe("app-unavailable mid-session", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  async function gateway() {
    vi.stubEnv("VITE_API_BASE_URL", api);
    vi.stubEnv("VITE_SITE_URL", site);
    vi.resetModules();
    const g = await import("./graphql.ts");
    const s = await import("./site.ts");
    const redirects: string[] = [];
    s.setRedirect((u) => redirects.push(u));
    return { g, redirects };
  }

  const problem = {
    type: "https://agentbullpen.ai/problems/app-unavailable",
    title: "This demo isn't open right now",
    status: 503,
    app: "temporal",
    underConstructionUrl: "/under-construction?app=temporal",
  };

  it("the gateway's 503 problem on any call sends the visitor to the under-construction page", async () => {
    const { g, redirects } = await gateway();
    vi.stubGlobal("fetch", async (url: string) =>
      url.endsWith("/api/antiforgery/token")
        ? new Response(JSON.stringify({ token: "t" }), { status: 200 })
        : new Response(JSON.stringify(problem), { status: 503, headers: { "Content-Type": "application/problem+json" } }));
    await expect(g.gql("{ books { id } }")).rejects.toBeInstanceOf(g.AppUnavailableError);
    expect(redirects).toEqual([`${site}/under-construction?app=temporal`]);
  });

  it("the same problem inside a GraphQL error (from a model call) does too", async () => {
    const { g, redirects } = await gateway();
    vi.stubGlobal("fetch", async (url: string) =>
      url.endsWith("/api/antiforgery/token")
        ? new Response(JSON.stringify({ token: "t" }), { status: 200 })
        : new Response(JSON.stringify({ errors: [{ message: "closed", extensions: { code: "APP_UNAVAILABLE", problem } }] }), { status: 200 }));
    await expect(g.gql("mutation { chat }")).rejects.toBeInstanceOf(g.AppUnavailableError);
    expect(redirects).toEqual([`${site}/under-construction?app=temporal`]);
  });

  it("A 401 on any call (the session ended) sends the visitor to the site's sign-in page, to come back to this page", async () => {
    const page = "http://localhost:5174/book/treasure-island/query?ch=8";
    const signIn = `${site}/signin?returnUrl=${encodeURIComponent(page)}`;
    vi.stubGlobal("window", { location: { href: page } });
    // The GraphQL call itself…
    let first = await gateway();
    vi.stubGlobal("fetch", async (url: string) =>
      url.endsWith("/api/antiforgery/token") ? new Response(JSON.stringify({ token: "t" }), { status: 200 }) : new Response("", { status: 401 }));
    await expect(first.g.gql("{ books { id } }")).rejects.toBeInstanceOf(first.g.SignedOutError);
    expect(first.redirects).toEqual([signIn]);
    // …or the antiforgery token it needs first.
    first = await gateway();
    vi.stubGlobal("fetch", async () => new Response("", { status: 401 }));
    await expect(first.g.gql("mutation { chat }")).rejects.toBeInstanceOf(first.g.SignedOutError);
    expect(first.redirects).toEqual([signIn]);
  });

  it("Standalone, a 401 is an ordinary error: no sign-in redirect and no call to the site", async () => {
    vi.resetModules();
    const g = await import("./graphql.ts");
    const s = await import("./site.ts");
    const redirects: string[] = [];
    s.setRedirect((u) => redirects.push(u));
    const calls: string[] = [];
    vi.stubGlobal("window", { location: { href: "http://localhost:5173/" } });
    vi.stubGlobal("fetch", async (url: string) => {
      calls.push(url);
      return new Response("", { status: 401 });
    });
    await expect(g.gql("{ books { id } }")).rejects.toBeInstanceOf(g.SignedOutError);
    expect(redirects).toEqual([]);
    expect(calls).toEqual(["/graphql"]);
    // The load-time check and the sign-out do nothing either: no /api/me, no token, no sign-out call.
    expect(await s.startSite()).toMatchObject({ enabled: false, ready: true, signedOut: false, user: null });
    expect(s.onSignedOut()).toBe(false);
    expect(await s.signOut()).toBe(false);
    expect(calls).toEqual(["/graphql"]);
    expect(redirects).toEqual([]);
  });

  it("standalone, the same answer is an ordinary error and nothing is redirected", async () => {
    vi.resetModules();
    const g = await import("./graphql.ts");
    const s = await import("./site.ts");
    const redirects: string[] = [];
    s.setRedirect((u) => redirects.push(u));
    const calls: string[] = [];
    vi.stubGlobal("fetch", async (url: string) => {
      calls.push(url);
      return new Response(JSON.stringify({ errors: [{ message: "closed", extensions: { problem } }] }), { status: 200 });
    });
    await expect(g.gql("{ books { id } }")).rejects.toBeInstanceOf(g.GraphQlRequestError);
    expect(redirects).toEqual([]);
    expect(calls).toEqual(["/graphql"]);   // same-origin only: no site, no antiforgery call
  });
});
