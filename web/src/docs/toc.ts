// The docs' table of contents: the three groups of docs/README.md, each page in its table's order. `label` is the
// page's link text in README.md (menu and pager), `title` its H1 (the browser tab), `file` its path under docs/. toc.test.ts
// checks this list against README.md and the disk, so adding a page is: write it, add it to README.md's table, add its line here.

export type GroupKey = "guide" | "design" | "story";

export type DocPage = { group: GroupKey; slug: string; label: string; title: string; file: string };

export type DocGroup = { key: GroupKey; name: string; eyebrow: string; pages: DocPage[] };

const page = (group: GroupKey, slug: string, label: string, title = label): DocPage => ({
  group,
  slug,
  label,
  title,
  file: `${group}/${slug}.md`,
});

export const groups: readonly DocGroup[] = [
  {
    key: "guide",
    name: "Guide",
    eyebrow: "GUIDE",
    pages: [
      page("guide", "getting-started", "Getting started"),
      page("guide", "configuration", "Configuration"),
      page("guide", "indexing", "Indexing a book"),
      page("guide", "schema", "Database schema"),
      page("guide", "project-layout", "Project layout"),
      page("guide", "tools", "Cross-check tools"),
    ],
  },
  {
    key: "design",
    name: "Design & research",
    eyebrow: "DESIGN & RESEARCH",
    pages: [
      page("design", "graph-model", "The graph model"),
      page("design", "two-clocks", "Two clocks"),
      page("design", "pipeline", "The indexing pipeline"),
      page("design", "grounding", "Entities and grounding"),
      page("design", "chat", "Character chat"),
      page("design", "query-engine", "Query engine"),
      page("design", "graph-view", "The graph view"),
      page("design", "models", "Models and gateway"),
    ],
  },
  {
    key: "story",
    name: "The story",
    eyebrow: "THE STORY",
    pages: [
      page("story", "the-question", "The question"),
      page("story", "whats-next", "What's next"),
    ],
  },
];

export const allPages: readonly DocPage[] = groups.flatMap((g) => g.pages);

/** The landing page, /docs. */
export const indexFile = "README.md";

export const pagePath = (p: DocPage) => `/docs/${p.group}/${p.slug}`;

export function findPage(group: string | undefined, slug: string | undefined): DocPage | null {
  return allPages.find((p) => p.group === group && p.slug === slug) ?? null;
}

export function pageByFile(file: string): DocPage | null {
  return allPages.find((p) => p.file === file) ?? null;
}

/** The eyebrow over the H1: "The story · 2 of 6" for the story (read in order), the group's name otherwise. */
export function eyebrow(p: DocPage): string {
  const g = groups.find((x) => x.key === p.group)!;
  return g.key === "story" ? `${g.name} · ${g.pages.indexOf(p) + 1} of ${g.pages.length}` : g.name;
}

/** Previous and next within the page's own group (no pager across groups). */
export function neighbours(p: DocPage): { prev: DocPage | null; next: DocPage | null } {
  const list = groups.find((x) => x.key === p.group)!.pages;
  const i = list.indexOf(p);
  return { prev: list[i - 1] ?? null, next: list[i + 1] ?? null };
}
