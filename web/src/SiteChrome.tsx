import { Menu, MenuItemLink, MenuList, MenuPopover, MenuTrigger, makeStyles, tokens } from "@fluentui/react-components";
import { KitButton } from "./design-kit/components/KitButton.tsx";
import { UserMenu, type UserMenuItem } from "./design-kit/components/UserMenu.tsx";
import { Wordmark } from "./design-kit/components/Wordmark.tsx";
import { signOut, useSite } from "./site.ts";

// The top bar's site parts, behind the site only; standalone (or without VITE_SITE_URL) they render nothing but the plain wordmark.
// As the kit's authenticated shell (Authenticated.dc.html): the wordmark links to the site, and the signed-in member's user
// menu is at the top right. The site's other live demos, the same for every caller (an admin sees what the public sees).

// The "Other demos" menu as a card. Fluent's default popover surface is the page's black in this theme, so the menu had no
// visible edge; it takes the kit's panel surface and border, as the user menu does (design-kit UserMenu), and its items sit on it.
const useStyles = makeStyles({
  popover: {
    boxSizing: "border-box",
    padding: "8px",
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: "12px",
    boxShadow: "none",
  },
  list: { gap: "2px" },
  item: {
    borderRadius: "8px",
    color: tokens.colorNeutralForeground1,
    backgroundColor: "transparent",
    ":hover": { backgroundColor: tokens.colorSubtleBackgroundHover, color: tokens.colorNeutralForeground1 },
  },
});

/** The wordmark; behind the site it is the link to the site, as in the mockup. */
export function SiteWordmark() {
  const site = useSite();
  if (!site.enabled || !site.siteUrl) return <Wordmark />;
  return (
    <a href={site.siteUrl} className="no-underline">
      <Wordmark />
    </a>
  );
}

/** The user menu's items, all on the site, the same for every member: no Admin item. */
export function userMenuItems(siteUrl: string, onSignOut: () => void = () => void signOut()): UserMenuItem[] {
  return [
    { label: "Usage", href: `${siteUrl}/usage` },
    { label: "Account settings", href: `${siteUrl}/account` },
    { label: "Sign out", onClick: onSignOut },
  ];
}

/** The signed-in member's menu, top right: the kit's UserMenu with the member from the site's /api/me. */
export function SiteUserMenu() {
  const site = useSite();
  if (!site.enabled || !site.siteUrl || !site.user) return null;
  return <UserMenu name={site.user.name} email={site.user.email} initials={site.user.initials} items={userMenuItems(site.siteUrl)} />;
}

export function SiteLinks() {
  const s = useStyles();
  const site = useSite();
  // Behind the gateway only, and only with the site's address (VITE_SITE_URL) to link to.
  if (!site.enabled || !site.siteUrl) return null;
  const demos = site.otherDemos.filter((d) => d.url);
  if (demos.length === 0) return null;
  return (
    <nav aria-label="AgentBullpen.AI" className="flex items-center gap-12">
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <KitButton variant="outline" size="xs">
            Other demos
          </KitButton>
        </MenuTrigger>
        <MenuPopover className={s.popover}>
          <MenuList className={s.list}>
            {demos.map((d) => (
              <MenuItemLink key={d.id} href={d.url!} className={s.item}>
                {d.name}
              </MenuItemLink>
            ))}
          </MenuList>
        </MenuPopover>
      </Menu>
    </nav>
  );
}
