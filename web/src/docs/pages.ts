import type { MDXContent } from "mdx/types";

// The markdown in docs/, compiled by @mdx-js/rollup at build time (vite.config.ts): one lazy chunk per page, keyed by its path under
// docs/ ("story/two-clocks.md"). Nothing is copied; editing a .md and rebuilding publishes it.
type Loader = () => Promise<{ default: MDXContent }>;

const modules = {
  ...import.meta.glob("../../../docs/{guide,design,story}/*.md"),
  ...import.meta.glob("../../../docs/README.md"),
} as Record<string, Loader>;

export const loaders: Readonly<Record<string, Loader>> = Object.fromEntries(
  Object.entries(modules).map(([k, load]) => [k.replace("../../../docs/", ""), load]),
);
