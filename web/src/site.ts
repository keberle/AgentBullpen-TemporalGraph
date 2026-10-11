// The AgentBullpen.AI site's app status, behind the gateway only. One switch, gatewayMode (VITE_API_BASE_URL set), as for
// everything else the site adds: standalone there is no status fetch, no redirect, no banner and no cross-links. The site keeps one status per demo
// (hidden, soon, live, paused) and its gateway is the real lock: it answers the app-unavailable problem for a demo that isn't
// open. This module adds what a visitor sees: a load-time check that sends them to the site's under-construction page unless the demo
// is live (a caller the gateway lets through simply loads the app), the same redirect on an app-unavailable problem mid-session, and the header's links
// to the site and its other live demos. Admins see exactly what the public sees: no banner, no admin-only list (a test proves it).
// Standalone it makes no calls at all (a test proves that too).
//
// Behind the gateway the demo is a signed-in app, and it fails closed: only a member the site confirms sees any of it. Before the
// status check it asks the site who the member is (/api/me). A 2xx answer with the member as JSON → the kit's user menu in the
// top bar (SiteChrome.tsx). Anything else (a 401, another status, a body that isn't the member, the site unreachable or the call
// blocked by CORS) → the site's sign-in page, with this page as returnUrl; nothing of the app renders and no other call is made.
// A 401 on any later call (the session ended) goes to the same sign-in page. Only the member's name, email and initials are
// kept: no roles, so nothing can render differently for an admin.

import { useSyncExternalStore } from "react";
import { apiBase, gatewayMode, siteUrl } from "./graphql.ts";

/** This demo's id in the site's app catalogue (= the gateway's cluster id). */
export const appId = "temporal";

/** `GET {api}/api/apps/{id}/status`. `adminAccess`: the gateway lets this caller through while the demo is closed. */
export type AppStatus = {
  status: string;
  message?: string | null;
  until?: string | null;
  underConstructionUrl?: string | null;
  adminAccess?: boolean;
};

/** One entry of `GET {api}/api/apps`: the site's demos, non-hidden, in order. */
export type SiteApp = {
  id: string;
  name: string;
  url?: string | null;
  repoUrl?: string | null;
  status: string;
  message?: string | null;
  until?: string | null;
};

/** The signed-in member, for the user menu: from the site's `GET {api}/api/me`. */
export type SiteUser = { name: string; email: string; initials: string };

export type SiteState = {
  /** Behind the site (gatewayMode); false = standalone: no calls, no links, no banner. */
  enabled: boolean;
  siteUrl: string;
  /** The load-time check has answered (standalone: at once). Until then, behind the gateway, the app renders nothing. */
  ready: boolean;
  /** The site did not confirm a member (/api/me): the visitor is on their way to the sign-in page, and the app renders nothing. */
  signedOut: boolean;
  /** The signed-in member; null standalone, and for a visitor the site did not confirm. */
  user: SiteUser | null;
  /** The status call's answer (without the access flag); null before it, or when that call failed (then the confirmed member's app shows as usual). */
  status: AppStatus | null;
  /** The header's "Other demos": the live ones, the same for every caller; never this demo. */
  otherDemos: SiteApp[];
  /** The visitor was sent away: to the sign-in page (signedOut), else to the under-construction page. */
  redirected: boolean;
};

export type SiteDeps = {
  /** The one switch: behind the gateway (gatewayMode). False → nothing below runs. */
  gateway: boolean;
  siteUrl: string;
  apiBase: string;
  fetch: (url: string, init?: RequestInit) => Promise<Response>;
  redirect: (url: string) => void;
  /** This page's full URL, for the sign-in page's returnUrl. */
  here: () => string;
};

const standalone: SiteState = {
  enabled: false,
  siteUrl: "",
  ready: true,
  signedOut: false,
  user: null,
  status: null,
  otherDemos: [],
  redirected: false,
};

/** The site's sign-in page, coming back to `here` afterwards. Null without a site to send them to. */
export function signInUrl(site: string, here: string): string | null {
  return site ? `${site}/signin?returnUrl=${encodeURIComponent(here)}` : null;
}

/** Sends the visitor to the site's sign-in page; false when there is no site address to send them to. */
function toSignIn(deps: Pick<SiteDeps, "siteUrl" | "redirect" | "here">): boolean {
  const url = signInUrl(deps.siteUrl, deps.here());
  if (url) deps.redirect(url);
  return !!url;
}

type Me = { firstName?: string | null; lastName?: string | null; initials?: string | null; email?: string | null };

/**
 * `GET {api}/api/me`: the member, from a 2xx answer whose body is a JSON object. Everything else is "signed-out": a 401, any
 * other status, a body that isn't that, the site unreachable, the call blocked by CORS. There is no "couldn't tell".
 */
async function me(deps: SiteDeps): Promise<SiteUser | "signed-out"> {
  try {
    const response = await deps.fetch(`${deps.apiBase}/api/me`, { credentials: "include", headers: { Accept: "application/json" } });
    if (!response.ok) return "signed-out";
    const m = (await response.json()) as Me | null;
    if (m === null || typeof m !== "object" || Array.isArray(m)) return "signed-out";
    const name = [m.firstName, m.lastName].map((n) => (n ?? "").trim()).filter(Boolean).join(" ");
    const email = m.email ?? "";
    const initials = m.initials || name.split(" ").map((n) => n[0]).slice(0, 2).join("").toUpperCase();
    return { name: name || email, email, initials };
  } catch {
    return "signed-out"; // unreachable, blocked, or not JSON: not a confirmed member
  }
}

/**
 * Where to send a visitor when the demo isn't open: the site's own `underConstructionUrl` (a path on the site, or an absolute
 * address on the site's origin), else `{site}/under-construction?app=temporal`. An absolute address anywhere else is not followed:
 * the answer it came in could send a visitor to any page. Null without a site to send them to.
 */
export function underConstructionUrl(site: string, given?: string | null): string | null {
  if (!site) return null;
  const fallback = `${site}/under-construction?app=${appId}`;
  if (!given) return fallback;
  if (/^https?:\/\//i.test(given)) return sameOrigin(given, site) ? given : fallback;
  return `${site}${given.startsWith("/") ? "" : "/"}${given}`;
}

function sameOrigin(a: string, b: string): boolean {
  try {
    return new URL(a).origin === new URL(b).origin;
  } catch {
    return false; // not an address at all
  }
}

/** The app-unavailable problem: `https://agentbullpen.ai/problems/app-unavailable`. */
export function isAppUnavailable(problem: { type?: string } | null | undefined): boolean {
  return !!problem?.type && /\/problems\/app-unavailable\/?$/.test(problem.type);
}

/** A call made for a confirmed member (the status, the demo list): its answer, or null when it failed. */
async function json<T>(deps: SiteDeps, path: string): Promise<T | null> {
  try {
    const response = await deps.fetch(`${deps.apiBase}${path}`, { credentials: "include", headers: { Accept: "application/json" } });
    return response.ok ? ((await response.json()) as T) : null;
  } catch {
    return null; // this call failed after the member was confirmed: the app shows as usual (the gateway locks the API itself)
  }
}

/**
 * The load-time check. Behind the gateway only a member confirmed by /api/me loads the app; anyone else is sent to the sign-in
 * page. Redirects through `deps.redirect`, also when the demo isn't open to this member.
 */
export async function loadSite(deps: SiteDeps): Promise<SiteState> {
  if (!deps.gateway || !deps.apiBase) return standalone;
  // Who is this, before anything else. Not a confirmed member → the sign-in page, and no other call is made.
  const user = await me(deps);
  if (user === "signed-out") {
    return { ...standalone, enabled: true, siteUrl: deps.siteUrl, signedOut: true, redirected: toSignIn(deps) };
  }
  const [status, apps] = await Promise.all([json<AppStatus>(deps, `/api/apps/${appId}/status`), json<SiteApp[]>(deps, "/api/apps")]);
  const list = Array.isArray(apps) ? apps : [];
  // A caller the gateway lets through (the status call reports access) loads the app like anyone on a live demo: nothing else
  // about them is kept, so nothing renders differently for them.
  const { adminAccess: access, ...shown } = status ?? {};
  const base: SiteState = { ...standalone, enabled: true, siteUrl: deps.siteUrl, user, status: status ? (shown as AppStatus) : null };
  if (status && status.status !== "live" && !access) {
    const url = underConstructionUrl(deps.siteUrl, status.underConstructionUrl);
    if (url) {
      deps.redirect(url);
      return { ...base, redirected: true };
    }
  }
  return {
    ...base,
    otherDemos: list.filter((a) => a.id !== appId && a.status === "live").map(({ id, name, url, repoUrl, status, message, until }) =>
      ({ id, name, url, repoUrl, status, message, until })),
  };
}

// ── the app's copy of the state ───────────────────────────────────────────────────────────────────────────────────────────

// Behind the gateway nothing shows until the load-time check has confirmed a member (no signed-out content).
let state: SiteState = gatewayMode ? { ...standalone, enabled: true, siteUrl, ready: false } : standalone;
let started: Promise<SiteState> | null = null;
const listeners = new Set<() => void>();
let redirect: (url: string) => void = (url) => window.location.replace(url);

/** Tests replace the redirect (a real one would leave the page). */
export function setRedirect(fn: (url: string) => void): void {
  redirect = fn;
}

const defaults = (): SiteDeps => ({
  gateway: gatewayMode,
  siteUrl,
  apiBase,
  fetch: (u, i) => fetch(u, i),
  redirect: (u) => redirect(u),
  here: () => window.location.href,
});

/** Runs the load-time check once (StrictMode runs effects twice). */
export function startSite(deps: Partial<SiteDeps> = {}): Promise<SiteState> {
  started ??= loadSite({ ...defaults(), ...deps }).then((s) => {
    state = s;
    listeners.forEach((l) => l());
    return s;
  });
  return started;
}

/** The site state for React. */
export function useSite(): SiteState {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => state,
    () => state,
  );
}

/**
 * An app-unavailable problem from any call (the demo was paused mid-session) sends the visitor to the under-construction
 * page. Returns false when there is no site to send them to (standalone), so the caller shows the error as usual.
 */
export function onAppUnavailable(problem: { underConstructionUrl?: string | null } | null | undefined, site: string = siteUrl,
  gateway: boolean = gatewayMode): boolean {
  if (!gateway) return false;
  const url = underConstructionUrl(site, problem?.underConstructionUrl);
  if (!url) return false;
  redirect(url);
  return true;
}

/**
 * A 401 from any call (the session ended mid-use) sends the visitor to the site's sign-in page, to come back to this page.
 * Returns false when there is no site to send them to (standalone, or no VITE_SITE_URL), so the caller shows the error as usual.
 */
export function onSignedOut(deps: Partial<SiteDeps> = {}): boolean {
  const d = { ...defaults(), ...deps };
  return d.gateway && toSignIn(d);
}

/**
 * The user menu's "Sign out": the site's sign-out (POST {api}/api/auth/signout with its antiforgery token and the
 * member's cookie), then the site's home page. A session that has already ended counts as signed out. False = it didn't happen
 * (the site refused or couldn't be reached), and the visitor stays where they are.
 */
export async function signOut(deps: Partial<SiteDeps> = {}): Promise<boolean> {
  const d = { ...defaults(), ...deps };
  if (!d.gateway || !d.apiBase || !d.siteUrl) return false;
  try {
    const asked = await d.fetch(`${d.apiBase}/api/antiforgery/token`, { credentials: "include", headers: { Accept: "application/json" } });
    if (asked.ok) {
      const { token } = (await asked.json()) as { token: string };
      const response = await d.fetch(`${d.apiBase}/api/auth/signout`, {
        method: "POST",
        credentials: "include",
        headers: { "X-CSRF-TOKEN": token },
      });
      if (!response.ok && response.status !== 401) return false;
    } else if (asked.status !== 401) return false;
  } catch {
    return false;
  }
  d.redirect(`${d.siteUrl}/`);
  return true;
}
