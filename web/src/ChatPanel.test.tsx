// @vitest-environment jsdom
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Budget, ChatCharacter, ChatResult } from "./chat.ts";

// The chat panel itself (BudgetChat.dc.html). Under each reply its tokens and cost; behind the site, today's allowance under
// the input in its four states (normal, running low, used up, off) and none for "No limit"; the allowance read again when the tab
// gets the focus back; standalone has no footer and reads nothing on focus. The API is played: each query's answer is set per test.

const api = vi.hoisted(() => ({
  gateway: true,
  budget: null as unknown,
  /** The answer to the next allowance-only read (the focus re-read); falls back to `budget`. */
  budgetNext: undefined as unknown,
  chat: null as null | (() => unknown),
  calls: [] as string[],
}));

vi.mock("./graphql.ts", async (original) => {
  const real = await original<typeof import("./graphql.ts")>();
  const models = [
    { id: "openai:gpt-4o-mini", displayName: "OpenAI: GPT-4o-mini", provider: "openai", isDefault: true, available: true, unavailableUntilUtc: null },
    { id: "openai:gpt-4o", displayName: "OpenAI: GPT-4o", provider: "openai", isDefault: false, available: true, unavailableUntilUtc: null },
  ];
  const gql = async (query: string) => {
    if (query.includes("chatModels")) {
      api.calls.push("models");
      return { chatModels: api.gateway ? models : [models[0]], budget: api.budget };
    }
    if (query.includes("chat(input")) {
      api.calls.push("chat");
      const answer = api.chat!();
      if (answer instanceof Error) throw answer;
      return { chat: answer };
    }
    if (query.trim().startsWith("{ budget")) {
      api.calls.push("budget");
      const b = api.budgetNext === undefined ? api.budget : api.budgetNext;
      return { budget: b };
    }
    throw new Error(`unexpected query: ${query}`);
  };
  return {
    ...real,
    gql,
    get gatewayMode() {
      return api.gateway;
    },
  };
});

const jim: ChatCharacter = { name: "Jim Hawkins", displayName: "Jim Hawkins", initials: "JH", nodeId: 1, avatar: null };
const resets = "2026-10-11T00:00:00Z";
const standard = (remainingUsd: number, extra: Partial<Budget> = {}): Budget => ({ level: "standard", dailyUsd: 1, remainingUsd, unlimited: false, resetsAtUtc: resets, ...extra });
const off: Budget = { level: "off", dailyUsd: null, remainingUsd: 0, unlimited: false, resetsAtUtc: null };
const noLimit: Budget = { level: "none", dailyUsd: null, remainingUsd: null, unlimited: true, resetsAtUtc: null };
const local: Budget = { level: null, dailyUsd: null, remainingUsd: null, unlimited: true, resetsAtUtc: null };
const reply = (extra: Partial<ChatResult> = {}): ChatResult => ({
  sessionId: "s1",
  answer: "The captain? Rough as a gale and twice as loud.",
  toolCalls: [],
  lateRowsSuppressed: 0,
  usage: { inputTokens: 5020, outputTokens: 42, costUsd: 0.000605 },
  model: "openai/gpt-4o-mini",
  budget: standard(0.979395),
  fallback: null,
  ...extra,
});

let host: HTMLDivElement;
let root: Root;

const wait = (ms = 5) => act(() => new Promise<void>((done) => setTimeout(done, ms)));
async function until(what: () => boolean, name: string) {
  // By the clock, not a count of waits: a busy machine stretches each wait.
  for (const end = Date.now() + 4000; Date.now() < end && !what(); ) await wait();
  if (!what()) throw new Error(`never happened: ${name}\n${host.textContent}`);
}
const text = () => host.textContent ?? "";
const footer = () => host.querySelector<HTMLElement>("[aria-label=\"Today's allowance\"]");
const meter = () => host.querySelector<HTMLElement>("[role='meter']");
const notice = () => [...host.querySelectorAll("[role='status']")].map((n) => n.textContent).join(" | ");
const input = () => host.querySelector<HTMLInputElement>("input[aria-label='Message']")!;
const button = (name: string) => [...host.querySelectorAll("button")].find((b) => b.textContent?.trim().startsWith(name))!;
const click = (el: Element) => act(async () => el.dispatchEvent(new MouseEvent("click", { bubbles: true })));
const line = () => host.querySelector<HTMLElement>("[aria-label='Tokens and cost of this reply']");

async function open(budget: unknown, gateway = true) {
  Object.assign(api, { gateway, budget, budgetNext: undefined, chat: null, calls: [] });
  const { default: ChatPanel } = await import("./ChatPanel.tsx");
  await act(async () => root.render(<ChatPanel bookId="treasure-island" character={jim} chapter={9} spoilers={false} available onClose={() => {}} />));
  await until(() => api.calls.includes("models"), "the models and the allowance were read");
  await wait();
}

beforeEach(() => {
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  Element.prototype.scrollTo = () => {};
  host = document.body.appendChild(document.createElement("div"));
  root = createRoot(host);
});

afterEach(async () => {
  await act(async () => root.unmount());
  host.remove();
  vi.resetModules();
});

describe("Tokens and cost under each reply", () => {
  it("gateway: the turn's tokens and the site's cost, with the split and the model's name in the tooltip", async () => {
    await open(standard(0.98));
    api.chat = () => reply();
    await click(button("Try:"));
    await until(() => line() !== null, "the reply");

    expect(line()!.textContent).toBe("5,062 tokens · $0.0006");
    expect(line()!.title).toBe("5,020 in · 42 out · OpenAI: GPT-4o-mini");
    // Directly under the reply's bubble, before the lookups.
    expect(line()!.previousElementSibling!.textContent).toBe("The captain? Rough as a gale and twice as loud.");
    expect(line()!.nextElementSibling!.textContent).toContain("What I looked up");
    expect(host.querySelector("details summary")!.textContent).not.toMatch(/tokens/);   // said once
    // The allowance after the turn, from the same answer.
    expect(footer()!.textContent).toContain("$0.97 of $1.00 left today");
  });

  it("an answer from the site's fallback model names that model in the tooltip", async () => {
    await open(standard(0.98));
    api.chat = () => reply({ fallback: { requested: "OpenAI: GPT-4o-mini", used: "Claude Haiku 5.5" } });
    await click(button("Try:"));
    await until(() => line() !== null, "the reply");
    expect(line()!.title).toBe("5,020 in · 42 out · Claude Haiku 5.5");
  });

  it("a tiny cost reads “<$0.0001”; a turn the API has no cost for shows tokens only", async () => {
    await open(standard(0.98));
    api.chat = () => reply({ usage: { inputTokens: 15, outputTokens: 5, costUsd: 0.0000027 } });
    await click(button("Try:"));
    await until(() => line() !== null, "the reply");
    expect(line()!.textContent).toBe("20 tokens · <$0.0001");

    api.chat = () => reply({ usage: { inputTokens: 1610, outputTokens: 232, costUsd: null } });
    await click(button("Try:"));
    await until(() => host.querySelectorAll("[aria-label='Tokens and cost of this reply']").length === 2, "the second reply");
    expect([...host.querySelectorAll("[aria-label='Tokens and cost of this reply']")].at(-1)!.textContent).toBe("1,842 tokens");
  });

  it("standalone: tokens always; the cost when the provider sent one (OpenRouter), none otherwise; no footer", async () => {
    await open(local, false);
    api.chat = () => reply({ budget: local, usage: { inputTokens: 1610, outputTokens: 232, costUsd: null } });
    await click(button("Try:"));
    await until(() => line() !== null, "the reply");
    expect(line()!.textContent).toBe("1,842 tokens");
    expect(line()!.title).toBe("1,610 in · 232 out · OpenAI: GPT-4o-mini");

    api.chat = () => reply({ budget: local, usage: { inputTokens: 5020, outputTokens: 42, costUsd: 0.000605 } });
    await click(button("Try:"));
    await until(() => host.querySelectorAll("[aria-label='Tokens and cost of this reply']").length === 2, "the second reply");
    expect([...host.querySelectorAll("[aria-label='Tokens and cost of this reply']")].at(-1)!.textContent).toBe("5,062 tokens · $0.0006");
    expect(footer()).toBeNull();
    expect(meter()).toBeNull();
    expect(text()).not.toMatch(/left today|Resets|allowance/i);
  });
});

describe("Today's allowance under the input (gateway)", () => {
  it("normal: what is left of the day, the local reset time, the meter in the accent", async () => {
    await open(standard(0.98));
    expect(footer()!.textContent).toMatch(/^\$0\.98 of \$1\.00 left todayResets .*\d/);
    expect(meter()!.getAttribute("aria-valuenow")).toBe("98");
    expect(meter()!.getAttribute("aria-label")).toBe("Today's allowance left");
    const bar = meter()!.firstElementChild as HTMLElement;
    expect(bar.style.width).toBe("98%");
    expect(bar.className).toContain("bg-accent");
    expect(bar.className).not.toContain("bg-warning");
    expect(notice()).toBe("");
    expect(input().disabled).toBe(false);
    expect(text()).not.toMatch(/tokens left today/);   // the token footer is gone
  });

  it("running low: under 10 % the meter takes the kit's warning colour; chat still works", async () => {
    await open(standard(0.06));
    expect(footer()!.textContent).toContain("$0.06 of $1.00 left today");
    expect(meter()!.getAttribute("aria-valuenow")).toBe("6");
    const bar = meter()!.firstElementChild as HTMLElement;
    expect(bar.style.width).toBe("6%");
    expect(bar.className).toContain("bg-warning");
    expect(input().disabled).toBe(false);
    expect(notice()).toBe("");
  });

  it("used up: the notice, the footer at $0.00, input and Send off", async () => {
    await open(standard(0));
    expect(notice()).toMatch(/^You’ve used today’s \$1\.00\. Chat comes back at .*\d.*\.$/);
    expect(footer()!.textContent).toContain("$0.00 of $1.00 left today");
    expect(footer()!.textContent).toContain("Resets ");
    expect(meter()!.getAttribute("aria-valuenow")).toBe("0");
    expect(input().disabled).toBe(true);
    expect(button("Send").disabled).toBe(true);
    expect(button("Try:").disabled).toBe(true);
  });

  it("off: its own notice and footer, no reset time, input off", async () => {
    await open(off);
    expect(notice()).toBe("Chat isn’t turned on for your account yet. The graph and everything else still work.");
    expect(footer()!.textContent).toBe("Chat is off for your account");
    expect(text()).not.toMatch(/Resets|comes back|used today/);
    expect(meter()!.getAttribute("aria-valuenow")).toBe("0");
    expect(input().disabled).toBe(true);
    expect(button("Send").disabled).toBe(true);
  });

  it("no limit: no footer, and chat works", async () => {
    await open(noLimit);
    expect(footer()).toBeNull();
    expect(meter()).toBeNull();
    expect(notice()).toBe("");
    expect(input().disabled).toBe(false);
  });

  it("the site refusing a turn as used up or as off puts the panel in that state, with no error bubble", async () => {
    const { GraphQlRequestError } = await import("./graphql.ts");
    const problem = (slug: string, extra: object) =>
      new GraphQlRequestError([{ message: "refused", extensions: { code: "X", problem: { type: `https://agentbullpen.ai/problems/${slug}`, ...extra } } }]);

    await open(standard(0.0004));
    api.chat = () => problem("budget-exhausted", { dailyAllowanceUsd: 1, usedTodayUsd: 1.000205, resetsAtUtc: resets });
    await click(button("Try:"));
    await until(() => input().disabled, "the used-up state");
    expect(notice()).toMatch(/^You’ve used today’s \$1\.00\. Chat comes back at /);
    expect(footer()!.textContent).toContain("$0.00 of $1.00 left today");
    expect(host.querySelector("[role='alert']")).toBeNull();

    await act(async () => root.unmount());
    root = createRoot(host);
    await open(standard(0.5));
    api.chat = () => problem("chat-off", { level: "off" });
    await click(button("Try:"));
    await until(() => input().disabled, "the off state");
    expect(notice()).toBe("Chat isn’t turned on for your account yet. The graph and everything else still work.");
    expect(footer()!.textContent).toBe("Chat is off for your account");
    expect(notice()).not.toMatch(/used/);
    expect(host.querySelector("[role='alert']")).toBeNull();
  });
});

describe("The allowance is read again when the tab gets the focus back", () => {
  it("a level changed on the site shows without a reload: used up → Generous, then → off", async () => {
    await open(standard(0));
    expect(input().disabled).toBe(true);
    expect(api.calls).toEqual(["models"]);   // read once, when the panel opened

    api.budgetNext = { level: "generous", dailyUsd: 2, remainingUsd: 1, unlimited: false, resetsAtUtc: resets };
    await act(async () => void window.dispatchEvent(new Event("focus")));
    await until(() => !input().disabled, "the input came back");
    expect(api.calls).toEqual(["models", "budget"]);
    expect(footer()!.textContent).toContain("$1.00 of $2.00 left today");
    expect(notice()).toBe("");

    // Focus and visibility arriving together are one read.
    await act(async () => {
      window.dispatchEvent(new Event("focus"));
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await wait(20);
    expect(api.calls).toEqual(["models", "budget"]);

    await wait(1050);
    api.budgetNext = off;
    await act(async () => void document.dispatchEvent(new Event("visibilitychange")));
    await until(() => input().disabled, "chat went off");
    expect(footer()!.textContent).toBe("Chat is off for your account");
    expect(api.calls).toEqual(["models", "budget", "budget"]);
  });

  it("after the panel closes, focus reads nothing", async () => {
    await open(standard(0.5));
    await act(async () => root.render(<div />));
    await act(async () => void window.dispatchEvent(new Event("focus")));
    await wait(20);
    expect(api.calls).toEqual(["models"]);
  });

  it("standalone: focus reads nothing (there is no allowance)", async () => {
    await open(local, false);
    await act(async () => {
      window.dispatchEvent(new Event("focus"));
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await wait(20);
    expect(api.calls).toEqual(["models"]);
    expect(footer()).toBeNull();
  });
});

// The error bubble has the reply bubble's shape with a border in the kit's error colour and white text, and every line under a reply sits at the bubble's
// left edge: 12px, muted, 4px apart (no indent).
describe("The error bubble and the lines under a reply", () => {
  it("an error bubble under a normal reply: the reply's shape, error border, white text, Try again kept", async () => {
    const { GraphQlRequestError } = await import("./graphql.ts");
    await open(standard(0.98));
    api.chat = () => reply({ fallback: { requested: "OpenAI: GPT-4o-mini", used: "Claude Haiku 5.5" } });
    await click(button("Try:"));
    await until(() => line() !== null, "the reply");
    api.chat = () => new GraphQlRequestError([{ message: "upstream", extensions: { code: "LLM_ERROR", problem: { type: "https://agentbullpen.ai/problems/upstream-error" } } }]);
    await click(button("Try:"));
    await until(() => host.querySelector("[role='alert']") !== null, "the error bubble");

    const error = host.querySelector<HTMLElement>("[role='alert']")!;
    expect(error.textContent).toContain("That took too long or failed — try asking again.");
    const cls = error.className.split(/\s+/);
    for (const c of ["max-w-[88%]", "self-start", "rounded-12", "px-12", "py-10", "text-14", "leading-[1.45]", "border", "border-error", "text-fg"]) expect(cls, c).toContain(c);
    for (const old of ["max-w-[85%]", "rounded-14", "px-14", "py-12", "text-15", "leading-[1.5]", "border-divider", "text-error"]) expect(cls, old).not.toContain(old);
    expect(button("Try again")).toBeTruthy();

    // The reply above it: the same shape, and the same 88 % limit applied the same way (on the bubble itself).
    const bubble = line()!.previousElementSibling as HTMLElement;
    const shape = (el: HTMLElement) => el.className.split(/\s+/).filter((c) => /^(max-w-|rounded-|px-|py-|text-14|leading-)/.test(c)).sort();
    expect(shape(error)).toEqual(shape(bubble));
  });

  it("the tokens line, “Answered by …” and “What I looked up”: 12px, muted, no indent, 4px apart", async () => {
    await open(standard(0.98));
    api.chat = () => reply({ fallback: { requested: "OpenAI: GPT-4o-mini", used: "Claude Haiku 5.5" } });
    await click(button("Try:"));
    await until(() => line() !== null, "the reply");

    const wrapper = line()!.parentElement!;
    expect(wrapper.className.split(/\s+/)).toContain("gap-4");
    const [bubble, tokens, answeredBy, lookups] = [...wrapper.children] as HTMLElement[];
    expect(bubble.textContent).toContain("The captain?");
    expect(answeredBy.textContent).toBe("Answered by Claude Haiku 5.5 — OpenAI: GPT-4o-mini is unavailable right now.");
    expect(lookups.getAttribute("aria-label")).toBe("What I looked up");
    for (const el of [tokens, answeredBy, lookups]) {
      const cls = el.className.split(/\s+/);
      expect(cls, el.textContent ?? "").toEqual(expect.arrayContaining(["text-12", "text-fg-muted"]));
      expect(cls.filter((c) => /^(px-|pl-|ml-|-mt-|mt-|text-13)/.test(c)), el.textContent ?? "").toEqual([]);
    }
  });
});
