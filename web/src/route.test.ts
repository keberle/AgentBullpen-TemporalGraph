import { describe, expect, it } from "vitest";
import { chatSync, COALESCE_MS, formatRoute, historyMode, home, parseRoute, slug, type Route } from "./route.ts";

const parse = (url: string) => {
  const u = new URL(url, "http://x");
  return parseRoute(u.pathname, u.search);
};
const hound: Route = { ...home, book: "hound-of-the-baskervilles" };

describe("URL routes", () => {
  it("names the book list, the book view's tabs and its view state", () => {
    expect(parse("/")).toEqual({ ...home, known: true });
    expect(parse("/book/hound-of-the-baskervilles")).toEqual({ ...hound, known: true });
    expect(parse("/book/hound-of-the-baskervilles/3d?ch=7&spoilers=1&facts=1&chat=dr-watson")).toEqual({
      ...hound,
      tab: "3d",
      chapter: 7,
      spoilers: true,
      facts: true,
      chat: "dr-watson",
      known: true,
    });
    expect(parse("/book/hound-of-the-baskervilles/query").tab).toBe("query");
    expect(parse("/book/hound-of-the-baskervilles/").tab).toBe("2d"); // a trailing slash is the same view
  });

  it("formats the shortest URL (defaults left out) and round-trips", () => {
    expect(formatRoute(home)).toBe("/");
    expect(formatRoute(hound)).toBe("/book/hound-of-the-baskervilles");
    const r: Route = { ...hound, tab: "query", chapter: 12, spoilers: true, facts: false, chat: "mr-sherlock-holmes" };
    expect(formatRoute(r)).toBe("/book/hound-of-the-baskervilles/query?ch=12&spoilers=1&chat=mr-sherlock-holmes");
    for (const x of [home, hound, r, { ...hound, tab: "3d" as const, facts: true }]) expect(parse(formatRoute(x))).toEqual({ ...x, known: true });
    expect(formatRoute({ ...hound, book: "a b/c" })).toBe("/book/a%20b%2Fc");
    expect(parse("/book/a%20b%2Fc").book).toBe("a b/c");
  });

  it("ignores bad values and marks unknown paths", () => {
    const bad = parse("/book/hound-of-the-baskervilles?ch=0&spoilers=yes&chat=%20%20");
    expect([bad.chapter, bad.spoilers, bad.chat]).toEqual([1, false, null]);
    expect(parse("/book/hound-of-the-baskervilles?ch=2.5").chapter).toBe(1);
    expect(parse("/book/hound-of-the-baskervilles?spoilers=true&chat=Dr.%20Watson")).toMatchObject({ spoilers: true, chat: "dr-watson" });
    for (const u of ["/nope", "/book", "/book/x/4d", "/book/x/3d/more", "/books/hound-of-the-baskervilles.webp", "/book/%E0%A4%A"])
      expect(parse(u)).toEqual({ ...home, known: false });
  });

  it("slugs roster names", () => {
    expect(slug("Mr. Sherlock Holmes")).toBe("mr-sherlock-holmes");
    expect(slug("Dr. James Mortimer")).toBe("dr-james-mortimer");
    expect(slug("  Mrs. Laura Lyons ")).toBe("mrs-laura-lyons");
    expect(slug("D'Artagnan")).toBe("d-artagnan");
    expect(slug("Athos — Comte de La Fère")).toBe("athos-comte-de-la-fere");
  });

  it("coalesces a run of slider moves into one history entry; other changes push", () => {
    const t = 10_000;
    expect(historyMode("push", { kind: "chapter", t }, t + 10)).toBe("push");
    expect(historyMode("chapter", null, t)).toBe("push");
    expect(historyMode("chapter", { kind: "push", t }, t + 10)).toBe("push");
    expect(historyMode("chapter", { kind: "chapter", t }, t + 10)).toBe("replace");
    expect(historyMode("chapter", { kind: "chapter", t }, t + COALESCE_MS)).toBe("push");
    expect(historyMode("replace", null, t)).toBe("replace");
  });

  it("syncs the open chat from the URL once the roster is known", () => {
    const roster = [
      { name: "Mr. Sherlock Holmes", nodeId: 1 },
      { name: "Dr. Watson", nodeId: 3 },
    ];
    expect(chatSync(null, null, roster)).toEqual({ type: "none" });
    expect(chatSync("dr-watson", "Dr. Watson", roster)).toEqual({ type: "none" });
    expect(chatSync("dr-watson", null, roster)).toEqual({ type: "open", name: "Dr. Watson", nodeId: 3 });
    expect(chatSync("dr-watson", "Mr. Sherlock Holmes", roster)).toEqual({ type: "open", name: "Dr. Watson", nodeId: 3 });
    expect(chatSync(null, "Dr. Watson", roster)).toEqual({ type: "close" });
    // Not offered at this chapter (or no such character): the parameter is dropped.
    expect(chatSync("mrs-laura-lyons", null, roster)).toEqual({ type: "drop" });
  });
});
