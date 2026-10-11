// Number formatting used by the mockups.

/** Usage.dc.html's format: 1.2M, 37k, 940. */
export function fmtTokens(n: number): string {
  return n >= 1_000_000 ? (n / 1_000_000).toFixed(1) + "M" : n >= 1000 ? Math.round(n / 1000) + "k" : String(n);
}

/** Full digits with separators, as the mockups write them ("37,800"). */
export function fmtFull(n: number): string {
  return n.toLocaleString("en-US");
}
