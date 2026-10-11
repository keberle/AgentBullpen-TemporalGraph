import { indexFile, pagePath, pageByFile } from "./toc.ts";

// How the relative links written in docs/*.md become URLs in the web app. The markdown keeps the links that work on
// GitHub and in a clone (`configuration.md`, `../design/chat.md#open`, `../diagrams/two-clocks.svg`); the renderer maps them.

// The sheets, as built asset URLs, keyed by their path under docs/ ("diagrams/two-clocks.svg").
const sheetModules = import.meta.glob("../../../docs/diagrams/*.svg", { eager: true, query: "?url", import: "default" }) as Record<string, string>;
export const sheets: Readonly<Record<string, string>> = Object.fromEntries(
  Object.entries(sheetModules).map(([k, url]) => [k.replace("../../../docs/", ""), url]),
);

/** `href` resolved against the folder of `fromFile`, both relative to docs/; null when it climbs out of docs/. */
export function resolveDocPath(fromFile: string, href: string): string | null {
  const parts = fromFile.split("/").slice(0, -1);
  for (const seg of href.split("/")) {
    if (seg === "" || seg === ".") continue;
    if (seg === "..") {
      if (parts.length === 0) return null;
      parts.pop();
    } else parts.push(seg);
  }
  return parts.join("/");
}

export type ResolvedLink =
  | { kind: "route"; to: string } // another docs page: a client-side link
  | { kind: "anchor"; href: string } // #id on this page
  | { kind: "external"; href: string } // http(s), mailto: a new tab
  | { kind: "asset"; href: string } // a sheet linked rather than shown
  | { kind: "missing"; href: string }; // a relative link to nothing the docs have (toc.test.ts fails on these)

export function resolveLink(href: string, fromFile: string): ResolvedLink {
  if (href.startsWith("#")) return { kind: "anchor", href };
  if (/^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith("//")) return { kind: "external", href };
  const hashAt = href.indexOf("#");
  const path = hashAt < 0 ? href : href.slice(0, hashAt);
  const hash = hashAt < 0 ? "" : href.slice(hashAt);
  const target = resolveDocPath(fromFile, path);
  if (target == null) return { kind: "missing", href };
  if (target === indexFile) return { kind: "route", to: `/docs${hash}` };
  const page = pageByFile(target);
  if (page) return { kind: "route", to: pagePath(page) + hash };
  if (sheets[target]) return { kind: "asset", href: sheets[target] };
  return { kind: "missing", href };
}

/** The built URL of a sheet an `img` names (`../diagrams/two-clocks.svg`), or null. */
export function sheetUrl(src: string, fromFile: string): string | null {
  const target = resolveDocPath(fromFile, src);
  return target == null ? null : (sheets[target] ?? null);
}
