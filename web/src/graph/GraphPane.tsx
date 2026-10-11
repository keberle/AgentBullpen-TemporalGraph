import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Checkbox, makeStyles } from "@fluentui/react-components";
import { KitButton } from "../design-kit/components/KitButton.tsx";
import { PerceptionGlyph, perceptionKinds, type PerceptionKind } from "../design-kit/icons/perception.tsx";
import { checkStyle } from "../checkStyle.ts";
import { readDetails, writeDetails } from "./detailsSetting.ts";
import Graph2D from "./Graph2D.tsx";
import {
  ambiguousAliases,
  degrees,
  gate,
  GRAPH_MAX_ZOOM,
  GRAPH_MIN_ZOOM,
  isEntityLabel,
  isTemporal,
  nodeRows,
  sliderToZoom,
  zoomToSlider,
  type GraphPayload,
  type Highlight,
} from "./model.ts";

// The 2D and 3D graph tabs' frame: the "Show facts & events" toggle, the legend, and the selection popover (the
// mockup's right rail, compact: the app's right panel is the chat). The 3D tab loads on first use (React.lazy).

const Graph3D = lazy(() => import("./Graph3D.tsx"));

/**
 * A slim vertical zoom slider at the 2D pane's right edge, log scale between the graph's min and max zoom, with + and −.
 * Two-way without re-rendering on every zoom frame: the graph's zoom updates the input through its ref (`ref.current.show(k)`).
 */
function ZoomSlider({ api, onZoom }: { api: React.RefObject<{ show: (k: number) => void } | null>; onZoom: (k: number) => void }) {
  const input = useRef<HTMLInputElement>(null);
  const current = useRef(1);
  useEffect(() => {
    api.current = {
      show: (k: number) => {
        current.current = k;
        if (input.current) input.current.value = String(zoomToSlider(k, GRAPH_MIN_ZOOM, GRAPH_MAX_ZOOM));
      },
    };
    return () => {
      api.current = null;
    };
  }, [api]);
  const step = (factor: number) => onZoom(Math.min(GRAPH_MAX_ZOOM, Math.max(GRAPH_MIN_ZOOM, current.current * factor)));
  const button = "flex h-24 w-24 cursor-pointer items-center justify-center rounded-6 border border-divider bg-page/80 p-0 font-sans text-16 text-fg";
  return (
    <div className="absolute top-60 right-12 flex flex-col items-center gap-6 rounded-6 border border-divider bg-page/80 px-4 py-6" aria-label="Zoom">
      <button type="button" className={button} onClick={() => step(1.5)} aria-label="Zoom in">
        +
      </button>
      <input
        ref={input}
        type="range"
        min={0}
        max={100}
        defaultValue={zoomToSlider(1, GRAPH_MIN_ZOOM, GRAPH_MAX_ZOOM)}
        aria-label="Zoom level"
        className="h-160 w-20 cursor-pointer"
        style={{ writingMode: "vertical-lr", direction: "rtl", accentColor: "var(--abp-accent)" }}
        onChange={(e) => onZoom(sliderToZoom(Number(e.currentTarget.value), GRAPH_MIN_ZOOM, GRAPH_MAX_ZOOM))}
      />
      <button type="button" className={button} onClick={() => step(1 / 1.5)} aria-label="Zoom out">
        −
      </button>
    </div>
  );
}

const useStyles = makeStyles({
  check: checkStyle({ fontSize: "13px", gap: "6px" }),
});

/**
 * The legend's swatches, drawn with the kit's colours like the canvas (Character disc, Location diamond, Event ring, Fact square).
 * `temporal` (a book with a timeline) adds the referenced Event's dashed ring, names that book's edge types, and, with
 * `perceptions` (the PERCEIVES kinds now drawn), the perception icons by name: an icon is told by its shape, and this says which is which.
 */
function Legend({ temporal, perceptions }: { temporal: boolean; perceptions: readonly PerceptionKind[] }) {
  return (
    <div
      aria-label="Legend"
      className="pointer-events-none absolute bottom-12 left-14 flex flex-wrap items-center gap-x-16 gap-y-6 rounded-6 border border-divider bg-page/80 px-12 py-8 text-12 text-fg-muted"
    >
      <span className="inline-flex items-center gap-6">
        <i className="inline-block h-14 w-14 rounded-999 bg-fg" />
        Character
      </span>
      <span className="inline-flex items-center gap-6">
        <i className="inline-block h-14 w-14 scale-80 rotate-45" style={{ background: "var(--abp-fg-muted)" }} />
        Location
      </span>
      <span className="inline-flex items-center gap-6">
        <i className="inline-block box-border h-14 w-14 rounded-999 border-2" style={{ borderColor: "var(--abp-fg-faint)" }} />
        Event
      </span>
      {temporal && (
        <span className="inline-flex items-center gap-6">
          <i className="inline-block box-border h-14 w-14 rounded-999 border-2 border-dashed" style={{ borderColor: "var(--abp-fg-faint)" }} />
          Event told about
        </span>
      )}
      <span className="inline-flex items-center gap-6">
        <i className="m-3 inline-block h-8 w-8" style={{ background: "var(--abp-outline)" }} />
        Fact
      </span>
      <span className="inline-flex items-center gap-6">
        <i className="inline-block w-22 border-t-2" style={{ borderColor: "var(--abp-fg-muted)" }} />
        {temporal ? "IDENTICAL_TO / PERCEIVES (people)" : "MET"}
      </span>
      <span className="inline-flex items-center gap-6">
        <i className="inline-block w-22 border-t-2 border-dashed" style={{ borderColor: "var(--abp-fg-muted)" }} />
        RELATED_TO
      </span>
      <span className="inline-flex items-center gap-6">
        <i className="inline-block w-22 border-t-2 border-dotted" style={{ borderColor: "var(--abp-fg-muted)" }} />
        LOCATED_AT
      </span>
      <span className="inline-flex items-center gap-6">
        <i className="inline-block w-22 border-t" style={{ borderColor: "var(--abp-fg-faint)" }} />
        {temporal ? "PARTICIPATED_IN / DISCOVERED / PERCEIVES / MENTIONED_IN" : "ABOUT / PARTICIPATED_IN / STATES"}
      </span>
      {perceptions.length > 0 && (
        <span role="group" aria-label="Perception icons" className="flex basis-full flex-wrap items-center gap-x-12 gap-y-4">
          {perceptions.map((k) => (
            <span key={k} className="inline-flex items-center gap-4">
              <PerceptionGlyph kind={k} size={14} />
              {k}
            </span>
          ))}
        </span>
      )}
    </div>
  );
}

export default function GraphPane({
  mode,
  payload,
  chapter,
  spoilers,
  facts,
  onFactsChange,
  highlight,
  selectedId,
  onSelect,
  rosterNodes,
  onChat,
  avatars,
}: {
  mode: "2d" | "3d";
  payload: GraphPayload | null;
  chapter: number;
  spoilers: boolean;
  facts: boolean;
  onFactsChange: (facts: boolean) => void;
  highlight: Highlight;
  selectedId: number | null;
  onSelect: (id: number | null) => void;
  /** Roster characters by node id (the popover's "Chat" button starts their chat; a node click never does). */
  rosterNodes?: ReadonlyMap<number, string>;
  onChat?: (name: string) => void;
  /** Roster characters' avatar paths by node id (the 3D tab draws them as faces too). */
  avatars?: ReadonlyMap<number, string>;
}) {
  const s = useStyles();
  const degree = useMemo(() => (payload ? degrees(payload) : new Map<number, number>()), [payload]);
  const ambiguous = useMemo(() => (payload ? ambiguousAliases(payload) : new Set<string>()), [payload]);
  const gated = useMemo(
    () => (payload ? gate(payload, { chapter, spoilers, facts, highlight, ambiguous }) : null),
    [payload, chapter, spoilers, facts, highlight, ambiguous],
  );
  // The Fit button asks the tab to zoom to the visible nodes (the tabs also fit after the first settle and big re-gatings);
  // Focus zooms to the largest connected component; the zoom slider sets the 2D zoom and follows it.
  const [fitRequest, setFitRequest] = useState(0);
  const [focusRequest, setFocusRequest] = useState(0);
  const [zoomRequest, setZoomRequest] = useState<{ k: number } | null>(null);
  const slider = useRef<{ show: (k: number) => void } | null>(null);
  const onZoomChange = useCallback((k: number) => slider.current?.show(k), []);
  const [details, setDetails] = useState(readDetails);
  // The 3D tab's slow orbit (off by default; any drag stops it) and Reset view (back to the Focus framing).
  const [autoRotate, setAutoRotate] = useState(false);
  const [resetRequest, setResetRequest] = useState(0);
  const roster = useMemo(() => new Set(rosterNodes?.keys() ?? []), [rosterNodes]);
  // The perception kinds on the edges now drawn, in the kit's order, for the legend (none in a book without PERCEIVES edges).
  const perceptions = useMemo(() => {
    const drawn = new Set(gated?.edges.filter((e) => e.type === "PERCEIVES").map((e) => e.kind));
    return perceptionKinds.filter((k) => drawn.has(k));
  }, [gated]);
  const visibleSelected = gated && selectedId != null && gated.nodes.some((n) => n.id === selectedId) ? selectedId : null;
  const selectedNode = visibleSelected == null ? null : gated!.nodes.find((n) => n.id === visibleSelected)!;
  const rows = useMemo(
    () => (payload && visibleSelected != null ? nodeRows(payload, visibleSelected, chapter, spoilers, 8, ambiguous) : null),
    [payload, visibleSelected, chapter, spoilers, ambiguous],
  );

  // The popover follows its node every frame: positioned through the DOM, not React state.
  const pop = useRef<HTMLDivElement>(null);
  const place = (p: { x: number; y: number } | null) => {
    const el = pop.current;
    if (!el) return;
    if (!p) {
      el.style.visibility = "hidden";
      return;
    }
    el.style.visibility = "visible";
    el.style.transform = `translate(${Math.round(p.x + 16)}px, ${Math.round(p.y - 12)}px)`;
  };

  // Escape closes the popover (a click on the background does too).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onSelect(null);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onSelect]);

  const [threeLoaded, setThreeLoaded] = useState(false);
  useEffect(() => {
    if (mode === "3d") setThreeLoaded(true);
  }, [mode]);

  if (!payload || !gated) return <div className="absolute inset-0 flex items-center justify-center text-14 text-fg-muted">Loading the graph…</div>;

  return (
    <div
      className="absolute inset-0 overflow-hidden bg-page"
      aria-label={mode === "2d" ? "2D graph" : "3D graph"}
      data-characters={gated.nodes.filter((n) => n.label === "Character").length}
      data-nodes={gated.nodes.length}
    >
      {mode === "2d" ? (
        <Graph2D
          payload={payload}
          gated={gated}
          degree={degree}
          highlight={highlight}
          selectedId={visibleSelected}
          onSelect={onSelect}
          onFrame={place}
          fitRequest={fitRequest}
          focusRequest={focusRequest}
          zoomRequest={zoomRequest}
          onZoomChange={onZoomChange}
          avatars={avatars}
        />
      ) : (
        threeLoaded && (
          <Suspense fallback={<div className="absolute inset-0 flex items-center justify-center text-14 text-fg-muted">Loading 3D…</div>}>
            <Graph3D
              payload={payload}
              gated={gated}
              degree={degree}
              highlight={highlight}
              selectedId={visibleSelected}
              onSelect={onSelect}
              fitRequest={fitRequest}
              focusRequest={focusRequest}
              resetRequest={resetRequest}
              avatars={avatars}
              roster={roster}
              autoRotate={autoRotate}
              onAutoRotateStop={() => setAutoRotate(false)}
              onFrame={place}
            />
          </Suspense>
        )
      )}

      <div className="absolute top-8 right-12 flex items-center gap-12 rounded-6 border border-divider bg-page/80 px-8">
        <KitButton variant="outline" size="xs" onClick={() => setFitRequest((n) => n + 1)} aria-label="Fit the graph to the view">
          Fit
        </KitButton>
        <KitButton variant="outline" size="xs" onClick={() => setFocusRequest((n) => n + 1)} aria-label="Focus on the main cluster">
          Focus
        </KitButton>
        {mode === "3d" && (
          <>
            <KitButton variant="outline" size="xs" onClick={() => setResetRequest((n) => n + 1)} aria-label="Reset the view to the main cluster">
              Reset view
            </KitButton>
            <Checkbox label="Auto-rotate" checked={autoRotate} onChange={(_, d) => setAutoRotate(!!d.checked)} className={s.check} />
          </>
        )}
        <Checkbox label="Show facts & events" checked={facts} onChange={(_, d) => onFactsChange(!!d.checked)} className={s.check} />
        <Checkbox
          label="Details card"
          checked={details}
          onChange={(_, d) => {
            setDetails(!!d.checked);
            writeDetails(!!d.checked);
          }}
          className={s.check}
        />
        <span className="text-12 text-fg-muted" aria-label="Visible nodes">
          {gated.nodes.filter((n) => isEntityLabel(n.label)).length} people &amp; places ·{" "}
          {gated.nodes.length - gated.nodes.filter((n) => isEntityLabel(n.label)).length} facts &amp; events
        </span>
      </div>
      <Legend temporal={isTemporal(payload)} perceptions={perceptions} />
      {mode === "2d" && <ZoomSlider api={slider} onZoom={(k) => setZoomRequest({ k })} />}

      {details && selectedNode && rows && (
        <div
          ref={pop}
          role="dialog"
          aria-label={`About ${selectedNode.display}`}
          className="absolute top-0 left-0 max-w-320 rounded-8 border border-divider bg-panel px-12 py-10 text-13 text-fg shadow-lg"
          style={{ visibility: "hidden" }}
        >
          <div className="text-14 font-bold">{selectedNode.display}</div>
          <div className="mb-6 text-12 text-fg-muted">
            {selectedNode.label} · {rows.total} revealed row{rows.total === 1 ? "" : "s"} by chapter {chapter}
            {spoilers ? " (spoilers on)" : ""}
          </div>
          <ul className="m-0 flex list-none flex-col gap-4 p-0">
            {rows.rows.map((r, i) => (
              <li key={`${r.id}-${i}`} className="leading-[1.35]">
                {r.text} <span className="font-mono text-11 text-fg-muted">{r.detail}</span>
              </li>
            ))}
          </ul>
          {rows.total > rows.rows.length && <div className="mt-6 text-12 text-fg-muted">+{rows.total - rows.rows.length} more</div>}
          {rosterNodes?.has(selectedNode.id) && onChat && (
            <div className="mt-8">
              <KitButton size="xs" onClick={() => onChat(rosterNodes.get(selectedNode.id)!)}>
                Chat
              </KitButton>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
