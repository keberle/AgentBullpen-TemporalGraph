import { renderToStaticMarkup } from "react-dom/server";
import type { ReactNode } from "react";
import { MDXProvider } from "@mdx-js/react";
import { MemoryRouter } from "react-router";
import { describe, expect, it } from "vitest";
import { DocLink, docComponents, Sheet } from "./components.tsx";
import { resolveLink } from "./links.ts";
import { loaders } from "./pages.ts";
import { rehypeDocs } from "./rehypeDocs.ts";
import { allPages, indexFile } from "./toc.ts";

const html = (node: ReactNode) => renderToStaticMarkup(<MemoryRouter>{node}</MemoryRouter>);

describe("the a component", () => {
  it("turns a relative .md link into the page's route, keeping the #anchor", () => {
    expect(resolveLink("configuration.md", "guide/getting-started.md")).toEqual({ kind: "route", to: "/docs/guide/configuration" });
    expect(resolveLink("../design/chat.md#open", "story/the-question.md")).toEqual({ kind: "route", to: "/docs/design/chat#open" });
    expect(resolveLink("design/two-clocks.md", "README.md")).toEqual({ kind: "route", to: "/docs/design/two-clocks" });
    expect(resolveLink("../README.md", "guide/configuration.md")).toEqual({ kind: "route", to: "/docs" });
    expect(html(<DocLink href="configuration.md" file="guide/getting-started.md">Configuration</DocLink>)).toMatch(
      /^<a [^>]*href="\/docs\/guide\/configuration"[^>]*>Configuration<\/a>$/,
    );
  });

  it("opens http(s) links in a new tab", () => {
    const a = html(<DocLink href="https://www.loc.gov/standards/datetime/" file="design/two-clocks.md">EDTF</DocLink>);
    expect(a).toContain('href="https://www.loc.gov/standards/datetime/"');
    expect(a).toContain('target="_blank"');
    expect(a).toContain('rel="noreferrer"');
  });

  it("keeps an in-page #anchor as it is", () => {
    expect(resolveLink("#open", "design/chat.md")).toEqual({ kind: "anchor", href: "#open" });
    const a = html(<DocLink href="#open" file="design/chat.md">Open</DocLink>);
    expect(a).toContain('href="#open"');
    expect(a).not.toContain("target");
  });

  it("reports a page that doesn't exist, and renders it as text, not a dead link", () => {
    expect(resolveLink("nope.md", "guide/configuration.md").kind).toBe("missing");
    expect(resolveLink("../../README.md", "guide/configuration.md").kind).toBe("missing"); // outside docs/
    expect(html(<DocLink href="nope.md" file="guide/configuration.md">Nope</DocLink>)).toBe("<span>Nope</span>");
  });
});

describe("the img component", () => {
  it("puts a sheet in its frame with the alt text as the caption", () => {
    const fig = html(<Sheet src="../diagrams/two-clocks.svg" alt="One edge, two clocks" file="design/two-clocks.md" />);
    expect(fig).toMatch(/<img src="[^"]*two-clocks[^"]*\.svg" alt="One edge, two clocks"/);
    expect(fig).toContain("min-w-[758px]");
    expect(fig).toContain("<figcaption");
  });

  it("says so when a sheet is missing", () => {
    expect(html(<Sheet src="../diagrams/nope.svg" alt="x" file="design/two-clocks.md" />)).toContain("Missing sheet");
  });
});

describe("the Note box", () => {
  it("renders a blockquote with the Note label", () => {
    const Quote = docComponents("design/two-clocks.md").blockquote as (p: { children: ReactNode }) => ReactNode;
    const box = html(<Quote>{<p>no maintained .NET EDTF library exists</p>}</Quote>);
    expect(box).toContain(">Note</span>");
    expect(box).toContain("border-outline");
  });
});

async function render(file: string) {
  const { default: Content } = await loaders[file]();
  return html(
    <MDXProvider components={docComponents(file)}>
      <Content />
    </MDXProvider>,
  );
}

describe("every page compiles and renders", () => {
  it.each([indexFile, ...allPages.map((p) => p.file)])("%s", async (file) => {
    const out = await render(file);
    expect(out).toMatch(/^(<link [^>]*\/>)*<h1 id="[^"]+"/); // React hoists a preload <link> for each image to the front
    expect(out).not.toContain("Missing sheet");
    expect(out).not.toMatch(/<em>(The story · \d|Guide( · |<)|Design &amp; research( · |<))/); // said once, by the eyebrow
  });

  // The pages' line under the H1 is the group alone ("_Design & research_"), with no sources.
  it("gives Two clocks the sheet frame, and leaves out the line that only repeats the eyebrow", async () => {
    const out = await render("design/two-clocks.md");
    expect(out).not.toContain("Design &amp; research</em>");
    expect(out).toMatch(/<\/h1>\s*<p[^>]*>[^<]*<\/p>\s*<figure/); // the title, the page's one-sentence lead, then the sheet
    expect(out).not.toMatch(/<p[^>]*>\s*<figure/); // the image paragraph is unwrapped
    expect(await render("design/chat.md")).not.toMatch(/<em>Design &amp; research<\/em>/);
  });

  it("keeps a sources line, without the group in front of it", () => {
    type N = { type: string; tagName?: string; value?: string; properties?: Record<string, unknown>; children?: N[] };
    const el = (tagName: string, ...children: N[]): N => ({ type: "element", tagName, properties: {}, children });
    const tree = el("root", el("h1", { type: "text", value: "Two clocks" }), el("p", el("em", { type: "text", value: "The story · 2 of 6 · sources: the design notes" })));
    rehypeDocs()(tree);
    const line = tree.children![1];
    expect(line.properties!.className).toEqual(["doc-sources"]);
    expect(line.children![0].children![0].value).toBe("Sources: the design notes");
  });

  it("puts tables in a scrolling box", async () => {
    expect(await render("guide/configuration.md")).toMatch(/<div class="overflow-x-auto[^"]*"><table/);
  });

  // No page carries an "Open", "Still open" or "Not built yet" section any more; the panel plugin stays for the rehype unit test.
  it("renders no outlined Open panel on any page", async () => {
    for (const file of ["design/chat.md", "design/models.md", "design/query-engine.md", "design/two-clocks.md"]) {
      expect(await render(file), file).not.toContain("<section");
    }
  });

  it("keeps a < in prose as text", async () => {
    // indexing.md has `<id>` in code; format "md" never parses markup, so it reaches the page as text.
    expect(await render("guide/indexing.md")).toContain("&lt;id&gt;");
  });
});
