import { readdirSync, readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { resolveLink, sheets, sheetUrl } from "./links.ts";
import { allPages, eyebrow, groups, indexFile, neighbours } from "./toc.ts";

// The toc, docs/README.md and the disk agree; every link and sheet in every page resolves.
const docsDir = new URL("../../../docs/", import.meta.url);
const read = (file: string) => readFileSync(new URL(file, docsDir), "utf8");
const readme = read(indexFile);

// README.md's three tables, in order: [label](group/slug.md).
const readmeLinks = [...readme.matchAll(/^\| \[([^\]]+)\]\(([^)]+\.md)\)/gm)].map((m) => ({ label: m[1], file: m[2] }));

// Markdown links and images outside code spans and fenced blocks.
function links(md: string) {
  const prose = md.replace(/```[\s\S]*?```/g, "").replace(/`[^`\n]*`/g, "");
  return [...prose.matchAll(/(!?)\[[^\]]*\]\(([^)\s]+)\)/g)].map((m) => ({ image: m[1] === "!", href: m[2] }));
}

describe("docs table of contents", () => {
  it("lists README.md's pages in its order, with its link text", () => {
    expect(allPages.map((p) => ({ label: p.label, file: p.file }))).toEqual(readmeLinks);
  });

  it("names files that exist, and every page on disk is in it", () => {
    for (const g of groups) {
      const onDisk = readdirSync(new URL(`${g.key}/`, docsDir)).filter((f) => f.endsWith(".md"));
      expect(g.pages.map((p) => `${p.slug}.md`).sort()).toEqual(onDisk.sort());
    }
  });

  it("carries each page's H1 as its title", () => {
    for (const p of allPages) expect(read(p.file).split(/\r?\n/)[0], p.file).toBe(`# ${p.title}`);
  });

  it("numbers the story and pages within a group only", () => {
    const story = groups.find((g) => g.key === "story")!.pages;
    expect(eyebrow(story[1])).toBe("The story · 2 of 2");
    expect(eyebrow(allPages[0])).toBe("Guide");
    expect(neighbours(story[1]).prev?.label).toBe("The question");
    expect(neighbours(story[1]).next).toBeNull();
    expect(neighbours(allPages[0]).prev).toBeNull(); // first guide page: no link back across groups
    const lastDesign = groups.find((g) => g.key === "design")!.pages.at(-1)!;
    expect(neighbours(lastDesign).next).toBeNull();
  });
});

describe("links and sheets in the pages", () => {
  const files = [indexFile, ...allPages.map((p) => p.file)];

  it("every relative link reaches a docs page, a sheet or an anchor", () => {
    const missing = files.flatMap((f) =>
      links(read(f))
        .filter((l) => !l.image && resolveLink(l.href, f).kind === "missing")
        .map((l) => `${f} → ${l.href}`),
    );
    expect(missing).toEqual([]);
  });

  it("every image is a sheet the build knows", () => {
    const images = files.flatMap((f) => links(read(f)).filter((l) => l.image).map((l) => ({ f, href: l.href })));
    expect(images.length).toBeGreaterThan(0);
    for (const { f, href } of images) expect(sheetUrl(href, f), `${f} → ${href}`).toBeTruthy();
  });

  it("every sheet on disk resolves to a built URL", () => {
    const onDisk = readdirSync(new URL("diagrams/", docsDir)).filter((f) => f.endsWith(".svg"));
    expect(onDisk.length).toBe(13);
    for (const svg of onDisk) expect(sheetUrl(`../diagrams/${svg}`, "design/two-clocks.md"), svg).toBeTruthy();
    expect(Object.keys(sheets).sort()).toEqual(onDisk.map((s) => `diagrams/${s}`).sort());
  });
});
