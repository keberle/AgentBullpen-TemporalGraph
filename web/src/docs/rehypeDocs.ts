// A rehype step for docs/*.md (vite.config.ts), so the renderer can give three parts of a page the mockup's shape without the
// markdown changing:
//   - a paragraph holding only an image becomes the image (the sheet frame is a <figure>, which can't sit inside a <p>);
//   - the italic sources line right under the H1 gets class "doc-sources" (rendered as the muted line, not body text), and its
//     leading "The story · 2 of 6 · " / "Guide · " / "Design & research · " is dropped: the eyebrow over the H1 already says it;
//     a line that says nothing else ("_The story · 2 of 6_", "_Design & research_") is left out whole, for the same reason;
//   - a section headed "Open", "Still open" or "Not built yet" is wrapped in <section class="doc-open"> (the outlined panel).

type Node = { type: string; tagName?: string; value?: string; properties?: Record<string, unknown>; children?: Node[] };

const isElement = (n: Node, tag?: string) => n.type === "element" && (tag == null || n.tagName === tag);
const isBlank = (n: Node) => n.type === "text" && !n.value?.trim();
const solid = (n: Node) => (n.children ?? []).filter((c) => !isBlank(c));
const text = (n: Node): string => (n.type === "text" ? (n.value ?? "") : (n.children ?? []).map(text).join(""));

const groupPrefix = /^(The story · \d+ of \d+|Guide|Design & research) · /;
const groupOnly = /^(The story · \d+ of \d+|Guide|Design & research)$/;

export const openHeadings = ["Open", "Still open", "Not built yet"];

export function rehypeDocs() {
  return (tree: Node) => {
    // Images alone in a paragraph, anywhere.
    const unwrap = (n: Node) => {
      if (!n.children) return;
      n.children = n.children.map((c) => {
        const only = solid(c);
        return isElement(c, "p") && only.length === 1 && isElement(only[0], "img") ? only[0] : c;
      });
      n.children.forEach(unwrap);
    };
    unwrap(tree);
    let top = tree.children ?? [];

    // The sources line: the first block after the H1, when it is a paragraph that is all italics.
    const blocks = top.filter((n) => n.type === "element");
    const h1 = blocks.findIndex((n) => isElement(n, "h1"));
    const next = h1 >= 0 ? blocks[h1 + 1] : undefined;
    if (next && isElement(next, "p") && solid(next).length === 1 && isElement(solid(next)[0], "em") && groupOnly.test(text(next).trim())) {
      top = top.filter((n) => n !== next);
    } else if (next && isElement(next, "p") && solid(next).length === 1 && isElement(solid(next)[0], "em")) {
      next.properties = { ...next.properties, className: ["doc-sources"] };
      const first = solid(next)[0].children?.[0];
      if (first?.type === "text" && first.value) {
        const rest = first.value.replace(groupPrefix, "");
        first.value = rest === first.value ? rest : rest.charAt(0).toUpperCase() + rest.slice(1);
      }
    }

    // Open sections: from the heading to the next H1/H2.
    const out: Node[] = [];
    let open: Node | null = null;
    for (const n of top) {
      const heading = isElement(n, "h1") || isElement(n, "h2");
      if (heading) open = null;
      if (isElement(n, "h2") && openHeadings.includes(text(n).trim())) {
        open = { type: "element", tagName: "section", properties: { className: ["doc-open"] }, children: [] };
        out.push(open);
      }
      (open ? open.children! : out).push(n);
    }
    tree.children = out;
  };
}
