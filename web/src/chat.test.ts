import { describe, expect, it } from "vitest";
import {
  allowanceView,
  chatOffLine,
  chatOffNotice,
  chatQueries,
  costText,
  describeCall,
  dollars,
  fallbackModel,
  fallbackNote,
  failedText,
  leftText,
  lookupsDetail,
  lookupsSummary,
  modelLabel,
  modelLabelOf,
  modelOptionLabel,
  modelSwitchNote,
  newConversationNote,
  newModelNote,
  nextUtcMidnight,
  onAttention,
  problemOf,
  problemSlug,
  problemView,
  rateText,
  rateWaitText,
  replyLine,
  replyLineTitle,
  restingText,
  showModelPicker,
  signInUrl,
  signedOutText,
  type Budget,
  type ChatModel,
  type ChatResult,
} from "./chat.ts";
import { graphqlUrl, type Problem } from "./graphql.ts";

const result: ChatResult = {
  sessionId: "s",
  answer: "a",
  toolCalls: [
    { tool: "facts_about", args: 'character: "hound"', rows: 31, resolvedName: "Hound of the Baskervilles", nodeIds: [], edgeIds: [] },
    { tool: "characters_so_far", args: "", rows: 1, resolvedName: null, nodeIds: [], edgeIds: [] },
  ],
  lateRowsSuppressed: 2,
  usage: { inputTokens: 3200, outputTokens: 212 },
  model: "gpt-4.1-mini",
};

describe("What I looked up", () => {
  it("describes each call as the CLI does", () => {
    expect(describeCall(result.toolCalls[0])).toBe('facts_about(character: "hound") → 31 rows (as “Hound of the Baskervilles”)');
    expect(describeCall(result.toolCalls[1])).toBe("characters_so_far() → 1 row");
  });

  it("summarises lookups and hidden rows (the tokens are in the reply's own line)", () => {
    expect(lookupsSummary(result)).toBe("What I looked up · 2 lookups · 2 rows hidden");
    expect(lookupsSummary({ toolCalls: [], lateRowsSuppressed: 0 })).toBe("What I looked up · 0 lookups");
  });

  it("the detail has the cost when the API has one", () => {
    expect(lookupsDetail({ ...result, usage: { inputTokens: 3200, outputTokens: 212, costUsd: 0.000605 } }).at(-1)).toBe(
      "Tokens: 3,200 in · 212 out · $0.0006 · gpt-4.1-mini",
    );
  });

  it("lists the calls, the hidden rows and the tokens (no cost: none is made up)", () => {
    expect(lookupsDetail(result)).toEqual([
      'facts_about(character: "hound") → 31 rows (as “Hound of the Baskervilles”)',
      "characters_so_far() → 1 row",
      "2 rows hidden as not yet revealed",
      "Tokens: 3,200 in · 212 out · gpt-4.1-mini",
    ]);
  });

  it("says a new conversation started", () => {
    expect(newConversationNote("Holmes", 15, true)).toBe("New conversation with Holmes at chapter 15, spoilers on: the earlier messages were cleared.");
  });
});

describe("Models, budget and the gateway", () => {
  const model = (id: string, isDefault = false): ChatModel => ({ id, displayName: id, provider: "openai", isDefault });

  it("shows the picker only when there is a choice", () => {
    expect(showModelPicker([model("default", true)])).toBe(false);
    expect(showModelPicker([model("default", true), model("alt")])).toBe(true);
    expect(newModelNote("Holmes", "alt")).toBe("New conversation with Holmes using alt: the earlier messages were cleared.");
  });

  it("labels a model by its display name, else its id", () => {
    expect(modelLabel({ ...model("openrouter:openai/gpt-4o-mini"), displayName: "GPT-4o mini" })).toBe("GPT-4o mini");
    expect(modelLabel({ ...model("openrouter:openai/gpt-4o-mini"), displayName: "  " })).toBe("openrouter:openai/gpt-4o-mini");
    const list = [{ ...model("a"), displayName: "Model A" }];
    expect(modelLabelOf(list, "a")).toBe("Model A");
    expect(modelLabelOf(list, "gone")).toBe("gone");
    expect(modelLabelOf(list, null)).toBe("");
  });


  it("goes to /graphql standalone and to the gateway's /temporal/graphql otherwise", () => {
    expect(graphqlUrl("")).toBe("/graphql");
    expect(graphqlUrl("https://api.agentbullpen.ai")).toBe("https://api.agentbullpen.ai/temporal/graphql");
    expect(signInUrl("")).toBeNull();
    expect(signInUrl("https://www.agentbullpen.ai/")).toBe("https://www.agentbullpen.ai/signin");
  });
});

// What the chat panel shows for each problem (docs/design/models.md). The panel renders problemView's text and acts on
// its fields; these tests pin every row of the table.
describe("problem states in the chat panel", () => {
  const p = (slug: string, extra: Partial<Problem> = {}): Problem => ({ type: `https://agentbullpen.ai/problems/${slug}`, ...extra });
  const now = new Date("2026-10-09T18:30:00Z");
  const time = (d: Date) => d.toISOString().slice(11, 16) + " UTC";
  const gateway = { gateway: true, now, time };
  const standalone = { gateway: false, now, time };

  it("upstream-rejected, request-too-large and provider-not-found get their own messages, not internal-error", () => {
    const rejected = problemView(p("upstream-rejected"), "x", gateway);
    expect(rejected).toMatchObject({ kind: "rejected", text: "The model service turned that question down — try asking it another way.", retry: "button", disableInput: false });
    const tooLarge = problemView(p("request-too-large", { status: 413 }), "x", gateway);
    expect(tooLarge).toMatchObject({ kind: "too-large", text: "That's too long to send — ask a shorter question, or start a new conversation.", retry: null });
    // provider-not-found: like a model not allowed, the panel reloads the picker and moves to another model.
    expect(problemView(p("provider-not-found"), "x", gateway).kind).toBe("model");
    // An older API's error without a problem is read by its code.
    expect(problemOf({ message: "m", extensions: { code: "REQUEST_TOO_LARGE" } })?.type).toBe("https://agentbullpen.ai/problems/request-too-large");
    expect(problemOf({ message: "m", extensions: { code: "UPSTREAM_REJECTED" } })?.type).toBe("https://agentbullpen.ai/problems/upstream-rejected");
    expect(problemOf({ message: "m", extensions: { code: "PROVIDER_NOT_FOUND" } })?.type).toBe("https://agentbullpen.ai/problems/provider-not-found");
  });

  it("budget-exhausted: the day's allowance and when chat comes back, input off until then", () => {
    const v = problemView(p("budget-exhausted", { resetsAtUtc: "2026-10-10T00:00:00Z", dailyAllowanceUsd: 1, usedTodayUsd: 1.000412 }), "x", gateway);
    expect(v.kind).toBe("budget");
    expect(v.text).toBe("You’ve used today’s $1.00. Chat comes back at 00:00 UTC.");
    expect(v.disableInput).toBe(true);
    expect(v.dailyUsd).toBe(1);
    expect(v.until?.toISOString()).toBe("2026-10-10T00:00:00.000Z");
    // Without the site's time: the next UTC midnight. Without its amount: no amount is made up.
    const bare = problemView(p("budget-exhausted"), "x", gateway);
    expect(bare.until?.toISOString()).toBe("2026-10-10T00:00:00.000Z");
    expect(bare.text).toBe("You’ve used today’s allowance. Chat comes back at 00:00 UTC.");
    expect(bare.dailyUsd).toBeUndefined();
  });

  it("budget-exhausted: the input comes back once the reset time has passed", () => {
    const before: Budget = { level: "standard", dailyUsd: 1, remainingUsd: 0, unlimited: false, resetsAtUtc: "2026-10-10T00:00:00Z" };
    expect(allowanceView(before, { now, time })).toMatchObject({ state: "used-up", disableInput: true });
    expect(allowanceView(before, { now, time })!.until!.toISOString()).toBe("2026-10-10T00:00:00.000Z");
    // What the panel's timer sets at the reset time: a new day's allowance.
    const after = allowanceView({ ...before, remainingUsd: before.dailyUsd, resetsAtUtc: null }, { now: new Date("2026-10-10T00:00:01Z"), time });
    expect(after).toMatchObject({ state: "normal", disableInput: false, until: null, left: "$1.00 of $1.00 left today", reset: "Resets 00:00 UTC" });
    expect(nextUtcMidnight(new Date("2026-12-31T23:59:00Z")).toISOString()).toBe("2027-01-01T00:00:00.000Z");
  });

  it("chat-off: its own words, input off, and nothing to wait for", () => {
    const v = problemView(p("chat-off", { level: "off" }), "x", gateway);
    expect(v).toMatchObject({ kind: "chat-off", text: chatOffNotice, disableInput: true, retry: null });
    expect(v.until).toBeUndefined();
    expect(v.text).not.toMatch(/used|comes back/);   // not worded as "used up"
    expect(problemSlug(problemOf({ message: "m", extensions: { code: "CHAT_OFF" } })!)).toBe("chat-off");
  });

  it("rate-limited: 'One moment…' and one automatic retry after retryAfterSeconds, then the second-failure text", () => {
    const first = problemView(p("rate-limited", { retryAfterSeconds: 12 }), "x", { ...gateway, attempt: 0 });
    expect(first).toMatchObject({ kind: "rate-wait", text: rateWaitText, retry: "auto", retryAfterMs: 12000, disableInput: false });
    expect(problemView(p("rate-limited", { retryAfterSeconds: 900 }), "x", gateway).retryAfterMs).toBe(60000);   // capped
    expect(problemView(p("rate-limited"), "x", gateway).retryAfterMs).toBe(5000);                                 // none given
    const second = problemView(p("rate-limited", { retryAfterSeconds: 12 }), "x", { ...gateway, attempt: 1 });
    expect(second).toMatchObject({ kind: "rate", text: rateText, retry: null, disableInput: false });
    expect(rateText).toBe("Too many questions at once — try again in a minute.");
  });

  it("an answer from the fallback model gets a note under it", () => {
    expect(fallbackNote({ requested: "Claude Haiku 5.5", used: "GPT-4o mini" })).toBe("Answered by GPT-4o mini — Claude Haiku 5.5 is unavailable right now.");
  });

  it("provider-unavailable without a fallback: the resting message behind the gateway, never the provider", () => {
    const v = problemView(p("provider-unavailable", { reason: "credits", detail: "The model service is unavailable right now." }), "x", gateway);
    expect(v).toMatchObject({ kind: "unavailable", text: restingText, retry: "button", disableInput: false });
    expect(v.text).toBe("The characters are resting — the model service is unavailable right now. The site owner has been notified. Try again later.");
  });

  it("provider-unavailable standalone: the fix-it text for the person running it (OpenRouter's 402, as the API words it)", () => {
    const detail = "Your OpenRouter account is out of credits — top it up, or run `setup` to switch the chat to another model.";
    const v = problemView(p("provider-unavailable", { reason: "credits", provider: "openrouter", detail }), "x", standalone);
    expect(v).toMatchObject({ kind: "unavailable", text: detail, retry: "button", disableInput: false });
  });

  it("upstream-error and timeout: try again, with a button", () => {
    for (const slug of ["upstream-error", "timeout"])
      expect(problemView(p(slug), "The model call failed (HTTP 500).", gateway)).toMatchObject({ kind: "failed", text: failedText, retry: "button", disableInput: false });
    expect(failedText).toBe("That took too long or failed — try asking again.");
  });

  it("model-not-allowed / unavailable: greyed out in the picker with its time, and the chat moves to the default", () => {
    const models: ChatModel[] = [
      { id: "openrouter:anthropic/claude-haiku-5.5", displayName: "Claude Haiku 5.5", provider: "openrouter", isDefault: true, available: false, unavailableUntilUtc: "2026-10-09T20:00:00Z" },
      { id: "openrouter:openai/gpt-4o-mini", displayName: "GPT-4o mini", provider: "openrouter", isDefault: false },
    ];
    expect(modelOptionLabel(models[0], time)).toBe("Claude Haiku 5.5 — unavailable until 20:00 UTC");
    expect(modelOptionLabel({ ...models[0], unavailableUntilUtc: null }, time)).toBe("Claude Haiku 5.5 — unavailable");
    expect(modelOptionLabel(models[1], time)).toBe("GPT-4o mini");
    expect(fallbackModel(models)?.id).toBe("openrouter:openai/gpt-4o-mini");   // the default is unavailable: the first available
    expect(modelSwitchNote("Claude Haiku 5.5", "GPT-4o mini")).toBe("Claude Haiku 5.5 is unavailable right now — switched to GPT-4o mini. The earlier messages were cleared.");
    expect(problemView(p("model-not-allowed", { model: "x" }), "x", gateway).kind).toBe("model");
  });

  it("unauthorized: the session ended, input off (the panel adds the Sign in link)", () => {
    expect(problemView(p("unauthorized"), "x", gateway)).toMatchObject({ kind: "signed-out", text: signedOutText, disableInput: true });
    expect(signedOutText).toBe("Your session ended — sign in again");
  });

  it("internal-error: something went wrong, with the reference", () => {
    expect(problemView(p("internal-error", { traceId: "4bf92f3577b34da6a3ce929d0e0e4736" }), "x", gateway).text).toBe(
      "Something went wrong (ref 4bf92f3577b34da6a3ce929d0e0e4736).",
    );
  });

  it("anything else is shown as the API wrote it", () => {
    expect(problemView(p("unknown-character", { detail: "Book 'x' has no character 'y'." }), "m", gateway)).toMatchObject({ kind: "other", text: "Book 'x' has no character 'y'." });
    expect(problemView(null, "network down", gateway).text).toBe("network down");
  });

  it("reads extensions.problem, and an older API's code when there is no problem", () => {
    const problem = p("rate-limited", { retryAfterSeconds: 3 });
    expect(problemOf({ message: "m", extensions: { code: "RATE_LIMITED", problem } })).toBe(problem);
    expect(problemSlug(problemOf({ message: "m", extensions: { code: "BUDGET_EXHAUSTED" } })!)).toBe("budget-exhausted");
    expect(problemOf({ message: "m", extensions: { code: "SOMETHING_ELSE" } })).toBeNull();
    expect(problemOf(undefined)).toBeNull();
  });
});

// Each reply's tokens and cost, and today's allowance in dollars (BudgetChat.dc.html: its four states and their sample values).
describe("Tokens and cost per reply", () => {
  it("the line: tokens with thousands separators, then the cost to 4 decimals", () => {
    expect(replyLine({ inputTokens: 1610, outputTokens: 232, costUsd: 0.0004 })).toBe("1,842 tokens · $0.0004");
    expect(replyLine({ inputTokens: 1701, outputTokens: 214, costUsd: 0.000412 })).toBe("1,915 tokens · $0.0004");
    expect(replyLine({ inputTokens: 5020, outputTokens: 42, costUsd: 0.000605 })).toBe("5,062 tokens · $0.0006");
    expect(replyLine({ inputTokens: 120000, outputTokens: 3456, costUsd: 1.23456 })).toBe("123,456 tokens · $1.2346");
  });

  it("a cost under a hundredth of a cent is “<$0.0001”; nothing at all is $0.0000", () => {
    expect(costText(0.0000027)).toBe("<$0.0001");
    expect(costText(0.00009999)).toBe("<$0.0001");
    expect(costText(0.0001)).toBe("$0.0001");
    expect(costText(0)).toBe("$0.0000");
    expect(replyLine({ inputTokens: 10, outputTokens: 5, costUsd: 0.000003 })).toBe("15 tokens · <$0.0001");
  });

  it("no cost from the API: tokens only, never an estimate", () => {
    expect(replyLine({ inputTokens: 3200, outputTokens: 212 })).toBe("3,412 tokens");
    expect(replyLine({ inputTokens: 3200, outputTokens: 212, costUsd: null })).toBe("3,412 tokens");
    expect(replyLine({ inputTokens: 1, outputTokens: 0, costUsd: null })).toBe("1 token");
  });

  it("the tooltip: in, out and the model's display name", () => {
    expect(replyLineTitle({ inputTokens: 1610, outputTokens: 232, costUsd: 0.0004 }, "GPT-4o-mini")).toBe("1,610 in · 232 out · GPT-4o-mini");
    expect(replyLineTitle({ inputTokens: 1610, outputTokens: 232 }, null)).toBe("1,610 in · 232 out");
  });

  it("the queries ask for the cost and the allowance in dollars", () => {
    expect(chatQueries.chat).toContain("usage { inputTokens outputTokens costUsd }");
    for (const q of [chatQueries.chat, chatQueries.models, chatQueries.budget]) expect(q).toContain("budget { level dailyUsd remainingUsd unlimited resetsAtUtc }");
    expect(chatQueries.chat).not.toMatch(/budget \{ daily /);
  });
});

describe("Today's allowance under the input", () => {
  const now = new Date("2026-10-10T15:00:00Z");
  const time = () => "6:00 PM";
  const at = (remainingUsd: number | null, extra: Partial<Budget> = {}): Budget =>
    ({ level: "standard", dailyUsd: 1, remainingUsd, unlimited: false, resetsAtUtc: "2026-10-11T00:00:00Z", ...extra });
  const view = (b: Budget | null) => allowanceView(b, { now, time });

  it("normal: what is left of the day's allowance, when it resets, the meter in the accent", () => {
    expect(view(at(0.98))).toEqual({ state: "normal", left: "$0.98 of $1.00 left today", reset: "Resets 6:00 PM", percent: 98, notice: null, disableInput: false, until: null });
    expect(view(at(1))).toMatchObject({ state: "normal", left: "$1.00 of $1.00 left today", percent: 100 });
    expect(view(at(0.1))).toMatchObject({ state: "normal", percent: 10 });   // 10 % is not yet low
  });

  it("running low: under 10 % left, the warning colour", () => {
    expect(view(at(0.06))).toMatchObject({ state: "low", left: "$0.06 of $1.00 left today", reset: "Resets 6:00 PM", percent: 6, notice: null, disableInput: false });
    expect(view(at(0.0999))).toMatchObject({ state: "low", left: "$0.09 of $1.00 left today" });
    expect(view(at(0.019, { level: "generous", dailyUsd: 2 }))).toMatchObject({ state: "low", left: "$0.01 of $2.00 left today", percent: 1 });
  });

  it("what is left is never rounded up, and a remainder under a cent is not shown as nothing", () => {
    expect(leftText(0.9994)).toBe("$0.99");
    expect(leftText(0.29)).toBe("$0.29");   // not $0.28: 0.29 * 100 is 28.999… in binary
    expect(leftText(0.004)).toBe("<$0.01");
    expect(leftText(0)).toBe("$0.00");
    expect(dollars(0.5)).toBe("$0.50");
    expect(view(at(0.004))).toMatchObject({ state: "low", left: "<$0.01 of $1.00 left today", percent: 1, disableInput: false });
  });

  it("used up: the notice with the day's allowance and the local reset time; input and Send off until then", () => {
    const v = view(at(0))!;
    expect(v).toMatchObject({ state: "used-up", left: "$0.00 of $1.00 left today", reset: "Resets 6:00 PM", percent: 0, disableInput: true });
    expect(v.notice).toBe("You’ve used today’s $1.00. Chat comes back at 6:00 PM.");
    expect(v.until!.toISOString()).toBe("2026-10-11T00:00:00.000Z");
    expect(view(at(0, { level: "custom", dailyUsd: 0.01 }))!.notice).toBe("You’ve used today’s $0.01. Chat comes back at 6:00 PM.");
    // Without the site's reset time: the next UTC midnight.
    expect(view(at(0, { resetsAtUtc: null }))!.until!.toISOString()).toBe("2026-10-11T00:00:00.000Z");
  });

  it("off: its own notice and footer, no reset time, input off, nothing to wait for", () => {
    expect(view({ level: "off", dailyUsd: null, remainingUsd: 0, unlimited: false, resetsAtUtc: null })).toEqual({
      state: "off",
      left: chatOffLine,
      reset: null,
      percent: 0,
      notice: "Chat isn’t turned on for your account yet. The graph and everything else still work.",
      disableInput: true,
      until: null,
    });
    expect(chatOffLine).toBe("Chat is off for your account");
  });

  it("no limit, standalone, or no headers: no footer", () => {
    expect(view({ level: "none", dailyUsd: null, remainingUsd: null, unlimited: true, resetsAtUtc: null })).toBeNull();
    expect(view({ level: null, dailyUsd: null, remainingUsd: null, unlimited: true })).toBeNull();   // standalone
    expect(view(null)).toBeNull();
    expect(allowanceView(undefined)).toBeNull();
  });

  it("the local time of the reset comes from the browser's clock by default", () => {
    const v = allowanceView(at(0.5), { now })!;
    expect(v.reset).toMatch(/^Resets .*\d/);
  });
});

describe("The allowance is read again when the tab gets attention", () => {
  it("on window focus, and when the page is shown again; not when it is hidden; not after stop", () => {
    const win = new EventTarget();
    const doc = Object.assign(new EventTarget(), { visibilityState: "visible" });
    let reads = 0;
    const stop = onAttention(() => reads++, win, doc);
    win.dispatchEvent(new Event("focus"));
    expect(reads).toBe(1);
    doc.visibilityState = "hidden";
    doc.dispatchEvent(new Event("visibilitychange"));
    expect(reads).toBe(1);
    doc.visibilityState = "visible";
    doc.dispatchEvent(new Event("visibilitychange"));
    expect(reads).toBe(2);
    stop();
    win.dispatchEvent(new Event("focus"));
    doc.dispatchEvent(new Event("visibilitychange"));
    expect(reads).toBe(2);
  });
});
