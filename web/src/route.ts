import { useMemo, useSyncExternalStore } from "react";

// Client-side URL routing, no router library: the URL holds the screen and the view state, and the History API's
// back/forward moves through it.
//
//   /                                       the book list
//   /book/<id>                              the book view, 2D graph tab
//   /book/<id>/3d   /book/<id>/query        the 3D graph and Graph Query tabs
//   ?ch=7&spoilers=1&facts=1&chat=dr-watson the chapter (1 if absent), Spoilers, "Show facts & events", the open chat (the slug
//                                           of the roster character's canonical name)
//
// Paths start /book/, not /books/: web/public serves the hero and avatar images under /books/.

export type TabKey = "2d" | "3d" | "query";
export const TABS: readonly TabKey[] = ["2d", "3d", "query"];

export type Route = { book: string | null; tab: TabKey; chapter: number; spoilers: boolean; facts: boolean; chat: string | null };

export const home: Route = { book: null, tab: "2d", chapter: 1, spoilers: false, facts: false, chat: null };

/** "Mr. Sherlock Holmes" → "mr-sherlock-holmes" (the chat parameter; accents dropped). */
export function slug(name: string): string {
  return name
    .normalize("NFKD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

const flag = (v: string | null) => v === "1" || v === "true";

/** The route a URL names; anything unrecognised is the book list (`known: false`, so the app can replace the URL with /). */
export function parseRoute(pathname: string, search: string): Route & { known: boolean } {
  const parts = pathname.split("/").filter(Boolean);
  if (parts.length === 0) return { ...home, known: true };
  if (parts[0] !== "book" || parts.length < 2 || parts.length > 3) return { ...home, known: false };
  let book: string;
  try {
    book = decodeURIComponent(parts[1]);
  } catch {
    return { ...home, known: false };
  }
  const tab = parts[2] ?? "2d";
  if (!TABS.includes(tab as TabKey)) return { ...home, known: false };
  const q = new URLSearchParams(search);
  const ch = Number(q.get("ch"));
  const chat = q.get("chat");
  return {
    book,
    tab: tab as TabKey,
    chapter: Number.isInteger(ch) && ch >= 1 ? ch : 1,
    spoilers: flag(q.get("spoilers")),
    facts: flag(q.get("facts")),
    chat: chat && slug(chat) ? slug(chat) : null,
    known: true,
  };
}

/** The URL for a route; defaults are left out (chapter 1, spoilers and facts off, no chat, the 2D tab). */
export function formatRoute(r: Route): string {
  if (r.book == null) return "/";
  const q = new URLSearchParams();
  if (r.chapter !== 1) q.set("ch", String(r.chapter));
  if (r.spoilers) q.set("spoilers", "1");
  if (r.facts) q.set("facts", "1");
  if (r.chat) q.set("chat", r.chat);
  const query = q.toString();
  return `/book/${encodeURIComponent(r.book)}${r.tab === "2d" ? "" : `/${r.tab}`}${query ? `?${query}` : ""}`;
}

/**
 * How a change enters the history:
 * - "push": a new entry (screen, tab, Spoilers, facts, opening or closing a chat);
 * - "replace": rewrites the current entry (corrections: a chapter past the end, a chat whose character isn't offered);
 * - "chapter": the timeline slider; it pushes, but moves within `COALESCE_MS` of the previous slider move rewrite that entry,
 *   so one drag (or a run of arrow-key steps) is one Back step, not one per chapter.
 */
export type NavKind = "push" | "replace" | "chapter";
export type HistoryState = { kind: NavKind; t: number } | null;
export const COALESCE_MS = 1000;

export function historyMode(kind: NavKind, current: HistoryState, now: number): "push" | "replace" {
  if (kind === "replace") return "replace";
  if (kind === "chapter" && current?.kind === "chapter" && now - current.t < COALESCE_MS) return "replace";
  return "push";
}

const listeners = new Set<() => void>();
function subscribe(cb: () => void) {
  listeners.add(cb);
  window.addEventListener("popstate", cb);
  return () => {
    listeners.delete(cb);
    window.removeEventListener("popstate", cb);
  };
}
const snapshot = () => window.location.pathname + window.location.search;

export function navigate(r: Route, kind: NavKind = "push") {
  const url = formatRoute(r);
  if (url === snapshot()) return;
  const current = (window.history.state as HistoryState) ?? null;
  const now = Date.now();
  if (historyMode(kind, current, now) === "push") window.history.pushState({ kind, t: now } satisfies HistoryState, "", url);
  // A correction keeps the entry's own kind (so a slider run still coalesces after it); a slider move refreshes its time.
  else window.history.replaceState(kind === "replace" ? current : ({ kind, t: now } satisfies HistoryState), "", url);
  for (const l of listeners) l();
}

/**
 * What the URL's `chat` asks of the open chat once the roster for the current chapter and spoilers has arrived: open that
 * character (as its avatar would: chat and node), close the chat, nothing (already in step), or drop the parameter from the URL
 * (a character not offered at this chapter, e.g. a pasted link to a later chapter's character).
 */
export type ChatSync = { type: "none" } | { type: "close" } | { type: "drop" } | { type: "open"; name: string; nodeId: number | null };

export function chatSync(want: string | null, chatWith: string | null, roster: readonly { name: string; nodeId: number | null }[]): ChatSync {
  if (want === (chatWith ? slug(chatWith) : null)) return { type: "none" };
  if (want == null) return { type: "close" };
  const c = roster.find((r) => slug(r.name) === want);
  return c ? { type: "open", name: c.name, nodeId: c.nodeId } : { type: "drop" };
}

/** The current route, re-rendering on navigate() and on Back/Forward (popstate). */
export function useRoute(): Route & { known: boolean } {
  const url = useSyncExternalStore(subscribe, snapshot);
  return useMemo(() => {
    const u = new URL(url, "http://x");
    return parseRoute(u.pathname, u.search);
  }, [url]);
}
