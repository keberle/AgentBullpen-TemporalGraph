import { Field, Input, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";

// Fluent Field + Input styled to SignIn.dc.html: label 14px secondary with a
// 6px gap; input 48px, 0 14px, radius 8, white-20% border on the panel color.
// `code` is the 6-digit variant (20px, 6px letter spacing). `invalid` is the
// wrong-code state: 2px solid white border. Focus is not in the mockups:
// Fluent's own focus underline (accent) is left in place. [gap]
// `surface="page"`: black input on a panel (Account.dc.html).

const borderColors = (c: string) => ({ borderTopColor: c, borderRightColor: c, borderBottomColor: c, borderLeftColor: c });

const useStyles = makeStyles({
  field: { display: "flex", flexDirection: "column", gap: "6px" },
  // Fluent adds a 2px margin under Field labels; the mockups have none.
  label: { fontSize: "14px", lineHeight: "normal", color: tokens.colorNeutralForeground2, padding: 0, marginBottom: 0 },
  input: {
    minHeight: "48px",
    boxSizing: "border-box",
    padding: "0 14px",
    borderRadius: "8px",
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground2,
    ":hover": borderColors(tokens.colorNeutralStroke1),
    ":focus-within": borderColors(tokens.colorNeutralStroke1),
    "& input": { padding: 0, fontFamily: tokens.fontFamilyBase, fontSize: "16px", fontWeight: 400, color: tokens.colorNeutralForeground1 },
  },
  page: { backgroundColor: tokens.colorNeutralBackground1 },
  code: { "& input": { fontSize: "20px", letterSpacing: "6px" } },
  invalid: {
    border: `2px solid ${tokens.colorNeutralForeground1}`,
    ":hover": borderColors(tokens.colorNeutralForeground1),
    ":focus-within": borderColors(tokens.colorNeutralForeground1),
    // Beat Fluent's own invalid border (red, keyed on aria-invalid): the mockup draws it white.
    ":not(:focus-within)": borderColors(tokens.colorNeutralForeground1),
    ":hover:not(:focus-within)": borderColors(tokens.colorNeutralForeground1),
  },
});

type Props = {
  label: string;
  type?: "text" | "email" | "number";
  code?: boolean;
  invalid?: boolean;
  surface?: "panel" | "page";
  value?: string;
  onChange?: (value: string) => void;
  validationMessage?: string;
  className?: string;
};

export function TextField({ label, type = "text", code = false, invalid = false, surface = "panel", value, onChange, validationMessage, className }: Props) {
  const s = useStyles();
  return (
    <Field label={{ children: label, className: s.label }} className={mergeClasses(s.field, className)} validationMessage={validationMessage}>
      <Input
        type={type}
        inputMode={code ? "numeric" : undefined}
        placeholder={code ? "000000" : undefined}
        aria-invalid={invalid || undefined}
        value={value}
        onChange={onChange && ((_, d) => onChange(d.value))}
        className={mergeClasses(s.input, surface === "page" && s.page, code && s.code, invalid && s.invalid)}
      />
    </Field>
  );
}
