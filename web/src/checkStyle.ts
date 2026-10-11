import { tokens, type GriffelStyle } from "@fluentui/react-components";

/**
 * The one checkbox recipe for the timeline bar (Spoilers) and the graph toolbar (Show facts & events, Details card).
 * Fluent's Checkbox root is an inline-flex row with the default `align-items` (stretch): the 16px indicator keeps its top margin
 * while the label grows to the root's height, so with the timeline bar's 44px minimum height the box sat 6px above the text's
 * centre. Centring the row puts box and label on one centre line at any height or font size.
 */
export function checkStyle({ fontSize, gap, minHeight }: { fontSize: string; gap: string; minHeight?: string }): GriffelStyle {
  return {
    alignItems: "center",
    ...(minHeight ? { minHeight } : {}),
    "& .fui-Checkbox__indicator": { alignSelf: "center" },
    "& label": { fontSize, lineHeight: "20px", paddingLeft: gap, color: tokens.colorNeutralForeground1, alignSelf: "center" },
  };
}
