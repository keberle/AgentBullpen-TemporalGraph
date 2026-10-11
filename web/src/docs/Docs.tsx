import { lazy, Suspense, useEffect, useMemo, type ComponentType } from "react";
import { MDXProvider } from "@mdx-js/react";
import type { MDXProps } from "mdx/types";
import { Link, Route, Routes, useLocation, useParams } from "react-router";
import { TopBar } from "../design-kit/components/TopBar.tsx";
import { Wordmark } from "../design-kit/components/Wordmark.tsx";
import { docComponents } from "./components.tsx";
import { loaders } from "./pages.ts";
import { eyebrow, findPage, groups, indexFile, neighbours, pagePath, type DocPage } from "./toc.ts";

// The docs, at /docs and /docs/<group>/<page>: the repo's docs/*.md in the approved page design (docs-page.html).
// Public, no sign-in, and on purpose no link to or from the demo: the two are separate pathways, reachable by URL.

const pages = Object.fromEntries(Object.entries(loaders).map(([file, load]) => [file, lazy(load)])) as Record<string, ComponentType<MDXProps>>;

export default function Docs() {
  return (
    <div className="flex min-h-screen flex-col">
      <TopBar
        left={
          <div className="flex flex-wrap items-center gap-16">
            <Wordmark />
            <span className="text-15 text-fg-muted">
              / Temporal Graph ·{" "}
              <Link to="/docs" className="text-fg-muted no-underline hover:text-fg">
                Docs
              </Link>
            </span>
          </div>
        }
      />
      <Routes>
        <Route index element={<Layout file={indexFile} page={null} />} />
        <Route path=":group/:slug" element={<PageRoute />} />
        <Route path="*" element={<Layout file={null} page={null} />} />
      </Routes>
    </div>
  );
}

function PageRoute() {
  const { group, slug } = useParams();
  const page = findPage(group, slug);
  return <Layout file={page?.file ?? null} page={page} />;
}

function Layout({ file, page }: { file: string | null; page: DocPage | null }) {
  const title = page ? `${page.title} · Temporal Graph docs` : file ? "Temporal Graph docs" : "Not found · Temporal Graph docs";
  return (
    <div className="mx-auto box-border flex w-full max-w-[1240px] flex-wrap gap-x-56 px-24">
      <title>{title}</title>
      <Menu current={page} />
      <main className="flex min-w-0 max-w-[760px] flex-[999_1_560px] flex-col gap-24 pt-40 pb-96">
        {file ? (
          <>
            {page && <span className="text-14 text-fg-muted">{eyebrow(page)}</span>}
            <Suspense fallback={null}>
              <Article file={file} />
            </Suspense>
            {page && <Pager page={page} />}
          </>
        ) : (
          <>
            <h1 className="m-0 text-44 leading-[1.1] font-extrabold tracking-[-1px] text-fg">No such page</h1>
            <p className="m-0 text-17 leading-[1.7] text-fg-muted">
              The docs have no page at this address.{" "}
              <Link to="/docs" className="text-accent underline underline-offset-[3px] hover:text-fg">
                All pages
              </Link>
            </p>
          </>
        )}
      </main>
    </div>
  );
}

function Menu({ current }: { current: DocPage | null }) {
  return (
    <nav aria-label="Docs" className="flex max-w-[260px] flex-[1_1_220px] flex-col gap-28 py-40 text-15">
      {groups.map((g) => (
        <div key={g.key} className="flex flex-col gap-4">
          <span className="pb-6 text-12 font-bold tracking-[1.5px] text-fg-muted">{g.eyebrow}</span>
          {g.pages.map((p) => {
            const on = p === current;
            return (
              <Link
                key={p.slug}
                to={pagePath(p)}
                aria-current={on ? "page" : undefined}
                className={`box-border flex min-h-36 items-center rounded-8 px-12 leading-[1.35] no-underline ${on ? "bg-panel text-fg" : "text-fg-muted hover:text-fg"}`}
              >
                {p.label}
              </Link>
            );
          })}
        </div>
      ))}
    </nav>
  );
}

function Article({ file }: { file: string }) {
  const Content = pages[file];
  const components = useMemo(() => docComponents(file), [file]);
  return (
    <MDXProvider components={components}>
      <Content />
      <ScrollOnShow />
    </MDXProvider>
  );
}

/** Once a page has rendered: to its #anchor when the URL has one (again once the sheets above it have loaded), else to the top. */
function ScrollOnShow() {
  const { pathname, hash } = useLocation();
  useEffect(() => {
    const id = hash ? decodeURIComponent(hash.slice(1)) : null;
    const toAnchor = () => document.getElementById(id ?? "")?.scrollIntoView();
    if (!id || !document.getElementById(id)) {
      window.scrollTo(0, 0); // not returned: Chromium's scrollTo now returns a Promise, which an effect must not
      return;
    }
    toAnchor();
    let live = true;
    const loaded = (i: HTMLImageElement) =>
      new Promise((done) => {
        i.addEventListener("load", done, { once: true });
        i.addEventListener("error", done, { once: true });
      });
    const pending = [...document.querySelectorAll<HTMLImageElement>("main img")].filter((i) => !i.complete);
    if (pending.length) Promise.all(pending.map(loaded)).then(() => live && toAnchor());
    return () => {
      live = false;
    };
  }, [pathname, hash]);
  return null;
}

function Pager({ page }: { page: DocPage }) {
  const { prev, next } = neighbours(page);
  const cls = "font-semibold text-accent no-underline hover:text-fg";
  return (
    <nav aria-label="Pager" className="flex justify-between gap-16 border-t border-divider pt-24 text-15">
      {prev ? (
        <Link to={pagePath(prev)} className={cls}>
          ← {prev.label}
        </Link>
      ) : (
        <span />
      )}
      {next && (
        <Link to={pagePath(next)} className={`${cls} text-right`}>
          Next: {next.label} →
        </Link>
      )}
    </nav>
  );
}
