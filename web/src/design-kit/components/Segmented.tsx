import { Button, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";

// Segmented button group — Usage.dc.html "Time range", Admin.dc.html "Filter":
// panel track, 4px padding and gap, radius 10; buttons 36px, 0 14px, radius 8,
// 600 14px. Selected = divider tint + white text; others transparent + secondary.
// Hover is not in the mockups, so it equals rest. [gap]

const useStyles = makeStyles({
  track: { display: "flex", gap: "4px", padding: "4px", borderRadius: "10px", backgroundColor: tokens.colorNeutralBackground2 },
  btn: {
    minWidth: 0,
    minHeight: "36px",
    padding: "0 14px",
    border: 0,
    borderRadius: "8px",
    fontFamily: tokens.fontFamilyBase,
    fontSize: "14px",
    fontWeight: 600,
    lineHeight: "normal",
    backgroundColor: "transparent",
    color: tokens.colorNeutralForeground2,
    ":hover": { backgroundColor: "transparent", color: tokens.colorNeutralForeground2 },
    ":hover:active": { backgroundColor: "transparent", color: tokens.colorNeutralForeground2 },
  },
  on: {
    backgroundColor: tokens.colorSubtleBackgroundSelected,
    color: tokens.colorNeutralForeground1,
    ":hover": { backgroundColor: tokens.colorSubtleBackgroundSelected, color: tokens.colorNeutralForeground1 },
    ":hover:active": { backgroundColor: tokens.colorSubtleBackgroundSelected, color: tokens.colorNeutralForeground1 },
  },
});

export function Segmented<K extends string | number>({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: readonly (readonly [K, string])[];
  value: K;
  onChange: (k: K) => void;
}) {
  const s = useStyles();
  return (
    <div role="group" aria-label={label} className={s.track}>
      {options.map(([k, text]) => (
        <Button key={String(k)} aria-pressed={k === value} onClick={() => onChange(k)} className={mergeClasses(s.btn, k === value && s.on)}>
          {text}
        </Button>
      ))}
    </div>
  );
}
