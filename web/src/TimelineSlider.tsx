import { useRef } from "react";
import { nearestStop, type TimelineLayout } from "./graph/model.ts";

// The timeline slider of a book that ships its timeline (model.ts timelineLayout). Its stops are still the chapters
// (the chat, the Graph Query tab and `?ch=` take a chapter), but its track is drawn from the timeline: a tick per chapter and a
// smaller tick per event, and with Spoilers on the track is story time, so the stops are not evenly spaced and the thumb sits at
// story(pos) of the selected chapter. That is why this is not Fluent's Slider, which can only space its steps evenly; a book
// without a timeline keeps that one (BookView).
//
// Shapes, not colours, say what a tick is: a chapter is an upright bar on the track; an event is a small dot under it, filled when
// narrated and hollow when referenced (told about, not shown). Ticks the reader has not reached are fainter and have no tooltip.
// The anchor's note (the one assumed date) is the track's tooltip and description, labelled as an assumption.

/** Half the thumb's width: the track is inset by it, so the thumb stays inside the slider at both ends. */
const INSET = 10;

export default function TimelineSlider({
  id,
  layout,
  chapter,
  onChange,
  className,
}: {
  id?: string;
  layout: TimelineLayout;
  chapter: number;
  onChange: (chapter: number) => void;
  className?: string;
}) {
  const track = useRef<HTMLDivElement>(null);
  const first = layout.stops[0]?.chapter ?? 1;
  const last = layout.stops[layout.stops.length - 1]?.chapter ?? 1;
  const go = (n: number) => {
    const to = Math.max(first, Math.min(last, n));
    if (to !== chapter) onChange(to);
  };
  // A press or drag picks the chapter whose stop is nearest the pointer.
  const pick = (clientX: number) => {
    const box = track.current?.getBoundingClientRect();
    if (!box || box.width <= 0) return;
    go(nearestStop(layout.stops, Math.max(0, Math.min(1, (clientX - box.left) / box.width)), chapter));
  };
  const left = (at: number) => `${(at * 100).toFixed(3)}%`;
  const assumption = layout.note ? `Assumption: ${layout.note}` : undefined;
  const axis = layout.axis === "story" ? "story time" : "the text";

  return (
    <div
      id={id}
      role="slider"
      tabIndex={0}
      aria-label="Reader is at"
      aria-orientation="horizontal"
      aria-valuemin={first}
      aria-valuemax={last}
      aria-valuenow={chapter}
      aria-valuetext={`Chapter ${chapter}`}
      aria-description={[`Laid out by ${axis}.`, assumption].filter(Boolean).join(" ")}
      data-axis={layout.axis}
      title={assumption}
      className={`relative h-44 cursor-pointer touch-none select-none rounded-6 outline-offset-2 focus-visible:outline-2 focus-visible:outline-accent ${className ?? ""}`}
      onKeyDown={(e) => {
        const to =
          e.key === "ArrowRight" || e.key === "ArrowUp"
            ? chapter + 1
            : e.key === "ArrowLeft" || e.key === "ArrowDown"
              ? chapter - 1
              : e.key === "PageUp"
                ? chapter + 5
                : e.key === "PageDown"
                  ? chapter - 5
                  : e.key === "Home"
                    ? first
                    : e.key === "End"
                      ? last
                      : null;
        if (to === null) return;
        e.preventDefault();
        go(to);
      }}
      onPointerDown={(e) => {
        if (e.button !== 0) return;
        e.currentTarget.setPointerCapture?.(e.pointerId);
        e.currentTarget.focus();
        pick(e.clientX);
      }}
      onPointerMove={(e) => {
        if (e.buttons & 1 && e.currentTarget.hasPointerCapture?.(e.pointerId)) pick(e.clientX);
      }}
    >
      <div ref={track} className="absolute inset-y-0" style={{ left: INSET, right: INSET }}>
        {/* The rail, and the part the reader has covered. */}
        <div className="absolute inset-x-0 top-1/2 h-4 -translate-y-1/2 rounded-999" style={{ background: "var(--abp-outline)" }} />
        <div className="absolute left-0 top-1/2 h-4 -translate-y-1/2 rounded-999 bg-accent" style={{ width: left(layout.thumb) }} />
        {layout.ticks.map((t) =>
          t.kind === "chapter" ? (
            <span
              key={t.key}
              data-tick="chapter"
              title={t.label ? `Chapter ${t.refId}: ${t.label}` : `Chapter ${t.refId}`}
              className="absolute top-1/2 h-14 w-2 -translate-x-1/2 -translate-y-1/2 rounded-999"
              style={{ left: left(t.at), background: "var(--abp-fg)", opacity: t.shown ? 0.9 : 0.4 }}
            />
          ) : (
            <span
              key={t.key}
              data-tick={t.referenced ? "event-referenced" : "event-narrated"}
              title={t.label ? `${t.referenced ? "Told about" : "Event"}: ${t.label}` : undefined}
              className="absolute box-border h-6 w-6 -translate-x-1/2 rounded-999"
              style={{
                left: left(t.at),
                top: "calc(50% + 9px)",
                // Filled = narrated, hollow = referenced.
                background: t.referenced ? "transparent" : "var(--abp-fg-muted)",
                borderStyle: t.referenced ? "solid" : "none",
                borderWidth: t.referenced ? 1.5 : 0,
                borderColor: "var(--abp-fg)",
                opacity: t.shown ? 1 : 0.3,
              }}
            />
          ),
        )}
        <div
          data-thumb=""
          className="pointer-events-none absolute top-1/2 h-20 w-20 -translate-x-1/2 -translate-y-1/2 rounded-999 bg-accent"
          style={{ left: left(layout.thumb), boxShadow: "0 0 0 2px var(--abp-page)" }}
        />
      </div>
    </div>
  );
}
