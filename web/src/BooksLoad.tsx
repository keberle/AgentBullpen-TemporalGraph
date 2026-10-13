import { KitButton } from "./design-kit/components/KitButton.tsx";

// What the first screen shows while the books query is on its way. Deployed, the API sleeps when nobody is using it, so the
// first call after a quiet spell waits for it to start (up to a minute): the page says so instead of showing an empty list.
// The book list and a link straight to a book show the same three states.

/** The books query: pending, pending for more than `SLOW_MS`, failed (or given up after `TIMEOUT_MS`), answered. */
export type BooksState = "loading" | "slow" | "timed-out" | "ready";

/** When the "it sleeps" line joins the status. */
export const SLOW_MS = 10_000;
/** When the books query is given up: under the site gateway's own timeout, so the page's message is the one a visitor sees. */
export const TIMEOUT_MS = 150_000;

const pulse = "animate-[abp-pulse_1.6s_ease-in-out_infinite] motion-reduce:animate-none";

/** The status line with its spinner; `slow` adds why it takes this long. */
export function BooksWaiting({ slow }: { slow: boolean }) {
  return (
    <div role="status" aria-live="polite" className="flex flex-col items-center gap-6 text-center">
      <span className="flex items-center gap-10 text-16 font-semibold">
        <span className="box-content size-14 animate-[abp-spin_0.9s_linear_infinite] rounded-999 border-2 border-outline border-t-accent motion-reduce:animate-none" />
        Starting the temporal graph…
      </span>
      {slow && (
        <span className="max-w-520 text-14 leading-[1.5] text-fg-muted">
          It sleeps when nobody is using it. The first visit after a quiet spell takes up to a minute.
        </span>
      )}
    </div>
  );
}

/** The books query failed or was given up; "Try again" runs it again. */
export function BooksFailed({ onRetry }: { onRetry: () => void }) {
  return (
    <div role="alert" className="box-border flex w-full max-w-720 items-center gap-16 rounded-12 border border-outline bg-panel px-16 py-14">
      <span className="grow text-15 leading-[1.5]">The temporal graph didn't start. Try again in a moment.</span>
      <KitButton variant="outline" size="action" className="shrink-0" onClick={onRetry}>
        Try again
      </KitButton>
    </div>
  );
}

/** A book card's place while the books load: the hero's 3:2 block, then a bar each for the title and the author. */
export function BookCardSkeleton() {
  return (
    <div aria-hidden="true" className="box-border flex flex-col overflow-hidden rounded-16 border border-divider bg-panel">
      <div className={`aspect-[3/2] w-full bg-track ${pulse}`} />
      {/* min-h-52: the title and author lines of a real card, so the page keeps its height when the books arrive. */}
      <div className="flex min-h-52 flex-col justify-center gap-10 p-28">
        <div className={`h-18 w-3/4 rounded-6 bg-track ${pulse}`} />
        <div className={`h-12 w-1/2 rounded-6 bg-track ${pulse}`} />
      </div>
    </div>
  );
}
