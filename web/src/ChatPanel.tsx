import { useEffect, useRef, useState } from "react";
import { Avatar, Input, Select, makeStyles, tokens } from "@fluentui/react-components";
import { KitButton } from "./design-kit/components/KitButton.tsx";
import { GraphQlRequestError, SignedOutError, gatewayMode, gql, siteUrl } from "./graphql.ts";
import {
  allowanceView,
  chatQueries,
  fallbackModel,
  fallbackNote,
  lookupsDetail,
  lookupsSummary,
  modelLabel,
  modelLabelOf,
  modelOptionLabel,
  modelSwitchNote,
  newConversationNote,
  newModelNote,
  noModelNote,
  onAttention,
  problemOf,
  problemView,
  replyLine,
  replyLineTitle,
  showModelPicker,
  signInUrl,
  signedOutText,
  type Budget,
  type ChatCharacter,
  type ChatModel,
  type ChatResult,
} from "./chat.ts";

// The chat window, ported from the site's prototype (the section that slides in
// when a character is clicked), on the real `chat` mutation. Controls ported from the prototype's controls.tsx (Bubbles,
// ChatInput, TryPrompt). Under each answer a collapsible "What I looked up" line; changing chapter, character or
// spoilers starts a new conversation (the parent remounts nothing: the session id and messages are reset here).
// A model picker in the header when there is more than one model (another model starts a new conversation too); behind the
// AgentBullpen.AI gateway, the tokens left today under the input, an out-of-budget state, and a "Sign in" link when signed out.
// Every failure is a problem (docs/design/models.md) with its own state: the budget's reset time (the input comes back by
// itself), one quiet retry after a rate limit, a Try again button when the model service is down or a turn failed, a note under an
// answer from the site's fallback model, unavailable models greyed out in the picker, and the session-ended state.
// Under each reply its tokens and cost; behind the site, today's allowance in dollars under the input
// (what is left, when it resets, a thin meter), a notice when it is used up or chat is off for the account, and the allowance read
// again whenever the tab gets the focus back (it may have been changed on the site meanwhile).

const borderColors = (c: string) => ({ borderTopColor: c, borderRightColor: c, borderBottomColor: c, borderLeftColor: c });

const useStyles = makeStyles({
  // Chat input: 44px, 0 12px, radius 8, outline border, panel, 15px (prototype controls.tsx).
  input: {
    flexGrow: 1,
    minWidth: 0,
    minHeight: "44px",
    boxSizing: "border-box",
    padding: "0 12px",
    borderRadius: "8px",
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground2,
    ":hover": borderColors(tokens.colorNeutralStroke1),
    ":focus-within": borderColors(tokens.colorNeutralStroke1),
    "& input": { padding: 0, fontFamily: tokens.fontFamilyBase, fontSize: "15px", color: tokens.colorNeutralForeground1 },
  },
  // The prototype's 40px accent circle with the initials.
  avatar: {
    width: "40px",
    height: "40px",
    "& .fui-Avatar__initials": { backgroundColor: "var(--abp-accent)", color: "var(--abp-on-accent)", fontWeight: 700, fontSize: "15px" },
  },
});

/** error.retry: the message a "Try again" button asks again. */
type Msg =
  | { kind: "mine"; text: string }
  | { kind: "answer"; result: ChatResult; model?: string }
  | { kind: "error"; text: string; retry?: string }
  | { kind: "note"; text: string };

/** Bubbles (BudgetChat.dc.html): max 88% of the row for the text (as the mockup, the padding comes on top), 10px 12px,
 * radius 12, 14px / 1.45. A reply = the panel surface + white text, left; mine = accent + black text, right. The error
 * bubble has the reply's shape, with its border in the kit's error colour and white text; the lines under a reply (tokens, "Answered
 * by …", "What I looked up") are 12px, muted, 4px apart, at the bubble's left edge. */
/** `clear`: shown on the latest answer while its rows are lit on the graph ("Clear" lives in the "What I looked up" line). */
function Bubble({ msg, clear, retry }: { msg: Msg; clear?: () => void; retry?: (message: string) => void }) {
  if (msg.kind === "note") return <div className="self-center px-8 text-center text-13 text-fg-muted">{msg.text}</div>;
  if (msg.kind === "mine") return <div className="max-w-[88%] self-end rounded-12 bg-accent px-12 py-10 text-14 leading-[1.45] text-on-accent">{msg.text}</div>;
  if (msg.kind === "error")
    return (
      <div role="alert" className="flex max-w-[88%] flex-col items-start gap-8 self-start rounded-12 border border-error px-12 py-10 text-14 leading-[1.45] text-fg">
        <span className="whitespace-pre-wrap">{msg.text}</span>
        {msg.retry && retry && (
          <KitButton variant="outline" size="sm" onClick={() => retry(msg.retry!)} style={{ minHeight: "32px", padding: "0 12px", borderRadius: "8px", fontSize: "14px" }}>
            Try again
          </KitButton>
        )}
      </div>
    );
  const r = msg.result;
  return (
    <div className="flex flex-col items-start gap-4">
      <div className="max-w-[88%] rounded-12 bg-panel px-12 py-10 text-14 leading-[1.45] whitespace-pre-wrap text-fg">{r.answer}</div>
      {/* Tokens and cost of this reply (mockup: 12px, secondary, 4px under the bubble at its left edge; the split and the
          model in the tooltip). */}
      <span title={replyLineTitle(r.usage, msg.model)} aria-label="Tokens and cost of this reply" className="text-12 text-fg-muted">
        {replyLine(r.usage)}
      </span>
      {r.fallback && <div className="text-12 text-fg-muted">{fallbackNote(r.fallback)}</div>}
      <details className="text-12 text-fg-muted" aria-label="What I looked up">
        <summary className="cursor-pointer">
          {lookupsSummary(r)}
          {clear && (
            <>
              {" · "}
              <button
                type="button"
                onClick={(e) => {
                  e.preventDefault();
                  clear();
                }}
                className="cursor-pointer border-0 bg-transparent p-0 font-sans text-12 text-accent underline"
              >
                Clear
              </button>
              <span className="sr-only"> the graph highlight</span>
            </>
          )}
        </summary>
        <ul className="m-0 mt-6 flex list-none flex-col gap-4 p-0 font-mono text-12">
          {lookupsDetail(r).map((line, i) => (
            <li key={i}>{line}</li>
          ))}
        </ul>
      </details>
    </div>
  );
}

export default function ChatPanel({
  bookId,
  character,
  chapter,
  spoilers,
  available,
  onClose,
  onAnswer,
  highlightActive,
  onClearHighlight,
}: {
  bookId: string;
  character: ChatCharacter;
  chapter: number;
  spoilers: boolean;
  available: boolean | null;
  onClose: () => void;
  /** Each answer's result, whose tool calls' ids light up the graph. */
  onAnswer?: (result: ChatResult) => void;
  /** True while the latest answer's rows are lit on the graph. */
  highlightActive?: boolean;
  onClearHighlight?: () => void;
}) {
  const s = useStyles();
  const [msgs, setMsgs] = useState<Msg[]>([]);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [models, setModels] = useState<ChatModel[]>([]);
  const [model, setModel] = useState<string | null>(null);
  const [budget, setBudget] = useState<Budget | null>(null);
  const [signedOut, setSignedOut] = useState(false);
  const list = useRef<HTMLDivElement>(null);
  // The pending second try after a rate limit, and the note for a switch off an unavailable model.
  const retryTimer = useRef<number | null>(null);
  const switchNote = useRef<string | null>(null);

  // The models to pick from and the budget, once chat is available. An unavailable current model moves to the default.
  // refused: the API just said this model isn't allowed, so move off it even if the list still offers it.
  const loadModels = (current: string | null, refused = false) =>
    gql<{ chatModels: ChatModel[]; budget: Budget | null }>(chatQueries.models)
      .then((d) => {
        setModels(d.chatModels);
        setBudget(d.budget);
        const now = d.chatModels.find((x) => x.id === current);
        const to = fallbackModel(refused ? d.chatModels.filter((x) => x.id !== current) : d.chatModels);
        if (!refused && now && now.available !== false) return;
        if (!to) {
          // No model to move to: keep the picker as it is and say so.
          if (refused) setMsgs((prev) => [...prev, { kind: "error", text: "That model isn't available right now, and no other model is. Try again later." }]);
          if (!current) setModel(d.chatModels.find((x) => x.isDefault)?.id ?? null);
          return;
        }
        if (current && to.id !== current) switchNote.current = modelSwitchNote(modelLabelOf(d.chatModels, current), modelLabel(to));
        setModel(to.id);
      })
      .catch((e: Error) => {
        if (e instanceof SignedOutError) setSignedOut(true);
      });
  useEffect(() => {
    if (available !== true) return;
    void loadModels(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [available]);

  // The allowance again (it is read with the models when the panel opens) whenever the tab gets the focus back: an admin may
  // have changed the level on the site meanwhile. Behind the site only; standalone has no allowance to read.
  const loadBudget = () =>
    gql<{ budget: Budget | null }>(chatQueries.budget)
      .then((d) => setBudget(d.budget))
      .catch((e: Error) => {
        if (e instanceof SignedOutError) setSignedOut(true);
      });
  useEffect(() => {
    if (!gatewayMode || available !== true) return;
    let last = 0;
    return onAttention(() => {
      // Focus and visibility often arrive together: one read.
      if (Date.now() - last < 1000) return;
      last = Date.now();
      void loadBudget();
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [available]);

  // Chapter, character or spoilers changed: a new conversation (the server would start a new session anyway). The previous key,
  // not a "first run" flag: StrictMode runs effects twice on mount, which must not count as a change.
  const key = `${character.name}|${chapter}|${spoilers}`;
  // The same for the model; its first value (the default, once the list has loaded) is not a change.
  const modelKey = useRef(model);
  const shownKey = useRef(key);
  useEffect(() => {
    if (shownKey.current === key) return;
    shownKey.current = key;
    if (retryTimer.current !== null) window.clearTimeout(retryTimer.current);
    setSessionId(null);
    setMsgs([{ kind: "note", text: newConversationNote(character.displayName, chapter, spoilers) }]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  useEffect(() => {
    const before = modelKey.current;
    modelKey.current = model;
    if (before === null || before === model) return;
    setSessionId(null);
    const note = switchNote.current ?? newModelNote(character.displayName, modelLabelOf(models, model));
    switchNote.current = null;
    setMsgs([{ kind: "note", text: note }]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model]);

  // A block body: newer browsers return a Promise from scrollTo, and an effect may only return a cleanup function.
  // Also when the allowance changes, so its notice at the foot of the messages is in view.
  useEffect(() => {
    list.current?.scrollTo({ top: list.current.scrollHeight });
  }, [msgs, budget]);

  // Today's allowance (null: nothing to show). Used up or off: the input and Send are off.
  const allowance = allowanceView(budget);
  const exhausted = allowance?.disableInput === true;
  const resetsAt = allowance?.until?.getTime() ?? null;
  // The input comes back by itself when the allowance resets (a new day's allowance, then the site's own figure). Chat that is
  // off has no reset time, so no timer.
  useEffect(() => {
    if (resetsAt === null) return;
    const wait = Math.min(Math.max(0, resetsAt - Date.now()) + 1000, 2 ** 31 - 1);
    const t = window.setTimeout(() => {
      setBudget((b) => (b ? { ...b, remainingUsd: b.dailyUsd, resetsAtUtc: null } : b));
      if (gatewayMode) void loadBudget();
    }, wait);
    return () => window.clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetsAt]);
  useEffect(() => () => {
    if (retryTimer.current !== null) window.clearTimeout(retryTimer.current);
  }, []);

  /** One turn. attempt 1 is the panel's own second try after a rate limit (the reader's message is already shown). */
  const ask = (m: string, attempt: number) => {
    setSending(true);
    let waiting = false;
    gql<{ chat: ChatResult }>(chatQueries.chat, { input: { bookId, chapter, character: character.name, spoilers, sessionId, message: m, model } })
      .then((d) => {
        setSessionId(d.chat.sessionId);
        // The model that answered, by its label (the site's fallback model when it stepped in), for the reply line's tooltip.
        setMsgs((prev) => [...prev, { kind: "answer", result: d.chat, model: d.chat.fallback?.used ?? modelLabelOf(models, model) }]);
        if (d.chat.budget) setBudget(d.chat.budget);
        onAnswer?.(d.chat);
      })
      .catch((e: Error) => {
        // Signed out, the allowance used up and chat being off are states of the panel (a notice, the input off), not chat bubbles.
        if (e instanceof SignedOutError) return setSignedOut(true);
        const first = e instanceof GraphQlRequestError ? e.errors[0] : undefined;
        const view = problemView(problemOf(first), e.message, { gateway: gatewayMode, attempt });
        switch (view.kind) {
          case "signed-out":
            return setSignedOut(true);
          case "budget":
            return setBudget((b) => ({
              level: b?.level ?? null,
              dailyUsd: view.dailyUsd ?? b?.dailyUsd ?? null,
              remainingUsd: 0,
              unlimited: false,
              resetsAtUtc: view.until!.toISOString(),
            }));
          case "chat-off":
            return setBudget({ level: "off", dailyUsd: null, remainingUsd: 0, unlimited: false, resetsAtUtc: null });
          case "rate-wait":
            waiting = true;
            setMsgs((prev) => [...prev, { kind: "note", text: view.text }]);
            retryTimer.current = window.setTimeout(() => ask(m, 1), view.retryAfterMs);
            return;
          case "model":
            return void loadModels(model, true);
          default:
            setMsgs((prev) => [...prev, { kind: "error", text: view.text, retry: view.retry === "button" ? m : undefined }]);
        }
      })
      .finally(() => {
        if (!waiting) setSending(false);
      });
  };

  const send = (message: string) => {
    const m = message.trim();
    if (!m || sending || !available || exhausted || signedOut) return;
    setText("");
    setMsgs((prev) => [...prev, { kind: "mine", text: m }]);
    ask(m, 0);
  };

  const retry = (m: string) => {
    if (sending || !available || exhausted || signedOut) return;
    ask(m, 0);
  };

  const disabled = available !== true || exhausted || signedOut;
  const signIn = signInUrl(siteUrl);
  const lastAnswer = msgs.map((m) => m.kind).lastIndexOf("answer");
  const tryText = "What do you know so far?";

  return (
    <section aria-label="Chat" className="flex min-h-0 w-360 shrink-0 flex-col border-l border-divider">
      <div className="flex shrink-0 items-center gap-12 border-b border-divider p-16">
        {/* The character's avatar when there is one; Fluent shows the initials if the image fails. */}
        <Avatar
          initials={character.initials}
          image={character.avatar ? { src: `/${character.avatar}` } : undefined}
          name={character.displayName}
          className={s.avatar}
          aria-hidden
        />
        <div className="flex min-w-0 grow flex-col">
          <span className="text-16 font-bold">{character.displayName}</span>
          {/* The help text. The character gate holds in both modes. */}
          <span
            className="text-13 text-fg-muted"
            title={`${character.displayName} answers only from what ${character.displayName} has seen, been told or worked out by this point. Spoilers moves the story's clock, not who knows what: with it on, a character still does not know what only others have learned.`}
          >
            Knows up to chapter {chapter + (spoilers ? " · spoilers on" : " · only what the reader knows")}
          </span>
          {showModelPicker(models) && model && (
            <Select aria-label="Model" size="small" value={model} disabled={sending} onChange={(_, d) => setModel(d.value)} className="mt-4 max-w-[220px]">
              {models.map((m) => (
                <option key={m.id} value={m.id} disabled={m.available === false}>
                  {modelOptionLabel(m)}
                </option>
              ))}
            </Select>
          )}
        </div>
        <KitButton
          variant="text"
          size="sm"
          aria-label="Close chat"
          onClick={onClose}
          style={{ width: "40px", minHeight: "40px", borderRadius: "8px", fontSize: "20px", color: "var(--abp-fg)", padding: 0 }}
        >
          ×
        </KitButton>
      </div>
      <div ref={list} aria-label="Messages" aria-live="polite" className="flex min-h-0 grow flex-col gap-14 overflow-y-auto p-16">
        {available === false && <div className="self-center px-8 text-center text-14 text-fg-muted">{noModelNote}</div>}
        {signedOut && (
          <div role="status" className="self-center px-8 text-center text-14 text-fg-muted">
            {signedOutText}
            {signIn && (
              <>
                {" "}
                <a href={signIn} className="text-accent underline">
                  Sign in
                </a>
              </>
            )}
          </div>
        )}
        {msgs.map((m, i) => (
          <Bubble key={i} msg={m} clear={highlightActive && i === lastAnswer ? onClearHighlight : undefined} retry={sending ? undefined : retry} />
        ))}
        {sending && <div className="self-start px-4 text-13 text-fg-muted">{character.displayName} is thinking…</div>}
        {/* Used up, or chat off for the account (mockup: a panel-coloured notice at the foot of the messages). */}
        {allowance?.notice && !signedOut && (
          <div role="status" className="mt-auto shrink-0 rounded-10 bg-panel p-12 text-center text-14 leading-[1.45] text-fg">
            {allowance.notice}
          </div>
        )}
      </div>
      <div className="flex shrink-0 flex-col gap-8 border-t border-divider px-16 pt-12 pb-16">
        {/* "Try: …" prompt — 40px, 0 12px, radius 8, outline, white 14px 400, left-aligned (prototype TryPrompt). */}
        <KitButton
          variant="outline"
          size="sm"
          disabled={disabled || sending}
          onClick={() => send(tryText)}
          style={{ minHeight: "40px", padding: "0 12px", borderRadius: "8px", fontSize: "14px", justifyContent: "flex-start", textAlign: "left" }}
        >
          Try: “{tryText}”
        </KitButton>
        <form
          className="flex gap-8"
          onSubmit={(e) => {
            e.preventDefault();
            send(text);
          }}
        >
          <Input
            aria-label="Message"
            placeholder={`Ask ${character.displayName} something…`}
            className={s.input}
            value={text}
            onChange={(_, d) => setText(d.value)}
            disabled={disabled}
          />
          {/* While chat is off (used up, off for the account, signed out, no model) Send is grey, not the accent. */}
          <KitButton
            size="form"
            type="submit"
            disabled={disabled || sending || text.trim() === ""}
            style={{ padding: "0 16px", ...(disabled ? { backgroundColor: "var(--abp-outline)" } : {}) }}
          >
            Send
          </KitButton>
        </form>
        {/* Today's allowance (mockup: what is left and the reset time at 13px, a 6px meter; warning colour under 10 %). */}
        {allowance && (
          <div aria-label="Today's allowance" className="flex flex-col gap-6 pt-2">
            <div className="flex justify-between gap-8 text-13">
              <span>{allowance.left}</span>
              {allowance.reset && <span className="text-fg-muted">{allowance.reset}</span>}
            </div>
            <div
              role="meter"
              aria-label="Today's allowance left"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={allowance.percent}
              className="h-6 overflow-hidden rounded-3 bg-divider"
            >
              <div className={`h-full rounded-3 ${allowance.state === "low" ? "bg-warning" : "bg-accent"}`} style={{ width: `${allowance.percent}%` }} />
            </div>
          </div>
        )}
      </div>
    </section>
  );
}
