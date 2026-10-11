import { useState, type MouseEvent } from "react";
import { Avatar, mergeClasses, Menu, MenuItem, MenuItemLink, MenuList, MenuPopover, MenuTrigger, makeStyles, tokens } from "@fluentui/react-components";

// Signed-in user menu: Fluent Avatar + Menu, styled to TopBar.dc.html.
// Kept in its own module so a standalone demo can leave it out entirely.

/** With `href` the item is a link; without, a plain action (e.g. Sign out). */
export type UserMenuItem = { label: string; href?: string; onClick?: (e: MouseEvent) => void };

const useStyles = makeStyles({
  trigger: {
    width: "40px",
    height: "40px",
    padding: 0,
    border: 0,
    borderRadius: "50%",
    backgroundColor: "transparent",
    cursor: "pointer",
    outlineOffset: "2px",
    outline: "2px solid transparent",
  },
  open: { outline: `2px solid ${tokens.colorBrandForeground1}` },
  avatar: {
    width: "40px",
    height: "40px",
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground1,
    fontSize: "15px",
    fontWeight: 600,
  },
  initials: { backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1, fontSize: "15px", fontWeight: 600 },
  popover: {
    width: "260px",
    boxSizing: "border-box",
    padding: "8px",
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: "12px",
    boxShadow: "none", // [gap] the mockup panel has no shadow
  },
  header: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    padding: "10px 12px 12px",
    marginBottom: "6px",
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  name: { fontSize: "15px", fontWeight: 600, color: tokens.colorNeutralForeground1 },
  email: { fontSize: "13px", color: tokens.colorNeutralForeground2 },
  list: { gap: "2px" },
  item: {
    minHeight: "44px",
    padding: "0 12px",
    // Mockup: display flex, align-items center. Fluent's MenuItem top-aligns its content by default.
    alignItems: "center",
    "& .fui-MenuItem__content": { lineHeight: "normal" },
    borderRadius: "8px",
    fontSize: "15px",
    color: tokens.colorNeutralForeground1,
    backgroundColor: "transparent",
    ":hover": { backgroundColor: tokens.colorSubtleBackgroundHover, color: tokens.colorNeutralForeground1 },
  },
});

export function UserMenu({ name, email, initials, items }: { name: string; email: string; initials: string; items: readonly UserMenuItem[] }) {
  const s = useStyles();
  const [open, setOpen] = useState(false);
  return (
    <Menu open={open} onOpenChange={(_, d) => setOpen(d.open)} positioning={{ position: "below", align: "end", offset: 12 }}>
      <MenuTrigger disableButtonEnhancement>
        <button type="button" aria-label={`User menu for ${name}`} className={mergeClasses(s.trigger, open && s.open)}>
          <Avatar initials={{ children: initials, className: s.initials }} name={name} className={s.avatar} />
        </button>
      </MenuTrigger>
      <MenuPopover className={s.popover}>
        <div className={s.header}>
          <span className={s.name}>{name}</span>
          <span className={s.email}>{email}</span>
        </div>
        <MenuList className={s.list}>
          {items.map((i) =>
            i.href !== undefined ? (
              <MenuItemLink key={i.label} href={i.href} onClick={i.onClick} className={s.item}>
                {i.label}
              </MenuItemLink>
            ) : (
              <MenuItem key={i.label} onClick={i.onClick} className={s.item}>
                {i.label}
              </MenuItem>
            ),
          )}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}
