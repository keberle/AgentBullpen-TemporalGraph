// Who is selected where: the graph node, the character dropdown and the chat. A node click selects in the graph and
// the dropdown and never touches the chat; only the avatar row, the popover's "Chat" button and the panel start or end a chat.

export type Selection = { node: number | null; character: string | null; chatWith: string | null };

export type SelectionAction =
  /** A node clicked in the graph (null: the background). `characterName`: the node's canonical name if it is a Character in the dropdown. */
  | { type: "node"; id: number | null; characterName?: string | null }
  /** An avatar in the roster row: opens that character's chat and selects its node. */
  | { type: "avatar"; name: string; nodeId: number | null }
  /** The popover's "Chat" button on a roster character's node. */
  | { type: "chat"; name: string }
  /** The character dropdown. */
  | { type: "dropdown"; name: string | null; nodeId: number | null }
  /** The chat panel's close button. */
  | { type: "closeChat" }
  /** The roster changed (chapter, spoilers): a chat whose character is no longer offered closes. */
  | { type: "roster"; names: string[] }
  /** The book's character list arrived: the dropdown starts on the first. */
  | { type: "characters"; first: string | null };

export const noSelection: Selection = { node: null, character: null, chatWith: null };

/**
 * The actions that switch the selection (a node click, the dropdown, a roster avatar) also clear the chat answer's highlight,
 * whose accent rings otherwise stayed on the previous answer's nodes, so exactly one node is ringed afterwards: the selected one.
 */
export function clearsHighlight(a: SelectionAction): boolean {
  return a.type === "node" || a.type === "dropdown" || a.type === "avatar";
}

export function selectionReducer(s: Selection, a: SelectionAction): Selection {
  switch (a.type) {
    case "node":
      return { ...s, node: a.id, character: a.characterName ?? s.character };
    case "avatar":
      return { node: a.nodeId, character: a.name, chatWith: a.name };
    case "chat":
      return { ...s, character: a.name, chatWith: a.name };
    case "dropdown":
      return { ...s, character: a.name, node: a.nodeId };
    case "closeChat":
      return { ...s, chatWith: null };
    case "roster":
      return s.chatWith && !a.names.includes(s.chatWith) ? { ...s, chatWith: null } : s;
    case "characters":
      return { ...s, character: a.first };
  }
}
