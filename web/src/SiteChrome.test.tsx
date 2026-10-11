import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";

// Admins see exactly what the public sees. The same page renders, byte for byte, for an admin and for a member;
// the only difference the site's answers make is whether a closed demo loads at all.
// The kit's authenticated shell behind the site (the wordmark links to the site, the member's user menu at the top right);
// the menu is the same for an admin and a member, and has no Admin item.

const site = "https://agentbullpen.test";
const api = "https://api.agentbullpen.test";
const apps = [
  { id: "temporal", name: "Temporal Graph", url: "https://temporal.agentbullpen.test", status: "live" },
  { id: "covers", name: "Covers", url: "https://covers.agentbullpen.test", status: "live" },
  { id: "next", name: "Next demo", url: "https://next.agentbullpen.test", status: "soon" },
  { id: "old", name: "Old demo", url: "https://old.agentbullpen.test", status: "paused" },
];
const books = [{ id: "hound-of-the-baskervilles", title: "The Hound of the Baskervilles", author: "Arthur Conan Doyle", hero: null }];

const member = { firstName: "Kim", lastName: "Evans", initials: "KE", email: "kim@example.test", roles: [] as string[] };

/**
 * The book list's page and the site state, after the load-time check with these answers (a fresh copy of the app's modules).
 * `me`: the site's /api/me answer; null = signed out (401). `gateway: false` = standalone.
 */
async function render(status: Record<string, unknown>, list: unknown[], me: Record<string, unknown> | null = member, gateway = true) {
  vi.resetModules();
  const siteModule = await import("./site.ts");
  const { SiteLinks, SiteUserMenu } = await import("./SiteChrome.tsx");
  const { default: BookList } = await import("./BookList.tsx");
  const redirects: string[] = [];
  const calls: string[] = [];
  const answers: Record<string, unknown> = { "/api/me": me, "/api/apps/temporal/status": status, "/api/apps": list };
  const state = await siteModule.startSite({
    gateway,
    siteUrl: site,
    apiBase: gateway ? api : "",
    fetch: async (url) => {
      calls.push(url);
      const answer = answers[url.slice(api.length)];
      return answer === null ? new Response(null, { status: 401 }) : new Response(JSON.stringify(answer), { status: 200 });
    },
    redirect: (u) => redirects.push(u),
    here: () => "http://localhost:5174/",
  });
  const page = renderToStaticMarkup(<BookList books={books as never} onPick={() => {}} />);
  const links = renderToStaticMarkup(<SiteLinks />);
  const menu = renderToStaticMarkup(<SiteUserMenu />);
  return { state, page, links, menu, redirects, calls };
}

const asAdmin = (xs: typeof apps) => xs.map((a) => ({ ...a, adminAccess: true }));

describe("An admin sees what the public sees", () => {
  afterEach(() => vi.resetModules());

  it("renders identically for an admin and a member", async () => {
    const member = await render({ status: "live" }, apps);
    const admin = await render({ status: "live", adminAccess: true }, asAdmin(apps));
    expect(admin.page).toBe(member.page);
    expect(admin.links).toBe(member.links);
    expect(admin.state).toEqual(member.state);
    // The page has the site's links and nothing admin-only. The site link is the wordmark, as in the kit's mockup.
    expect(member.links).toContain("Other demos");
    expect(member.page).toContain(`<a href="${site}"`);
    expect(member.page).not.toContain("← AgentBullpen.AI");
    expect(member.page).toContain("Other demos");
    expect(admin.menu).toBe(member.menu);
    for (const html of [member.page, admin.page]) {
      expect(html).not.toMatch(/Preview|for everyone else|role="status"|\((soon|paused|hidden)\)|Admin/);
    }
    // "Other demos" is the live list for both.
    expect(admin.state.otherDemos.map((a) => a.name)).toEqual(["Covers"]);
    expect(member.state.otherDemos.map((a) => a.name)).toEqual(["Covers"]);
    expect([member.redirects, admin.redirects]).toEqual([[], []]);
  });

  it("an admin let through on a paused demo gets the same page as a member on a live one", async () => {
    const member = await render({ status: "live" }, apps);
    const admin = await render({ status: "paused", adminAccess: true }, asAdmin(apps));
    expect(admin.redirects).toEqual([]);
    expect(admin.page).toBe(member.page);
    expect(admin.links).toBe(member.links);
    expect(admin.state.otherDemos).toEqual(member.state.otherDemos);
  });
});

describe("The user menu, behind the site only", () => {
  afterEach(() => vi.resetModules());

  it("signed in: the kit's user menu at the top right, with the member's name, email and initials", async () => {
    const r = await render({ status: "live" }, apps);
    expect(r.state.user).toEqual({ name: "Kim Evans", email: "kim@example.test", initials: "KE" });
    expect(r.menu).toContain('aria-label="User menu for Kim Evans"');
    expect(r.menu).toContain("KE");
    // In the page it is the header's last child: after the left group, as TopBar's `right`.
    expect(r.page).toMatch(/\/ Temporal Graph.*<button type="button" aria-label="User menu for Kim Evans"((?!<\/button>).)*KE<\/span><\/span><\/button><\/header>/);
  });

  it("the open menu: the member's name and email, then Usage, Account settings and Sign out, on the site; no Admin", async () => {
    vi.resetModules();
    const { UserMenu } = await import("./design-kit/components/UserMenu.tsx");
    const { userMenuItems } = await import("./SiteChrome.tsx");
    let signedOut = 0;
    const items = userMenuItems(site, () => signedOut++);
    expect(items.map((i) => [i.label, i.href])).toEqual([
      ["Usage", `${site}/usage`],
      ["Account settings", `${site}/account`],
      ["Sign out", undefined],
    ]);
    items[2].onClick!({} as never);
    expect(signedOut).toBe(1);
    // An admin's /api/me (roles: Admin) gives the same member fields, so the same menu.
    const admin = await render({ status: "live", adminAccess: true }, asAdmin(apps), { ...member, roles: ["Admin"] });
    expect(admin.state.user).toEqual({ name: "Kim Evans", email: "kim@example.test", initials: "KE" });
    expect(JSON.stringify(admin.state)).not.toMatch(/admin|roles/i);
    expect(renderToStaticMarkup(<UserMenu name="Kim Evans" email="kim@example.test" initials="KE" items={items} />)).toContain("User menu for Kim Evans");
  });

  it("signed out: the sign-in page, and the page has no user menu", async () => {
    const r = await render({ status: "live" }, apps, null);
    expect(r.redirects).toEqual([`${site}/signin?returnUrl=${encodeURIComponent("http://localhost:5174/")}`]);
    expect(r.calls).toEqual([`${api}/api/me`]);
    expect(r.state).toMatchObject({ signedOut: true, user: null });
    expect(r.menu).toBe("");
  });

  it("standalone: no call to the site, no user menu, no site links, and the wordmark is not a link", async () => {
    const r = await render({ status: "live" }, apps, member, false);
    expect(r.calls).toEqual([]);
    expect(r.redirects).toEqual([]);
    expect(r.state).toMatchObject({ enabled: false, user: null });
    expect(r.menu).toBe("");
    expect(r.links).toBe("");
    expect(r.page).not.toMatch(/User menu|<a href="https:\/\/agentbullpen\.test"|Other demos/);
    expect(r.page).toContain("AgentBullpen");
  });
});
