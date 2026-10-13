// Minimal GraphQL client for the api.
// Standalone (VITE_API_BASE_URL empty): same-origin /graphql (Vite proxies it to the api, see vite.config.ts; or the api hosts the
// built app, Web:Root). Gateway mode (VITE_API_BASE_URL = the site's API, e.g. https://api.agentbullpen.ai): the site's
// gateway at <base>/temporal/graphql, cross-origin with the member's HttpOnly session cookie (credentials: "include"), and the
// site's antiforgery token as X-CSRF-TOKEN on every POST (fetched once from <base>/api/antiforgery/token, refetched once on a 400),
// as the site's own web client does. Nothing auth-related is stored in JS beyond that in-memory token.

import { isAppUnavailable, onAppUnavailable, onSignedOut } from "./site.ts";

/**
 * RFC 9457 problem details, as every API error carries them in `extensions.problem` (docs/design/models.md). `type` is
 * https://agentbullpen.ai/problems/<slug>; the rest are the typed extensions a problem may have.
 */
export type Problem = {
  type: string;
  title?: string;
  status?: number;
  detail?: string;
  traceId?: string;
  reason?: string;
  provider?: string;
  retryAfterSeconds?: number;
  resetsAtUtc?: string;
  /** budget-exhausted: the day's allowance and what was spent, in US dollars. */
  dailyAllowanceUsd?: number;
  usedTodayUsd?: number;
  /** chat-off: the member's allowance level ("off"). */
  level?: string;
  fallbackModel?: string;
  model?: string;
  /** app-unavailable: where the site's under-construction page is (absolute, or relative to the site). */
  underConstructionUrl?: string;
  line?: number;
  column?: number;
};

export type GraphQlError = {
  message: string;
  extensions?: { code?: string; reason?: string; line?: number; column?: number; offset?: number; problem?: Problem };
};

export class GraphQlRequestError extends Error {
  readonly errors: GraphQlError[];
  constructor(errors: GraphQlError[]) {
    super(errors.map((e) => e.message).join("; "));
    this.errors = errors;
  }
}

/** The demo isn't open (the site's app-unavailable problem); the visitor is being sent to the under-construction page. */
export class AppUnavailableError extends Error {
  readonly problem: Problem;
  constructor(problem: Problem) {
    super(problem.detail ?? "This demo isn't open right now.");
    this.problem = problem;
  }
}

/** The gateway answered 401 (signed out, or the session expired). */
export class SignedOutError extends Error {
  /** The visitor is being sent to the site's sign-in page (false standalone: there is nowhere to send them). */
  readonly redirected: boolean;
  constructor(redirected = false) {
    super("You are signed out of AgentBullpen.AI.");
    this.redirected = redirected;
  }
}

/** A 401 mid-session also sends the visitor to the site's sign-in page (to come back here); the error is for the caller. */
function signedOut(): SignedOutError {
  return new SignedOutError(onSignedOut());
}

/** The site's API base, without a trailing slash; empty = standalone. */
export const apiBase = (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/+$/, "");
/** The site's address (gateway mode), for its sign-in page, the user menu's links and the header's; empty = none of them. */
export const siteUrl = (import.meta.env.VITE_SITE_URL ?? "").replace(/\/+$/, "");
export const gatewayMode = apiBase !== "";

/** Where GraphQL requests go: <base>/temporal/graphql behind the gateway, else /graphql. */
export function graphqlUrl(base: string = apiBase): string {
  return base ? `${base}/temporal/graphql` : "/graphql";
}

let csrfToken: string | null = null;

async function csrf(refresh = false, signal?: AbortSignal): Promise<string> {
  if (refresh || !csrfToken) {
    const response = await fetch(`${apiBase}/api/antiforgery/token`, { credentials: "include", headers: { Accept: "application/json" }, signal });
    if (response.status === 401) throw signedOut();
    csrfToken = ((await response.json()) as { token: string }).token;
  }
  return csrfToken;
}

async function post(query: string, variables: Record<string, unknown> | undefined, signal?: AbortSignal, refreshCsrf = false): Promise<Response> {
  const body = JSON.stringify({ query, variables });
  if (!gatewayMode) return fetch("/graphql", { method: "POST", headers: { "Content-Type": "application/json" }, body, signal });
  return fetch(graphqlUrl(), {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": await csrf(refreshCsrf, signal) },
    body,
    signal,
  });
}

/** `signal`: gives the whole call up when it aborts (the token it may need first, the request, reading the answer). */
export async function gql<T>(query: string, variables?: Record<string, unknown>, signal?: AbortSignal): Promise<T> {
  let response = await post(query, variables, signal);
  // The gateway rejects a stale antiforgery token with 400: fetch a new one once.
  if (gatewayMode && response.status === 400) response = await post(query, variables, signal, true);
  if (response.status === 401) throw signedOut();
  const body = (await response.json().catch(() => ({}))) as { data?: T; errors?: GraphQlError[] } & Partial<Problem>;
  // The site's gateway answers a demo that isn't open with the app-unavailable problem (503, problem+json) before it reaches
  // the API; the API passes the same problem on from a model call. Either way: to the under-construction page.
  const unavailable = isAppUnavailable(body as Problem) ? (body as Problem) : body.errors?.map((e) => e.extensions?.problem).find(isAppUnavailable);
  if (unavailable && onAppUnavailable(unavailable)) throw new AppUnavailableError(unavailable);
  if (body.errors?.length) throw new GraphQlRequestError(body.errors);
  if (!body.data) throw new Error(`No data (HTTP ${response.status})`);
  return body.data;
}

/** hero: the 3:2 hero image, relative to the site root (web/public), or null. */
export type Book = { id: string; title: string; author: string; hero: string | null };
export type Chapter = { number: number; title: string | null };
/** group: "sample" = the book's own queries (literal names), "tool" = the chat tools' queries. */
export type SampleQuery = { name: string; question: string; text: string; group: "sample" | "tool" };
export type CypherResult = { columns: string[]; rows: (string | null)[][]; truncated: boolean; elapsedMs: number };

export const queries = {
  books: "{ books { id title author hero } }",
  book: "query($id: String!) { chapters(bookId: $id) { number title } characters(bookId: $id) sampleQueries(bookId: $id) { name question text group } }",
  cypher:
    "query($book: String!, $q: String!, $ch: Int!, $c: String, $s: Boolean!) { cypher(bookId: $book, query: $q, chapter: $ch, character: $c, spoilers: $s) { columns rows truncated elapsedMs } }",
};
