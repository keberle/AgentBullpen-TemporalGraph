import { TopBar } from "./design-kit/components/TopBar.tsx";
import { BookCard } from "./BookCard.tsx";
import { BookCardSkeleton, BooksFailed, BooksWaiting, type BooksState } from "./BooksLoad.tsx";
import { SiteLinks, SiteUserMenu, SiteWordmark } from "./SiteChrome.tsx";
import type { Book } from "./graphql.ts";

// "Pick a book" screen, ported from the site's prototype. Books come from the
// GraphQL api (fetched once by App); clicking a card opens the book view.
// Until they arrive the screen says the API is starting, over two skeleton cards (BooksLoad.tsx).

export default function BookList({
  books,
  state,
  onRetry,
  onPick,
}: {
  books: Book[];
  state: BooksState;
  onRetry: () => void;
  onPick: (book: Book) => void;
}) {
  const waiting = state === "loading" || state === "slow";
  return (
    <div className="flex h-screen flex-col">
      <TopBar
        right={<SiteUserMenu />}
        left={
          <>
            <div className="flex items-center gap-16">
              <SiteWordmark />
              <span className="text-15 text-fg-muted">/ Temporal Graph</span>
            </div>
            <SiteLinks />
          </>
        }
      />
      {/* basis-0: the list takes the height left under the top bar and scrolls inside it (else a tall list squeezes the 64 px bar). */}
      <main className="flex min-h-0 grow basis-0 flex-col items-center gap-32 overflow-y-auto px-24 py-56">
        <div className="flex flex-col items-center gap-8 text-center">
          <h1 className="m-0 text-40 font-extrabold tracking-[-1px]">Pick a book</h1>
          <p className="m-0 text-17 text-fg-muted">Then pick a moment in the story and a character to talk to.</p>
        </div>
        {state === "timed-out" && <BooksFailed onRetry={onRetry} />}
        {waiting && <BooksWaiting slow={state === "slow"} />}
        {state !== "timed-out" && (
          <div className="grid w-full max-w-720 grid-cols-[repeat(auto-fit,minmax(min(260px,100%),1fr))] gap-20">
            {waiting ? (
              <>
                <BookCardSkeleton />
                <BookCardSkeleton />
              </>
            ) : (
              books.map((b) => <BookCard key={b.id} title={b.title} author={b.author} hero={b.hero} onClick={() => onPick(b)} />)
            )}
          </div>
        )}
        {state === "ready" && <span className="text-13 text-fg-muted">More books later</span>}
      </main>
    </div>
  );
}
