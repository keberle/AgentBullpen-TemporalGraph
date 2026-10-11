import { useEffect, useState } from "react";
import BookList from "./BookList.tsx";
import BookView from "./BookView.tsx";
import { gql, queries, type Book } from "./graphql.ts";
import { home, navigate, useRoute } from "./route.ts";
import { startSite, useSite } from "./site.ts";

// The two screens of the prototype (pick a book, then the book view). Chosen by the URL (route.ts), so a reload, a pasted
// link or Back/Forward opens the same screen, tab, chapter, spoilers, facts and chat.
export default function App() {
  const route = useRoute();
  const [books, setBooks] = useState<Book[] | null>(null);

  // Behind the site, first: who is this (signed out → the site's sign-in page), then its app status (the
  // under-construction page unless live, or the gateway lets this caller through). Standalone the state is ready at once.
  const site = useSite();
  const show = site.ready && !site.signedOut;
  useEffect(() => {
    void startSite();
  }, []);

  useEffect(() => {
    if (!show) return;
    gql<{ books: Book[] }>(queries.books)
      .then((d) => setBooks(d.books))
      .catch(() => setBooks([]));
  }, [show]);

  // A path the app doesn't know (e.g. /nope, /book/x/4d) shows the book list under its own URL.
  useEffect(() => {
    if (!route.known) navigate(home, "replace");
  }, [route.known]);

  const book = route.book == null ? null : (books?.find((b) => b.id === route.book) ?? null);
  useEffect(() => {
    document.title = book ? `${book.title} · Temporal Graph` : "Temporal Graph";
  }, [book]);

  // Behind the site nothing renders until the member is known, and nothing at all for a signed-out visitor.
  if (!show) return null;
  if (route.book == null) return <BookList books={books ?? []} onPick={(b) => navigate({ ...home, book: b.id })} />;
  if (books == null) return null; // the manifest is loading
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
