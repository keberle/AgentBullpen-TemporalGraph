import type { CSSProperties, MouseEvent, ReactNode } from "react";
import { Button, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";

// Fluent Button restyled to the mockups. Shapes, by mockup:
//   lg  52px · radius 10 · 0 28px · 16px      Public / Challenge link-buttons
//   md  48px · radius 8  · 16px · weight 600   SignIn actions (full width)
//   sm  44px · 15px                           SignIn "Back", Challenge "View source"
//   xs  32px · radius 6  · 0 10px · 13px       SignIn review strip
//   action 36px · radius 8 · 0 14px · 14px · 600  Admin actions, Run
//   form   44px · radius 8 · 0 20px · 15px · 600  Account Save / Reset, chat Send
// Variants: primary (accent fill, black label), outline (white-20% border,
// white label), accent-outline (accent border + label), text (no border).
// Hover/pressed states are not in the mockups, so they equal rest. [gap]

export type KitButtonVariant = "primary" | "outline" | "accent-outline" | "text";
export type KitButtonSize = "lg" | "md" | "sm" | "xs" | "action" | "form";

const borderColors = (c: string) => ({ borderTopColor: c, borderRightColor: c, borderBottomColor: c, borderLeftColor: c });
const borderWidths = (w: string) => ({ borderTopWidth: w, borderRightWidth: w, borderBottomWidth: w, borderLeftWidth: w });

const same = (bg: string, fg: string, border: string) => ({
  backgroundColor: bg,
  color: fg,
  ...borderColors(border),
  ":hover": { backgroundColor: bg, color: fg, ...borderColors(border) },
  ":hover:active": { backgroundColor: bg, color: fg, ...borderColors(border) },
});

const useStyles = makeStyles({
  base: {
    minWidth: 0,
    fontFamily: tokens.fontFamilyBase,
    lineHeight: "normal",
    borderTopStyle: "solid",
    borderRightStyle: "solid",
    borderBottomStyle: "solid",
    borderLeftStyle: "solid",
    ...borderWidths("1px"),
    textDecorationLine: "none",
  },
  lg: { minHeight: "52px", padding: "0 28px", borderRadius: "10px", fontSize: "16px", fontWeight: 600 },
  md: { minHeight: "48px", borderRadius: "8px", fontSize: "16px", fontWeight: 600 },
  sm: { minHeight: "44px", fontSize: "15px", fontWeight: 400 },
  // The mockups draw link-buttons as <a> (content-box), so min-height excludes the border.
  anchor: { boxSizing: "content-box", paddingTop: 0, paddingBottom: 0 },
  xs: { minHeight: "32px", padding: "0 10px", borderRadius: "6px", fontSize: "13px", fontWeight: 400 },
  action: { minHeight: "36px", padding: "0 14px", borderRadius: "8px", fontSize: "14px", fontWeight: 600 },
  form: { minHeight: "44px", padding: "0 20px", borderRadius: "8px", fontSize: "15px", fontWeight: 600 },
  primary: { ...same(tokens.colorBrandBackground, tokens.colorNeutralForegroundOnBrand, "transparent"), ...borderWidths("0") },
  // Only the 52px link-buttons (Public, Challenge) are 700; every other primary is 600.
  lgPrimary: { fontWeight: 700 },
  outline: same("transparent", tokens.colorNeutralForeground1, tokens.colorNeutralStroke1),
  accentOutline: same("transparent", tokens.colorBrandForeground1, tokens.colorBrandStroke1),
  text: { ...same("transparent", tokens.colorBrandForeground1, "transparent"), ...borderWidths("0") },
});

type Props = {
  children: ReactNode;
  variant?: KitButtonVariant;
  size?: KitButtonSize;
  href?: string;
  onClick?: (e: MouseEvent<HTMLElement>) => void;
  className?: string;
  /** Per-instance mockup values (e.g. a different weight or text color). */
  style?: CSSProperties;
  type?: "button" | "submit";
  disabled?: boolean;
  "aria-label"?: string;
  "aria-pressed"?: boolean;
};

export function KitButton({ children, variant = "primary", size = "lg", href, onClick, className, style, type = "button", ...aria }: Props) {
  const s = useStyles();
  const v = { primary: s.primary, outline: s.outline, "accent-outline": s.accentOutline, text: s.text }[variant];
  const cls = mergeClasses(s.base, s[size], v, size === "lg" && variant === "primary" && s.lgPrimary, href !== undefined && s.anchor, className);
  if (href) {
    return (
      <Button as="a" href={href} onClick={onClick} className={cls} style={style} {...aria}>
        {children}
      </Button>
    );
  }
  return (
    <Button type={type} onClick={onClick} className={cls} style={style} {...aria}>
      {children}
    </Button>
  );
}
