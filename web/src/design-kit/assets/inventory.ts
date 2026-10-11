// Running asset inventory. Update this list (not page
// markup) as assets are prompted, generated and optimised; /design/assets and
// every placeholder slot render from it.

export type AssetStatus = "not generated" | "to collect" | "generated" | "optimised" | "replaced";

export type Asset = {
  id: string;
  name: string;
  usedOn: string;
  pose: string;
  status: AssetStatus;
  /** Items the brief marks [open]. */
  open?: boolean;
  /** What replaced it, if anything. */
  note?: string;
};

export const inventory: readonly Asset[] = [
  { id: "A1", name: "Tile art — Design: Public", usedOn: "/design landing", pose: "Robot greeting visitors", status: "not generated" },
  { id: "A2", name: "Tile art — Design: Authenticated", usedOn: "/design landing", pose: "Robot at a workstation with the bullpen", status: "not generated" },
  { id: "A3", name: "Tile art — Design: Docs", usedOn: "/design landing", pose: "Robot with a book/graph", status: "not generated" },
  { id: "A4", name: "Tile art — Prototypes", usedOn: "/design landing", pose: "Open — not yet defined", status: "not generated", open: true },
  {
    id: "A5",
    name: "Hero “Challenge accepted!” comic",
    usedOn: "Public home hero",
    pose: "See the design system",
    status: "replaced",
    note: "Replaced by two cutouts (650×1100 and 776×1100), kept outside the public repo: they are people's likenesses and this app uses neither.",
  },
  { id: "A6", name: "Challenge-card hero images (one per challenge)", usedOn: "Public home + challenge pages", pose: "One per challenge; many can use real bullpen screenshots instead", status: "not generated" },
  { id: "A7", name: "Empty/loading/error/404 robot variants", usedOn: "Authenticated + public", pose: "Open — not yet defined", status: "not generated", open: true },
  { id: "A8", name: "Pending-approval state art", usedOn: "Registration flow", pose: "Open — not yet defined", status: "not generated", open: true },
  { id: "A9", name: "Existing bullpen app screenshots", usedOn: "Challenge pages", pose: "Real screenshots exist for most challenges", status: "to collect" },
  { id: "A10", name: "Logo / wordmark", usedOn: "Top bar, favicon", pose: "Open — existing app icon (robot in gear badge) is a candidate", status: "not generated", open: true },
];

export function asset(id: string): Asset {
  const a = inventory.find((x) => x.id === id);
  if (!a) throw new Error(`Unknown asset: ${id}`);
  return a;
}
