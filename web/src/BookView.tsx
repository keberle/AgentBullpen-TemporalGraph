import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import { SiteLinks, SiteUserMenu, SiteWordmark } from "./SiteChrome.tsx";
import {
  Checkbox,
  Dropdown,
  Option,
  Slider,
  Tab,
  TabList,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Textarea,
  makeStyles,
  mergeClasses,
  tokens,
} from "@fluentui/react-components";
import { KitButton } from "./design-kit/components/KitButton.tsx";
import { TopBar } from "./design-kit/components/TopBar.tsx";
import { GraphQlRequestError, gql, queries, type Book, type Chapter, type CypherResult, type GraphQlError, type SampleQuery } from "./graphql.ts";
import ChatPanel from "./ChatPanel.tsx";
import { checkStyle } from "./checkStyle.ts";
import { chatQueries, type ChatCharacter } from "./chat.ts";
import GraphPane from "./graph/GraphPane.tsx";
import {
  ambiguousAliases,
  formatStoryDate,
  graphQuery,
  highlightReducer,
  isIdColumn,
  isTemporal,
  noHighlight,
  resultHighlight,
  timelineLayout,
  viewAt,
  type GraphPayload,
} from "./graph/model.ts";
import TimelineSlider from "./TimelineSlider.tsx";
import { clearsHighlight, noSelection, selectionReducer, type SelectionAction } from "./graph/selection.ts";
import { chatSync, navigate, slug, type NavKind, type Route, type TabKey } from "./route.ts";

// Book view, ported from the site's prototype:
// header crumb + Change book, the timeline row (Reader is at · chapter slider · Spoilers) with a character picker,
// and the Graph Query tab only (no 2D/3D tabs or usage strip in this pass). Real data from the api:
// chapters, characters and sample queries per book; Run sends the editor's text to `cypher`.
// The prototype's character row on the right (initials; spoilers off: only characters mentioned by the chapter, by
// their display name); clicking one slides the chat window in (ChatPanel) and also sets the query's $character.
// The 2D graph tab (default) and the lazy 3D tab, left of Graph Query, on the whole graph fetched once per book and gated
// in the browser by the timeline row. The chat's last answer lights its rows up; avatars, the character dropdown and Character
// nodes select each other. A node click never switches the chat (selectionReducer); its popover has a "Chat" button.
// The tab, chapter, Spoilers, "Show facts & events" and the open chat live in the URL (route.ts); this view reads them from
// `route` and changes them with navigate(), so Back/Forward and a reload restore them.

const cell = { padding: "12px 16px", borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, height: "auto" };
const useStyles = makeStyles({
  tabs: { padding: "12px 24px 0", gap: "4px" },
  // Tabs: 40px, 0 16px, 600 15px; selected white with a 3px accent underline (Fluent's indicator, accent-colored).
  tab: { minHeight: "40px", padding: "0 16px", "& .fui-Tab__content": { fontFamily: tokens.fontFamilyBase, fontSize: "15px", fontWeight: 600, color: tokens.colorNeutralForeground2 } },
  tabOn: { "& .fui-Tab__content": { color: tokens.colorNeutralForeground1 } },
  slider: { flex: "1 1 200px", minWidth: "160px" },
  check: checkStyle({ fontSize: "15px", gap: "8px", minHeight: "44px" }),
  // "Show ids" under the results grid, at the footer's size (as the graph toolbar's checkboxes).
  checkSmall: checkStyle({ fontSize: "13px", gap: "6px" }),
  picker: { minWidth: "240px" },
  editor: {
    minHeight: "140px",
    borderRadius: "12px",
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    "& textarea": { minHeight: "140px", padding: "16px", fontFamily: tokens.fontFamilyMonospace, fontVariantLigatures: "none", fontSize: "14px", lineHeight: 1.6, color: tokens.colorNeutralForeground1, whiteSpace: "pre" },
  },
  // Fluent Table forces a 20px line height and pads header labels inside a button; the mockup uses neither.
  table: { lineHeight: "normal" },
  th: { ...cell, color: tokens.colorNeutralForeground2, fontWeight: 600, fontSize: "14px", "& .fui-TableHeaderCell__button": { padding: 0, minHeight: 0, lineHeight: "normal" } },
  td: { ...cell, fontSize: "14px" },
  row: { ":hover": { backgroundColor: "transparent" } }, // [gap] no row hover drawn
});

type Outcome = { kind: "result"; result: CypherResult; spoilers: boolean } | { kind: "error"; errors: GraphQlError[] };

export default function BookView({ book, route, onChangeBook }: { book: Book; route: Route; onChangeBook: () => void }) {
  const s = useStyles();
  const [chapters, setChapters] = useState<Chapter[]>([]);
  const [characters, setCharacters] = useState<string[]>([]);
  const [samples, setSamples] = useState<SampleQuery[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  // The chat tools' queries sit below the book's samples, collapsed until "Show".
  const [showTools, setShowTools] = useState(false);
  // The results grid hides the id columns unless "Show ids".
  const [showIds, setShowIds] = useState(false);
  const [text, setText] = useState("");
  const [running, setRunning] = useState(false);
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [chatAvailable, setChatAvailable] = useState<boolean | null>(null);
  const [chatCharacters, setChatCharacters] = useState<ChatCharacter[]>([]);
  const [graph, setGraph] = useState<GraphPayload | null>(null);
  const [highlight, dispatchHighlight] = useReducer(highlightReducer, noHighlight);
  const [selection, dispatchSelectionOnly] = useReducer(selectionReducer, noSelection);
  const { character, chatWith, node: selectedNode } = selection;

  // The view state from the URL. The chapter is clamped to the book once its chapters are known (and the URL corrected).
  const { tab, spoilers, facts } = route;
  const chapter = chapters.length > 0 ? Math.min(route.chapter, chapters.length) : route.chapter;
  const routeRef = useRef(route);
  routeRef.current = route;
  const go = useCallback((change: Partial<Route>, kind?: NavKind) => navigate({ ...routeRef.current, ...change }, kind), []);
  const setChapter = (n: number) => go({ chapter: n }, "chapter");
  const setSpoilers = (on: boolean) => go({ spoilers: on });
  const setTab = (t: TabKey) => go({ tab: t });
  const setFacts = (on: boolean) => go({ facts: on });
  useEffect(() => {
    if (chapters.length > 0 && route.chapter > chapters.length) go({ chapter: chapters.length }, "replace");
  }, [chapters.length, route.chapter, go]);

  // A selection action updates the selection and, when it opens, switches or closes the chat, the URL's `chat` (a new history
  // entry; the roster ending a chat after a chapter move corrects the current entry instead). A selection switch also drops the
  // previous chat answer's highlight (one ring, on the selected node).
  const selectionRef = useRef(selection);
  selectionRef.current = selection;
  const applySelection = useCallback((a: SelectionAction) => {
    selectionRef.current = selectionReducer(selectionRef.current, a);
    dispatchSelectionOnly(a);
    if (clearsHighlight(a)) dispatchHighlight({ type: "clear" });
  }, []);
  const dispatchSelection = useCallback(
    (a: SelectionAction) => {
      const before = selectionRef.current.chatWith;
      applySelection(a);
      const after = selectionRef.current.chatWith;
      if (after !== before) go({ chat: after ? slug(after) : null }, a.type === "roster" ? "replace" : "push");
    },
    [applySelection, go],
  );

  // The whole graph, once per book (the graph tabs gate it in the browser).
  useEffect(() => {
    gql<{ graph: GraphPayload }>(graphQuery, { id: book.id })
      .then((d) => setGraph(d.graph))
      .catch((e: Error) => setLoadError(e.message));
  }, [book.id]);

  // Character canonical name ↔ node id, for the dropdown and Character node clicks.
  const characterNodes = useMemo(() => {
    const byName = new Map<string, number>();
    const byId = new Map<number, string>();
    for (const n of graph?.nodes ?? [])
      if (n.label === "Character" && n.name) {
        byName.set(n.name, n.id);
        byId.set(n.id, n.name);
      }
    return { byName, byId };
  }, [graph]);

  useEffect(() => {
    gql<{ chapters: Chapter[]; characters: string[]; sampleQueries: SampleQuery[] }>(queries.book, { id: book.id })
      .then((d) => {
        setChapters(d.chapters);
        setCharacters(d.characters);
        dispatchSelection({ type: "characters", first: d.characters[0] ?? null });
        setSamples(d.sampleQueries);
      })
      .catch((e: Error) => setLoadError(e.message));
  }, [book.id]);

  useEffect(() => {
    gql<{ chatAvailable: boolean }>(chatQueries.available)
      .then((d) => setChatAvailable(d.chatAvailable))
      .catch(() => setChatAvailable(false));
  }, []);

  // The character row follows the chapter and spoilers (display names; later characters hidden with spoilers off).
  // A response that arrives after the timeline moved on is dropped (stepping quickly fires overlapping requests).
  // `rosterKey` says which chapter and spoilers the roster is for, so the URL's chat is only acted on with a current roster.
  const [rosterKey, setRosterKey] = useState<string | null>(null);
  useEffect(() => {
    if (chapters.length === 0) return;
    let current = true;
    const key = `${chapter}|${spoilers}`;
    gql<{ chatCharacters: ChatCharacter[] }>(chatQueries.characters, { book: book.id, ch: chapter, s: spoilers })
      .then((d) => {
        if (!current) return;
        setChatCharacters(d.chatCharacters);
        setRosterKey(key);
        // The open chat's character isn't known at this chapter any more: close the chat.
        dispatchSelection({ type: "roster", names: d.chatCharacters.map((c) => c.name) });
      })
      .catch(() => {
        if (!current) return;
        setChatCharacters([]);
        setRosterKey(key);
      });
    return () => {
      current = false;
    };
  }, [book.id, chapter, spoilers, chapters.length, dispatchSelection]);

  // The URL's chat -> the open chat (a reload, a pasted link, Back/Forward). In-app changes already match, so this does
  // nothing for them; a character not offered at this chapter is dropped from the URL.
  useEffect(() => {
    if (rosterKey !== `${chapter}|${spoilers}`) return;
    const sync = chatSync(route.chat, chatWith, chatCharacters);
    if (sync.type === "open") applySelection({ type: "avatar", name: sync.name, nodeId: sync.nodeId });
    else if (sync.type === "close") applySelection({ type: "closeChat" });
    else if (sync.type === "drop") go({ chat: null }, "replace");
  }, [route.chat, chatWith, chatCharacters, rosterKey, chapter, spoilers, applySelection, go]);

  const chatCharacter = chatCharacters.find((c) => c.name === chatWith) ?? null;

  // A node clicked in the graph: selected, and a Character also becomes the dropdown's character. It never opens or
  // changes the chat; the popover's "Chat" button does, for roster characters.
  const selectFromGraph = useCallback(
    (id: number | null) => {
      const name = id == null ? undefined : characterNodes.byId.get(id);
      dispatchSelection({ type: "node", id, characterName: name && characters.includes(name) ? name : null });
    },
    [characterNodes, characters],
  );
  const rosterNodes = useMemo(() => new Map(chatCharacters.filter((c) => c.nodeId != null).map((c) => [c.nodeId!, c.name])), [chatCharacters]);
  // Avatar images by node id for the 2D graph, and the avatars that failed to load (shown as initials, not requested again).
  const avatarNodes = useMemo(() => new Map(chatCharacters.filter((c) => c.nodeId != null && c.avatar).map((c) => [c.nodeId!, c.avatar!])), [chatCharacters]);
  const [brokenAvatars, setBrokenAvatars] = useState<ReadonlySet<string>>(new Set());

  // The dropdown's label for each canonical name: the name it is shown under at this chapter, as in the roster and the graph (
  // the roster's spelling once revealed, else the short name); a character not revealed yet keeps its short name.
  const ambiguous = useMemo(() => (graph ? ambiguousAliases(graph) : new Set<string>()), [graph]);
  const characterLabels = useMemo(() => {
    const m = new Map<string, string>();
    if (!graph) return m;
    // The graph's namer (viewAt), so two characters a visible IDENTICAL_TO edge joins read the same here as in the graph; a
    // book without a timeline gets displayName, as before.
    const view = viewAt(graph, chapter, spoilers, ambiguous);
    for (const n of graph.nodes)
      if (n.label === "Character" && n.name) m.set(n.name, view.name(n) ?? n.shortName ?? n.name);
    return m;
  }, [graph, chapter, spoilers, ambiguous]);
  const characterLabel = (name: string) => characterLabels.get(name) ?? name;
  // A book that ships its timeline gets the slider drawn from it (chapter and event ticks; story time with Spoilers on);
  // null for a book without one, which keeps the plain chapter slider.
  const timeline = useMemo(() => (graph && isTemporal(graph) && graph.chapters.length > 0 ? timelineLayout(graph, chapter, spoilers) : null), [graph, chapter, spoilers]);

  const sampleButton = (x: SampleQuery, i: number) => (
    <button
      key={x.name}
      type="button"
      aria-pressed={selected === i}
      onClick={() => pickSample(i)}
      className="min-h-52 shrink-0 cursor-pointer rounded-10 border px-12 py-10 text-left font-sans text-14 leading-[1.4] text-fg"
      style={{
        borderColor: selected === i ? "var(--abp-accent)" : "var(--abp-divider)",
        background: selected === i ? "rgba(96,165,250,0.12)" : "transparent",
      }}
    >
      {x.question}
    </button>
  );

  const pickSample = (i: number) => {
    setSelected(i);
    setText(samples[i].text);
    setOutcome(null);
  };

  // Changing chapter, character or spoilers doesn't re-run: Run does.
  const run = () => {
    setRunning(true);
    const sentSpoilers = spoilers;
    gql<{ cypher: CypherResult }>(queries.cypher, { book: book.id, q: text, ch: chapter, c: character, s: spoilers })
      .then((d) => {
        setOutcome({ kind: "result", result: d.cypher, spoilers: sentSpoilers });
        // The result lights its nodes and edges in the graph tabs, as a chat lookup does (a result without ids clears it).
        const lit = resultHighlight(d.cypher.columns, d.cypher.rows);
        dispatchHighlight(lit.nodeIds.length + lit.edgeIds.length > 0 ? { type: "answer", toolCalls: [lit] } : { type: "clear" });
      })
      .catch((e: Error) => setOutcome({ kind: "error", errors: e instanceof GraphQlRequestError ? e.errors : [{ message: e.message }] }))
      .finally(() => setRunning(false));
  };

  const maxChapter = Math.max(1, chapters.length);
  const params = [`$character = ${character === null ? "null" : `'${character}'`}`, `$chapter = ${chapter}`, `$spoilers = ${spoilers ? "true" : "false"}`];

  return (
    <div className="flex h-screen flex-col">
      <title>{`${book.title} · Temporal Graph`}</title>
      <TopBar
        right={<SiteUserMenu />}
        left={
          <>
            <div className="flex items-center gap-16">
              <SiteWordmark />
              <span className="text-15 text-fg-muted">/ Temporal Graph · {book.title}</span>
              <KitButton variant="outline" size="xs" onClick={onChangeBook}>
                Change book
              </KitButton>
            </div>
            <SiteLinks />
          </>
        }
      />

      {/* The hero as a 96 px crop band (object-position center 30%). A full-width 3:2 banner would be 1,067 px tall on a
          1,600 px pane, taller than the 1,000 px screen, so it would push the graph below the fold; the band keeps the graph in view. */}
      {book.hero && (
        <div className="relative h-96 shrink-0 overflow-hidden border-b border-divider" aria-label="Book banner">
          <img src={`/${book.hero}`} alt={book.title} className="absolute inset-0 block h-full w-full object-cover object-[center_30%]" />
          <div className="absolute inset-x-0 bottom-0 h-1/2 bg-linear-to-t from-page to-transparent" />
          <div className="absolute right-24 bottom-10 left-24 flex flex-wrap items-baseline gap-x-12 leading-[1.2] text-fg">
            <span className="text-20 font-bold">{book.title}</span>
            <span className="text-14 text-fg-muted">{book.author}</span>
          </div>
        </div>
      )}

      {/* basis-0: the row takes the height left under the top bar, however long the chat gets (else the top bar is squeezed). */}
      <div className="flex min-h-0 grow basis-0">
        <div className="flex min-h-0 min-w-0 grow flex-col">
          {/* Timeline + Spoilers, and the character picker */}
          <div className="flex shrink-0 flex-wrap items-center gap-x-20 gap-y-12 border-b border-divider px-24 py-14">
            <label htmlFor="tl" className="text-14 whitespace-nowrap text-fg-muted">
              Reader is at
            </label>
            {timeline ? (
              <TimelineSlider id="tl" layout={timeline} chapter={chapter} onChange={setChapter} className={s.slider} />
            ) : (
              <Slider
                id="tl"
                min={1}
                max={maxChapter}
                value={chapter}
                onChange={(_, d) => setChapter(d.value)}
                className={s.slider}
                aria-valuetext={`Chapter ${chapter}`}
              />
            )}
            <span className="min-w-92 text-15 font-bold whitespace-nowrap">Chapter {chapter}</span>
            {/* With Spoilers on the slider moves in story time: the story date reached, story(pos). The timeline's dates all hang
                on one assumed date (the anchor), so it is marked as an assumption, with the anchor's note behind it. */}
            {timeline && spoilers && timeline.story && (
              <span className="text-14 whitespace-nowrap text-fg-muted" aria-label="Story date" title={timeline.note ? `Assumption: ${timeline.note}` : undefined}>
                Story date <b className="text-fg">{formatStoryDate(timeline.story)}</b>
                {timeline.note ? " (assumed)" : ""}
              </span>
            )}
            <Checkbox label="Spoilers" checked={spoilers} onChange={(_, d) => setSpoilers(!!d.checked)} className={s.check} />
            <label id="character-label" className="text-14 whitespace-nowrap text-fg-muted">
              Character
            </label>
            <Dropdown
              aria-labelledby="character-label"
              className={s.picker}
              value={character ? characterLabel(character) : ""}
              selectedOptions={character ? [character] : []}
              onOptionSelect={(_, d) => {
                dispatchSelection({
                  type: "dropdown",
                  name: d.optionValue ?? null,
                  nodeId: d.optionValue ? (characterNodes.byName.get(d.optionValue) ?? null) : null,
                });
              }}
            >
              {characters.map((c) => (
                <Option key={c} value={c}>
                  {characterLabel(c)}
                </Option>
              ))}
            </Dropdown>
          </div>

          <main className="flex min-h-0 min-w-0 grow flex-col pb-16">
            <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as TabKey)} className={s.tabs}>
              {(
                [
                  ["2d", "2D graph"],
                  ["3d", "3D graph"],
                  ["query", "Graph Query"],
                ] as const
              ).map(([k, label]) => (
                <Tab key={k} value={k} className={mergeClasses(s.tab, tab === k && s.tabOn)}>
                  {label}
                </Tab>
              ))}
            </TabList>

            <div className="relative mx-24 mt-12 min-h-0 grow overflow-hidden rounded-16 bg-panel">
              {tab !== "query" && (
                <GraphPane
                  mode={tab}
                  payload={graph}
                  chapter={chapter}
                  spoilers={spoilers}
                  facts={facts}
                  onFactsChange={setFacts}
                  highlight={highlight}
                  selectedId={selectedNode}
                  onSelect={selectFromGraph}
                  rosterNodes={rosterNodes}
                  avatars={avatarNodes}
                  onChat={(name) => dispatchSelection({ type: "chat", name })}
                />
              )}
              <div className="absolute inset-0 flex" style={{ display: tab === "query" ? undefined : "none" }}>
                <aside aria-label="Sample queries" className="flex w-260 shrink-0 flex-col gap-8 overflow-y-auto border-r border-divider p-16">
                  <span className="pb-4 text-12 font-bold tracking-[1.5px] text-fg-muted">SAMPLE QUERIES</span>
                  {loadError && <span className="text-13 text-fg-muted">Couldn’t load the book: {loadError}</span>}
                  {samples.map((x, i) => x.group === "sample" && sampleButton(x, i))}
                  {samples.some((x) => x.group === "tool") && (
                    <div className="flex items-center gap-8 pt-12 pb-4">
                      <span id="tool-queries" className="text-12 font-bold tracking-[1.5px] text-fg-muted">
                        TOOL QUERIES
                      </span>
                      <button
                        type="button"
                        aria-expanded={showTools}
                        aria-controls="tool-query-list"
                        onClick={() => setShowTools((v) => !v)}
                        className="ml-auto cursor-pointer rounded-6 border-0 bg-transparent px-4 font-sans text-13 text-accent"
                      >
                        {showTools ? "Hide" : "Show"}
                      </button>
                    </div>
                  )}
                  {showTools && (
                    <div id="tool-query-list" role="group" aria-labelledby="tool-queries" className="flex flex-col gap-8">
                      <span className="text-13 leading-[1.4] text-fg-muted">What the chat calls; they use the selected character.</span>
                      {samples.map((x, i) => x.group === "tool" && sampleButton(x, i))}
                    </div>
                  )}
                </aside>
                <section aria-label="Query" className="flex min-h-0 min-w-0 grow flex-col gap-12 px-24 py-16">
                  <div className="flex flex-wrap items-center gap-x-12 gap-y-8">
                    <label htmlFor="cq" className="text-14 text-fg-muted">
                      openCypher
                    </label>
                    <KitButton size="action" onClick={run} disabled={running || text.trim() === ""} style={{ padding: "0 16px" }}>
                      Run
                    </KitButton>
                    <span className="ml-auto text-13 text-fg-muted">Parameter values come from the timeline, Spoilers and the selected character</span>
                  </div>
                  <div aria-label="Parameters sent with this query" className="flex flex-wrap items-center gap-8 font-mono text-14">
                    <span className="mr-4 font-sans text-13 font-semibold text-fg-muted">Parameters</span>
                    {params.map((p) => (
                      <span key={p.split(" ")[0]} className="rounded-6 border border-accent px-10 py-6" style={{ background: "rgba(96,165,250,0.15)" }}>
                        {p}
                      </span>
                    ))}
                  </div>
                  <Textarea id="cq" value={text} onChange={(_, d) => setText(d.value)} resize="vertical" className={s.editor} spellCheck={false} />
                  <div className="min-h-0 grow overflow-auto rounded-12 border border-divider">
                    {outcome?.kind === "result" ? (
                      <>
                        <Table aria-label="Query results" className={s.table}>
                          <TableHeader>
                            <TableRow className={s.row}>
                              {outcome.result.columns.map(
                                (h) =>
                                  (showIds || !isIdColumn(h)) && (
                                    <TableHeaderCell key={h} className={s.th}>
                                      {h}
                                    </TableHeaderCell>
                                  ),
                              )}
                            </TableRow>
                          </TableHeader>
                          <TableBody>
                            {outcome.result.rows.map((r, i) => (
                              <TableRow key={i} className={s.row}>
                                {r.map(
                                  (v, j) =>
                                    (showIds || !isIdColumn(outcome.result.columns[j])) && (
                                      <TableCell key={j} className={s.td}>
                                        {v ?? "null"}
                                      </TableCell>
                                    ),
                                )}
                              </TableRow>
                            ))}
                          </TableBody>
                        </Table>
                        <div className="flex flex-wrap items-center gap-x-12 gap-y-4 px-16 py-12 text-13 text-fg-muted">
                          <span>
                            {outcome.result.rows.length} row{outcome.result.rows.length === 1 ? "" : "s"} · {outcome.result.elapsedMs} ms
                            {outcome.result.truncated ? " · truncated" : ""}
                            {outcome.spoilers
                              ? timeline
                                ? " · spoilers on: filtered by story time (what is true in the story by the date the telling has reached)"
                                : " · spoilers on: nothing is hidden by when the reader learns it (a story-time filter isn’t built yet)"
                              : " · spoilers off: filtered by when the reader learns it"}
                          </span>
                          {(highlight.nodes.size > 0 || highlight.edges.size > 0) && (
                            <span>
                              Lit in the graph ·{" "}
                              <button
                                type="button"
                                onClick={() => dispatchHighlight({ type: "clear" })}
                                className="cursor-pointer border-0 bg-transparent p-0 font-sans text-13 text-accent underline"
                              >
                                Clear
                              </button>
                            </span>
                          )}
                          {outcome.result.columns.some(isIdColumn) && (
                            <Checkbox
                              label="Show ids"
                              checked={showIds}
                              onChange={(_, d) => setShowIds(!!d.checked)}
                              className={s.checkSmall}
                            />
                          )}
                        </div>
                      </>
                    ) : outcome?.kind === "error" ? (
                      <div role="alert" className="flex flex-col gap-6 px-16 py-12 text-14">
                        {outcome.errors.map((e, i) => (
                          <span key={i}>
                            <b>{e.extensions?.reason ?? e.message}</b>
                            {e.extensions?.line !== undefined && (
                              <span className="text-fg-muted">
                                {" "}
                                · line {e.extensions.line}, column {e.extensions.column}
                              </span>
                            )}
                          </span>
                        ))}
                      </div>
                    ) : (
                      <div className="flex h-full min-h-160 items-center justify-center text-14 text-fg-muted">
                        {running ? "Running…" : "Pick a sample or write a query, then Run."}
                      </div>
                    )}
                  </div>
                </section>
              </div>
            </div>
          </main>
        </div>

        {chatCharacter && (
          <ChatPanel
            bookId={book.id}
            character={chatCharacter}
            chapter={chapter}
            spoilers={spoilers}
            available={chatAvailable}
            onClose={() => dispatchSelection({ type: "closeChat" })}
            onAnswer={(r) => dispatchHighlight({ type: "answer", toolCalls: r.toolCalls })}
            highlightActive={highlight.nodes.size > 0 || highlight.edges.size > 0}
            onClearHighlight={() => dispatchHighlight({ type: "clear" })}
          />
        )}

        <aside aria-label="Characters" className="flex w-88 shrink-0 flex-col items-center gap-14 overflow-y-auto border-l border-divider py-16">
          {chatCharacters.map((c) => (
            <button
              key={c.name}
              type="button"
              title={c.displayName}
              aria-label={`Chat with ${c.displayName}`}
              aria-pressed={chatWith === c.name}
              onClick={() => {
                dispatchSelection({ type: "avatar", name: c.name, nodeId: c.nodeId });
              }}
              className="h-56 w-56 shrink-0 cursor-pointer overflow-hidden rounded-999 border-0 bg-panel p-0 font-sans text-16 font-bold text-fg"
              style={{ outline: chatWith === c.name ? "2px solid var(--abp-accent)" : "2px solid transparent", outlineOffset: "3px" }}
            >
              {c.avatar && !brokenAvatars.has(c.avatar) ? (
                <img
                  src={`/${c.avatar}`}
                  alt=""
                  className="block h-full w-full rounded-999 object-cover"
                  onError={() => setBrokenAvatars((b) => new Set(b).add(c.avatar!))}
                />
              ) : (
                c.initials
              )}
            </button>
          ))}
          {/* An empty chat roster (books.json "chat": [], e.g. a book not indexed yet), or none mentioned by this chapter. */}
          {chapters.length > 0 && chatCharacters.length === 0 && <span className="px-8 text-center text-12 text-fg-muted">No character to talk to yet</span>}
        </aside>
      </div>
    </div>
  );
}
