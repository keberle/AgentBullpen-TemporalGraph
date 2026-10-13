import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import mdx from "@mdx-js/rollup";
import remarkGfm from "remark-gfm";
import rehypeSlug from "rehype-slug";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { rehypeDocs } from "./src/docs/rehypeDocs.ts";

// Aspire injects PORT when launched via the AppHost; 5173 when run directly.
const port = process.env.PORT ? Number(process.env.PORT) : 5173;

// The browser only talks to this dev server; /graphql is proxied to the api.
// Aspire sets API_HTTP to the api's http endpoint (see AppHost.cs). Without it
// (plain `npm run dev`) there is no backend and the book list says the temporal graph didn't start.
const apiTarget = process.env.API_HTTP;

// The repo's docs (../docs/*.md) compiled at build time as plain markdown, not MDX: `format: "md"`, so a `<` or `{` in
// prose is text and can never break a page. Only files under docs/ go through it; the web app's own README.md files are untouched.
const repoRoot = fileURLToPath(new URL("..", import.meta.url));
const webRoot = fileURLToPath(new URL(".", import.meta.url));
const docs = mdx({
  format: "md",
  mdExtensions: [".md"],
  mdxExtensions: [],
  include: path.join(repoRoot, "docs", "**", "*.md").replaceAll("\\", "/"),
  remarkPlugins: [remarkGfm],
  rehypePlugins: [rehypeSlug, rehypeDocs],
  providerImportSource: "@mdx-js/react",
});

export default defineConfig({
  plugins: [{ enforce: "pre", ...docs }, react(), tailwindcss()],
  base: "/",
  // The compiled pages import react/jsx-runtime and @mdx-js/react from ../docs/, which has no node_modules: resolve them from web/.
  resolve: { dedupe: ["react", "react-dom", "@mdx-js/react"] },
  // client-side routes (/book/<id>/3d?ch=7, …): the dev and preview servers answer any page URL that isn't a file with
  // index.html (history fallback), so a reload or a pasted link works. Vite's default, stated here because the app relies on it.
  // Asset URLs are absolute (base "/") so they resolve from nested paths. The API does the same when it hosts web/dist (Web:Root).
  appType: "spa",
  server: {
    port,
    strictPort: true,
    // The dev server reads web/ and, outside it, only the repo's docs/ (the pages and their diagrams): nothing else in the repo.
    fs: { allow: [webRoot, path.join(repoRoot, "docs")] },
    proxy: apiTarget ? { "/graphql": { target: apiTarget, changeOrigin: false } } : undefined,
  },
  preview: { port },
});
