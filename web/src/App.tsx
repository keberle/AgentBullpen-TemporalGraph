import { useEffect, useState } from "react";
import BookList from "./BookList.tsx";
import { BooksFailed, BooksWaiting, SLOW_MS, TIMEOUT_MS, type BooksState } from "./BooksLoad.tsx";
import BookView from "./BookView.tsx";
import { AppUnavailableError, gql, queries, SignedOutError, type Book } from "./graphql.ts";
import { home, navigate, useRoute } from "./route.ts";
import { startSite, useSite } from "./site.ts";

// The two screens of the prototype (pick a book, then the book view). Chosen by the URL (route.ts), so a reload, a pasted
// link or Back/Forward opens the same screen, tab, chapter, spoilers, facts and chat.
export default function App() {
  const route = useRoute();
  const [books, setBooks] = useState<Book[] | null>(null);
  // The books query (BooksLoad.tsx): deployed, the first one after a quiet spell waits for the API to start.
  const [state, setState] = useState<BooksState>("loading");
  const [attempt, setAttempt] = useState(0);

  // Behind the site, first: who is this (anyone the site doesn't confirm as a member → its sign-in page), then its app status (the
  // under-construction page unless live, or the gateway lets this caller through). Standalone the state is ready at once.
  const site = useSite();
  const show = site.ready && !site.signedOut;
  useEffect(() => {
    void startSite();
  }, []);

  useEffect(() => {
    if (!show) return;
    let current = true; // false once this run is replaced (Try again) or the app is gone
    const slow = setTimeout(() => current && setState((s) => (s === "loading" ? "slow" : s)), SLOW_MS);
    gql<{ books: Book[] }>(queries.books, undefined, AbortSignal.timeout(TIMEOUT_MS))
      .then((d) => {
        if (!current) return;
        setBooks(d.books);
        setState("ready");
      })
      .catch((e: unknown) => {
        if (!current) return;
        // The visitor is on their way to the site's sign-in or under-construction page: the status stays until the page goes.
        if (e instanceof AppUnavailableError || (e instanceof SignedOutError && e.redirected)) return;
        setState("timed-out"); // any other failure, the timeout's abort included
      })
      .finally(() => clearTimeout(slow));
    return () => {
      current = false;
      clearTimeout(slow);
    };
  }, [show, attempt]);

  const retry = () => {
    setState("loading");
    setAttempt((n) => n + 1);
  };

  // A path the app doesn't know (e.g. /nope, /book/x/4d) shows the book list under its own URL.
  useEffect(() => {
    if (!route.known) navigate(home, "replace");
  }, [route.known]);

  const book = route.book == null ? null : (books?.find((b) => b.id === route.book) ?? null);
  useEffect(() => {
    document.title = book ? `${book.title} · Temporal Graph` : "Temporal Graph";
  }, [book]);

  // Behind the site nothing renders until the site has confirmed the member, and nothing at all for anyone else.
  if (!show) return null;
  if (route.book == null)
    return <BookList books={books ?? []} state={state} onRetry={retry} onPick={(b) => navigate({ ...home, book: b.id })} />;
  // A link straight to a book waits for the manifest like the book list does.
  if (state !== "ready")
    return (
      <div className="flex h-screen flex-col items-center justify-center px-24">
        {state === "timed-out" ? <BooksFailed onRetry={retry} /> : <BooksWaiting slow={state === "slow"} />}
      </div>
    );
  if (!book)
    return (
      <div className="flex h-screen flex-col items-center justify-center gap-16">
        <p className="m-0 text-17">There is no book "{route.book}".</p>
        <a
          href="/"
          className="text-15 text-accent"
          onClick={(e) => {
            e.preventDefault();
            navigate(home);
          }}
        >
          Pick a book
        </a>
      </div>
    );
  return <BookView key={book.id} book={book} route={route} onChangeBook={() => navigate(home)} />;
}
