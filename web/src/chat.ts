// Character chat: the GraphQL shapes of the `chat` mutation, `chatAvailable` and `chatCharacters`, and the text the
// chat panel shows under each answer ("What I looked up"). What the panel shows for each problem (problemView).
// Each reply's tokens and cost (replyLine), and today's allowance in dollars under the input (allowanceView).

import type { GraphQlError, Problem } from "./graphql.ts";

/** avatar: the 512×512 image, relative to the site root, or null (initials then). */
export type ChatCharacter = { name: string; displayName: string; initials: string; nodeId: number | null; avatar: string | null };
/** nodeIds/edgeIds: the ids of the rows the model saw, lit up on the graph. */
export type ChatToolCall = { tool: string; args: string; rows: number; resolvedName: string | null; nodeIds: number[]; edgeIds: number[] };
export type ChatResult = {
  sessionId: string;
  answer: string;
  toolCalls: ChatToolCall[];
  lateRowsSuppressed: number;
  /** The turn's totals over every model call. costUsd: what they cost, or null when a call came without a cost. */
  usage: { inputTokens: number; outputTokens: number; costUsd?: number | null };
  model: string | null;
  /** The allowance after this turn (gateway), unlimited (standalone), or null. */
  budget?: Budget | null;
  /** The site answered with its fallback model (labels), or null. */
  fallback?: { requested: string; used: string } | null;
};

/** A model the reader may pick (standalone: Chat:Models profiles; gateway: the site's list). */
/** `available` false while the site has tripped the model's provider, until `unavailableUntilUtc` when it says. */
export type ChatModel = {
  id: string;
  displayName: string;
  provider: string;
  isDefault: boolean;
  available?: boolean;
  unavailableUntilUtc?: string | null;
};
/**
 * The member's daily allowance in US dollars (from the gateway's headers); unlimited standalone and for
 * the level "none". level: off, light, standard, generous, custom, none; null standalone. resetsAtUtc: when it starts again.
 */
export type Budget = { level?: string | null; dailyUsd: number | null; remainingUsd: number | null; unlimited: boolean; resetsAtUtc?: string | null };

const budgetFields = "budget { level dailyUsd remainingUsd unlimited resetsAtUtc }";

export const chatQueries = {
  available: "{ chatAvailable }",
  characters:
    "query($book: String!, $ch: Int!, $s: Boolean!) { chatCharacters(bookId: $book, chapter: $ch, spoilers: $s) { name displayName initials nodeId avatar } }",
  chat: `mutation($input: ChatInput!) { chat(input: $input) { sessionId answer toolCalls { tool args rows resolvedName nodeIds edgeIds } lateRowsSuppressed usage { inputTokens outputTokens costUsd } model ${budgetFields} fallback { requested used } } }`,
  /** The model picker and the allowance. */
  models: `{ chatModels { id displayName provider isDefault available unavailableUntilUtc } ${budgetFields} }`,
  /** The allowance alone, re-read when the tab gets the focus back. */
  budget: `{ ${budgetFields} }`,
};

/** The note shown when no model is configured (README section "Dev LLM provider"). */
export const noModelNote = "No model configured — see README › Dev LLM provider";

/** `facts_about(character: "Holmes") → 12 rows`, plus ` (as "Hound of the Baskervilles")` when the name was resolved. */
export function describeCall(c: ChatToolCall): string {
  return `${c.tool}(${c.args}) → ${c.rows} row${c.rows === 1 ? "" : "s"}${c.resolvedName ? ` (as “${c.resolvedName}”)` : ""}`;
}

const thousands = (n: number) => n.toLocaleString("en-US");

// ── Tokens and cost per reply, and today's allowance ────────────────────────────────────────────────────────────────

/** A reply's cost: 4 decimals ("$0.0004"); "<$0.0001" for anything smaller that isn't nothing. */
export function costText(usd: number): string {
  return usd > 0 && usd < 0.0001 ? "<$0.0001" : `$${usd.toFixed(4)}`;
}

/**
 * The line under each reply: "1,842 tokens · $0.0004". Tokens always (in + out, over every model call of the turn); the cost only
 * when the API has one (gateway: the site's; standalone: the provider's own). It is never estimated here.
 */
export function replyLine(usage: ChatResult["usage"]): string {
  const total = usage.inputTokens + usage.outputTokens;
  const tokens = `${thousands(total)} token${total === 1 ? "" : "s"}`;
  return usage.costUsd == null ? tokens : `${tokens} · ${costText(usage.costUsd)}`;
}

/** The line's tooltip: "1,610 in · 232 out · GPT-4o-mini" (the model's display name; left out when unknown). */
export function replyLineTitle(usage: ChatResult["usage"], model: string | null | undefined): string {
  return [`${thousands(usage.inputTokens)} in`, `${thousands(usage.outputTokens)} out`, ...(model ? [model] : [])].join(" · ");
}

/** An allowance amount: "$1.00". */
export function dollars(usd: number): string {
  return `$${usd.toFixed(2)}`;
}

/** What is left, in whole cents and never rounded up: "$0.98"; "<$0.01" for a remainder under a cent; "$0.00" for none. */
export function leftText(usd: number): string {
  if (usd <= 0) return dollars(0);
  return usd < 0.01 ? "<$0.01" : dollars(Math.floor(usd * 100 + 1e-9) / 100);
}

/** Under this share of the day's allowance the meter turns to the warning colour. */
export const lowShare = 0.1;

export const chatOffNotice = "Chat isn’t turned on for your account yet. The graph and everything else still work.";
export const chatOffLine = "Chat is off for your account";

export type AllowanceView = {
  /** normal, low (under 10 % left), used-up (nothing left until the reset), off (chat isn't turned on for the account). */
  state: "normal" | "low" | "used-up" | "off";
  /** The footer's left side: "$0.98 of $1.00 left today", or "Chat is off for your account". */
  left: string;
  /** Its right side: "Resets 6:00 PM"; null when nothing resets (off). */
  reset: string | null;
  /** The meter, 0–100: the share of today's allowance left. */
  percent: number;
  /** The notice above the input (used-up, off); null otherwise. */
  notice: string | null;
  /** The input and Send are off. */
  disableInput: boolean;
  /** used-up: when the allowance starts again (the input comes back by itself then). */
  until: Date | null;
};

/**
 * The chat panel's allowance footer, behind the site only. Null = no footer: standalone, no headers, or no limit (the
 * level "none"). The site's levels: off, light, standard, generous, custom, none.
 */
export function allowanceView(b: Budget | null | undefined, opts: { now?: Date; time?: (d: Date) => string } = {}): AllowanceView | null {
  if (!b || b.unlimited || b.level === "none") return null;
  if (b.level === "off") return { state: "off", left: chatOffLine, reset: null, percent: 0, notice: chatOffNotice, disableInput: true, until: null };
  if (b.remainingUsd == null) return null;
  const now = opts.now ?? new Date();
  const time = opts.time ?? ((d: Date) => localTime(d, now));
  const until = b.resetsAtUtc ? new Date(b.resetsAtUtc) : nextUtcMidnight(now);
  const reset = `Resets ${time(until)}`;
  const daily = b.dailyUsd;
  if (b.remainingUsd <= 0) {
    return {
      state: "used-up",
      left: daily == null ? `${dollars(0)} left today` : `${dollars(0)} of ${dollars(daily)} left today`,
      reset,
      percent: 0,
      notice: `You’ve used today’s ${daily == null ? "allowance" : dollars(daily)}. Chat comes back at ${time(until)}.`,
      disableInput: true,
      until,
    };
  }
  if (daily == null || daily <= 0) return null;
  const share = Math.min(1, b.remainingUsd / daily);
  return {
    state: share < lowShare ? "low" : "normal",
    left: `${leftText(b.remainingUsd)} of ${dollars(daily)} left today`,
    reset,
    // Something left never reads as an empty meter.
    percent: Math.max(1, Math.round(share * 100)),
    notice: null,
    disableInput: false,
    until: null,
  };
}

/**
 * Calls back when the tab gets the reader's attention again (the window focused, or the page shown after being hidden); returns the
 * function that stops listening. The panel re-reads the allowance then, since it may have been changed on the site meanwhile.
 */
export function onAttention(
  callback: () => void,
  win: Pick<EventTarget, "addEventListener" | "removeEventListener"> = window,
  doc: Pick<EventTarget, "addEventListener" | "removeEventListener"> & { visibilityState?: string } = document,
): () => void {
  const shown = () => {
    if (doc.visibilityState !== "hidden") callback();
  };
  win.addEventListener("focus", callback);
  doc.addEventListener("visibilitychange", shown);
  return () => {
    win.removeEventListener("focus", callback);
    doc.removeEventListener("visibilitychange", shown);
  };
}

// ── Problems (docs/design/models.md) ────────────────────────────────────────────────────────────────────────────────

export const problemBase = "https://agentbullpen.ai/problems/";

/** The slug of a problem's type URI (`budget-exhausted`, …). */
export function problemSlug(p: Problem): string {
  return p.type.startsWith(problemBase) ? p.type.slice(problemBase.length) : p.type;
}

/** The error's problem; for an error without one (an older API), a problem from its code, so the panel still knows the state. */
export function problemOf(e: GraphQlError | undefined): Problem | null {
  if (!e) return null;
  if (e.extensions?.problem) return e.extensions.problem;
  const slug: Record<string, string> = {
    BUDGET_EXHAUSTED: "budget-exhausted",
    CHAT_OFF: "chat-off",
    RATE_LIMITED: "rate-limited",
    UNKNOWN_MODEL: "model-not-allowed",
    PROVIDER_UNAVAILABLE: "provider-unavailable",
    LLM_ERROR: "upstream-error",
    TIMEOUT: "timeout",
    UNAUTHORIZED: "unauthorized",
    UPSTREAM_REJECTED: "upstream-rejected",
    REQUEST_TOO_LARGE: "request-too-large",
    PROVIDER_NOT_FOUND: "provider-not-found",
    APP_UNAVAILABLE: "app-unavailable",
  };
  const code = e.extensions?.code;
  return code && slug[code] ? { type: problemBase + slug[code], detail: e.message } : null;
}

/** "3:00 PM" today; "Sat 3:00 PM" on another day. The panel's clock words (the tests pass their own). */
export function localTime(d: Date, now: Date = new Date()): string {
  const sameDay = d.toDateString() === now.toDateString();
  return d.toLocaleString([], sameDay ? { hour: "numeric", minute: "2-digit" } : { weekday: "short", hour: "numeric", minute: "2-digit" });
}

export type ProblemView = {
  /** budget (today's allowance used up), chat-off (the account's level is off), the first rate limit (wait, then ask again once),
   * the second, the model service down, a failed or slow turn, a model not allowed, signed out, an unexpected error, or anything
   * else (shown as the API wrote it). */
  kind: "budget" | "chat-off" | "rate-wait" | "rate" | "unavailable" | "failed" | "rejected" | "too-large" | "model" | "signed-out" | "internal" | "other";
  text: string;
  /** The input stays off: until `until` (budget), while chat is off for the account, or until the reader signs in again. */
  disableInput: boolean;
  /** "auto": ask again by itself after `retryAfterMs`; "button": a Try again button; null: neither. */
  retry: "auto" | "button" | null;
  retryAfterMs?: number;
  until?: Date;
  /** budget: the day's allowance, when the problem says. */
  dailyUsd?: number;
};

export const restingText =
  "The characters are resting — the model service is unavailable right now. The site owner has been notified. Try again later.";
export const failedText = "That took too long or failed — try asking again.";
export const signedOutText = "Your session ended — sign in again";
export const rateWaitText = "One moment…";
export const rateText = "Too many questions at once — try again in a minute.";
export const rejectedText = "The model service turned that question down — try asking it another way.";
export const tooLargeText = "That's too long to send — ask a shorter question, or start a new conversation.";
export const notServedText = "That model isn't available right now — switched to another.";

/** The next UTC midnight: when a daily allowance starts again if the API doesn't say. */
export function nextUtcMidnight(now: Date = new Date()): Date {
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1));
}

/**
 * What the chat panel shows for a problem. `gateway`: behind the site (the model service is the site's, whose owner is
 * notified) or standalone (the person running it reads the fix-it text in `detail`). `attempt`: 0 for a reader's message, 1 for
 * the panel's own second try after a rate limit. Never the provider's name or text: only what the API put in the problem.
 */
export function problemView(
  problem: Problem | null,
  message: string,
  opts: { gateway: boolean; attempt?: number; now?: Date; time?: (d: Date) => string },
): ProblemView {
  const now = opts.now ?? new Date();
  const time = opts.time ?? ((d: Date) => localTime(d, now));
  const slug = problem ? problemSlug(problem) : null;
  switch (slug) {
    case "budget-exhausted": {
      // The panel shows this as its used-up state (allowanceView), with the notice and the footer; the text is the same notice.
      const until = problem?.resetsAtUtc ? new Date(problem.resetsAtUtc) : nextUtcMidnight(now);
      const dailyUsd = typeof problem?.dailyAllowanceUsd === "number" ? problem.dailyAllowanceUsd : undefined;
      const text = allowanceView({ dailyUsd: dailyUsd ?? null, remainingUsd: 0, unlimited: false, resetsAtUtc: until.toISOString() }, { now, time })!.notice!;
      return { kind: "budget", text, disableInput: true, retry: null, until, dailyUsd };
    }
    case "chat-off":
      // The account's allowance level is off. Nothing resets it, so no time and no timer.
      return { kind: "chat-off", text: chatOffNotice, disableInput: true, retry: null };
    case "rate-limited":
      if ((opts.attempt ?? 0) === 0) {
        const seconds = Math.min(Math.max(problem?.retryAfterSeconds ?? 5, 1), 60);
        return { kind: "rate-wait", text: rateWaitText, disableInput: false, retry: "auto", retryAfterMs: seconds * 1000 };
      }
      return { kind: "rate", text: rateText, disableInput: false, retry: null };
    case "provider-unavailable":
      return {
        kind: "unavailable",
        text: opts.gateway ? restingText : (problem?.detail ?? "The model provider can't serve the chat right now. Run `setup check` to see why."),
        disableInput: false,
        retry: "button",
      };
    case "upstream-error":
    case "timeout":
      return { kind: "failed", text: failedText, disableInput: false, retry: "button" };
    case "model-not-allowed":
      return { kind: "model", text: "That model isn't available right now.", disableInput: false, retry: null };
    case "provider-not-found":
      // The site isn't serving that model's provider: like a model not allowed, the panel reloads the picker and moves to another.
      return { kind: "model", text: notServedText, disableInput: false, retry: null };
    case "upstream-rejected":
      return { kind: "rejected", text: rejectedText, disableInput: false, retry: "button" };
    case "request-too-large":
      return { kind: "too-large", text: tooLargeText, disableInput: false, retry: null };
    case "unauthorized":
      return { kind: "signed-out", text: signedOutText, disableInput: true, retry: null };
    case "internal-error":
      return { kind: "internal", text: `Something went wrong (ref ${problem?.traceId ?? "unknown"}).`, disableInput: false, retry: null };
    default:
      return { kind: "other", text: problem?.detail ?? message, disableInput: false, retry: null };
  }
}

/** "Answered by GPT-4o mini — Claude Haiku 5.5 is unavailable right now." */
export function fallbackNote(f: { requested: string; used: string }): string {
  return `Answered by ${f.used} — ${f.requested} is unavailable right now.`;
}

/** The picker's option: the label, and for an unavailable model "— unavailable until 3:00 PM" (or "— unavailable"). */
export function modelOptionLabel(m: ChatModel, time: (d: Date) => string = (d) => localTime(d)): string {
  if (m.available !== false) return modelLabel(m);
  return `${modelLabel(m)} — unavailable${m.unavailableUntilUtc ? ` until ${time(new Date(m.unavailableUntilUtc))}` : ""}`;
}

/** The model the chat falls back to: the default if it is available, else the first available one. */
export function fallbackModel(models: ChatModel[]): ChatModel | null {
  return models.find((m) => m.isDefault && m.available !== false) ?? models.find((m) => m.available !== false) ?? null;
}

/** The note when the chat moves off an unavailable model. */
export function modelSwitchNote(unavailable: string, to: string): string {
  return `${unavailable} is unavailable right now — switched to ${to}. The earlier messages were cleared.`;
}

/** The picker shows only when there is a choice. */
export function showModelPicker(models: ChatModel[]): boolean {
  return models.length > 1;
}

/** The collapsed line: "What I looked up · 2 lookups · 1 row hidden". The tokens are in the reply's own line above it. */
export function lookupsSummary(r: Pick<ChatResult, "toolCalls" | "lateRowsSuppressed">): string {
  const parts = ["What I looked up", `${r.toolCalls.length} lookup${r.toolCalls.length === 1 ? "" : "s"}`];
  if (r.lateRowsSuppressed > 0) parts.push(`${r.lateRowsSuppressed} row${r.lateRowsSuppressed === 1 ? "" : "s"} hidden`);
  return parts.join(" · ");
}

/** The expanded lines: each tool call, the hidden rows, then tokens, the cost when it is known, and the model's id. */
export function lookupsDetail(r: ChatResult): string[] {
  const lines = r.toolCalls.length > 0 ? r.toolCalls.map(describeCall) : ["(no lookups)"];
  if (r.lateRowsSuppressed > 0) lines.push(`${r.lateRowsSuppressed} row${r.lateRowsSuppressed === 1 ? "" : "s"} hidden as not yet revealed`);
  const cost = r.usage.costUsd == null ? "" : ` · ${costText(r.usage.costUsd)}`;
  lines.push(`Tokens: ${thousands(r.usage.inputTokens)} in · ${thousands(r.usage.outputTokens)} out${cost}${r.model ? ` · ${r.model}` : ""}`);
  return lines;
}

/** The one-line note when chapter, character or spoilers change: the conversation starts over. */
export function newConversationNote(displayName: string, chapter: number, spoilers: boolean): string {
  return `New conversation with ${displayName} at chapter ${chapter}${spoilers ? ", spoilers on" : ""}: the earlier messages were cleared.`;
}

/** The site's sign-in page (its route is /signin), or null when no site is configured (standalone). */
export function signInUrl(site: string): string | null {
  return site ? `${site.replace(/\/+$/, "")}/signin` : null;
}

/** The one-line note when the reader picks another model. */
/** The picker's label: the display name when the API has one, else the model id. */
export function modelLabel(m: ChatModel): string {
  return m.displayName?.trim() || m.id;
}

/** The label of the model with this id; the id itself if it is not in the list. */
export function modelLabelOf(models: ChatModel[], id: string | null): string {
  const m = models.find((x) => x.id === id);
  return m ? modelLabel(m) : (id ?? "");
}

export function newModelNote(displayName: string, model: string): string {
  return `New conversation with ${displayName} using ${model}: the earlier messages were cleared.`;
}
