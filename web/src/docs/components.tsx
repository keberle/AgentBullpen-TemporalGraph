import { Children, isValidElement, type ComponentPropsWithoutRef, type ReactElement, type ReactNode } from "react";
import type { MDXComponents } from "mdx/types";
import { Link } from "react-router";
import { resolveLink, sheetUrl } from "./links.ts";

// Markdown elements → the approved page's parts (mockup docs-page.html), kit tokens only. `file` is the page's path
// under docs/, against which its relative links and images resolve.

const has = (className: unknown, c: string) => typeof className === "string" && className.split(" ").includes(c);

// Ligatures off: JetBrains Mono would draw `<=` as ≤, which isn't what a reader should type.
const codeChip = "rounded-6 bg-panel px-6 py-1 font-mono text-[0.92em] text-fg [font-variant-ligatures:none]";

export function DocLink({ href = "", file, children }: { href?: string; file: string; children?: ReactNode }) {
  const link = resolveLink(href, file);
  const cls = "text-accent underline underline-offset-[3px] hover:text-fg";
  switch (link.kind) {
    case "route":
      return (
        <Link to={link.to} className={cls}>
          {children}
        </Link>
      );
    case "external":
      return (
        <a href={link.href} target="_blank" rel="noreferrer" className={cls}>
          {children}
        </a>
      );
    case "anchor":
    case "asset":
      return (
        <a href={link.href} className={cls}>
          {children}
        </a>
      );
    case "missing":
      return <span>{children}</span>; // toc.test.ts fails the build's tests before a page ships one of these
  }
}

/**
 * The sheet frame: outline, 16 px radius, scrolls sideways inside its frame below 760 px; the alt text is the caption. The sheet's
 * minimum is 758 px, the 760 px column less the frame's two 1 px borders, so at full width it fits instead of scrolling by 2 px.
 */
export function Sheet({ src = "", alt = "", file }: { src?: string; alt?: string; file: string }) {
  const url = sheetUrl(src, file) ?? (/^https?:/.test(src) ? src : null);
  return (
    <figure className="m-0 flex flex-col gap-10">
      <div className="overflow-x-auto rounded-16 border border-divider bg-page">
        {url ? (
          <img src={url} alt={alt} className="block h-auto w-full min-w-[758px]" />
        ) : (
          <p className="m-0 p-20 text-15 text-fg-muted">Missing sheet: {src}</p>
        )}
      </div>
      {alt && <figcaption className="text-13 text-fg-muted">{alt}</figcaption>}
    </figure>
  );
}

function Pre({ children }: ComponentPropsWithoutRef<"pre">) {
  // The fenced block's <code> child: its text goes in the panel block, without the inline chip's styling.
  const only = Children.only(children);
  const text = isValidElement(only) ? (only as ReactElement<{ children?: ReactNode }>).props.children : only;
  return (
    <pre className="m-0 overflow-x-auto rounded-12 border border-divider bg-panel px-20 py-16 font-mono text-14 leading-[1.6] text-fg [font-variant-ligatures:none]">
      <code>{text}</code>
    </pre>
  );
}

export function docComponents(file: string): MDXComponents {
  return {
    a: ({ href, children }) => (
      <DocLink href={href} file={file}>
        {children}
      </DocLink>
    ),
    img: ({ src, alt }) => <Sheet src={typeof src === "string" ? src : ""} alt={alt} file={file} />,
    h1: ({ id, children }) => (
      <h1 id={id} className="m-0 text-44 leading-[1.1] font-extrabold tracking-[-1px] text-fg">
        {children}
      </h1>
    ),
    h2: ({ id, children }) => (
      <h2 id={id} className="mt-16 mb-0 text-26 font-bold text-fg">
        {children}
      </h2>
    ),
    h3: ({ id, children }) => (
      <h3 id={id} className="mt-8 mb-0 text-20 font-bold text-fg">
        {children}
      </h3>
    ),
    p: ({ className, children }) =>
      has(className, "doc-sources") ? (
        // The "_Guide · sources: …_" line: muted and eyebrow-sized under the H1, not body text.
        <p className="m-0 text-14 leading-[1.5] text-fg-muted [&>em]:not-italic">{children}</p>
      ) : (
        <p className="m-0 text-17 leading-[1.7] text-fg-muted">{children}</p>
      ),
    strong: ({ children }) => <strong className="font-bold text-fg">{children}</strong>,
    code: ({ children }) => <code className={codeChip}>{children}</code>,
    pre: Pre,
    ul: ({ children }) => <ul className="m-0 flex flex-col gap-6 pl-24 text-17 leading-[1.7] text-fg-muted">{children}</ul>,
    ol: ({ children, start }) => (
      <ol start={start} className="m-0 flex flex-col gap-6 pl-24 text-17 leading-[1.7] text-fg-muted">
        {children}
      </ol>
    ),
    blockquote: ({ children }) => (
      // The Note box. Its first paragraph runs on after the label, as in the mockup.
      <div className="rounded-12 border border-outline px-20 py-16 text-16 leading-[1.6] [&_p]:text-16 [&_p]:leading-[1.6] [&>p:first-of-type]:inline">
        <span className="font-bold text-accent">Note</span> <span className="text-fg-muted">— </span>
        {children}
      </div>
    ),
    table: ({ children }) => (
      <div className="overflow-x-auto rounded-12 border border-divider">
        <table className="w-full border-collapse text-15 leading-[1.5] [&_tr:last-child>td]:border-b-0">{children}</table>
      </div>
    ),
    th: ({ children, style }) => (
      <th style={style} className="border-b border-divider px-14 py-10 text-left align-bottom font-semibold text-fg">
        {children}
      </th>
    ),
    td: ({ children, style }) => (
      <td style={style} className="min-w-[96px] border-b border-divider px-14 py-10 align-top text-fg-muted">
        {children}
      </td>
    ),
    hr: () => <hr className="m-0 w-full border-0 border-t border-divider" />,
    section: ({ className, children }) =>
      has(className, "doc-open") ? (
        // "Open" / "Still open" / "Not built yet": the outlined panel (the mockup's "Still open" card), heading as its label.
        <section className="flex flex-col gap-10 rounded-12 border border-divider bg-panel px-20 py-16 [&_h2]:m-0 [&_h2]:text-12 [&_h2]:font-bold [&_h2]:tracking-[1.5px] [&_h2]:text-accent [&_h2]:uppercase [&_li]:text-15 [&_li]:leading-[1.55] [&_ol]:text-15 [&_p]:text-15 [&_p]:leading-[1.55] [&_ul]:text-15">
          {children}
        </section>
      ) : (
        <section className={className}>{children}</section>
      ),
  };
}
