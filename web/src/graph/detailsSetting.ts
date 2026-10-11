// The "Details card" setting, remembered per browser. Storage can be unavailable (private mode, blocked site data):
// then it just isn't remembered. A browser with nothing stored starts with the card off.

export const DETAILS_KEY = "tg.graph.detailsCard";

type Store = Pick<Storage, "getItem" | "setItem">;
const browserStore = (): Store => localStorage;

export function readDetails(store: () => Store = browserStore): boolean {
  try {
    return store().getItem(DETAILS_KEY) === "1";
  } catch {
    return false;
  }
}

export function writeDetails(on: boolean, store: () => Store = browserStore) {
  try {
    store().setItem(DETAILS_KEY, on ? "1" : "0");
  } catch {
    /* not remembered */
  }
}
