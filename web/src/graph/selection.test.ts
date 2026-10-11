import { describe, expect, it } from "vitest";
import { highlightReducer, noHighlight, type Highlight } from "./model.ts";
import { clearsHighlight, noSelection, selectionReducer, type Selection, type SelectionAction } from "./selection.ts";

const talkingToHolmes: Selection = { node: 1, character: "Mr. Sherlock Holmes", chatWith: "Mr. Sherlock Holmes" };

describe("selectionReducer", () => {
  it("a node click selects the node and the dropdown but keeps the chat", () => {
    const s = selectionReducer(talkingToHolmes, { type: "node", id: 28, characterName: "Mr. Stapleton" });
    expect(s).toEqual({ node: 28, character: "Mr. Stapleton", chatWith: "Mr. Sherlock Holmes" });
    // A Location or fact: only the node; the background: nothing selected; the chat stays either way.
    expect(selectionReducer(talkingToHolmes, { type: "node", id: 12 })).toEqual({ ...talkingToHolmes, node: 12 });
    expect(selectionReducer(talkingToHolmes, { type: "node", id: null })).toEqual({ ...talkingToHolmes, node: null });
  });

  it("the popover's Chat button and the avatars start a chat", () => {
    const viaPopover = selectionReducer({ ...talkingToHolmes, node: 28 }, { type: "chat", name: "Mr. Stapleton" });
    expect(viaPopover).toEqual({ node: 28, character: "Mr. Stapleton", chatWith: "Mr. Stapleton" });
    expect(selectionReducer(noSelection, { type: "avatar", name: "Dr. Watson", nodeId: 3 })).toEqual({
      node: 3,
      character: "Dr. Watson",
      chatWith: "Dr. Watson",
    });
  });

  it("the dropdown selects the node, close and roster changes end the chat", () => {
    expect(selectionReducer(talkingToHolmes, { type: "dropdown", name: "Dr. Watson", nodeId: 3 })).toEqual({
      node: 3,
      character: "Dr. Watson",
      chatWith: "Mr. Sherlock Holmes",
    });
    expect(selectionReducer(talkingToHolmes, { type: "closeChat" }).chatWith).toBeNull();
    expect(selectionReducer(talkingToHolmes, { type: "roster", names: ["Dr. Watson"] }).chatWith).toBeNull();
    expect(selectionReducer(talkingToHolmes, { type: "roster", names: ["Mr. Sherlock Holmes"] })).toBe(talkingToHolmes);
    expect(selectionReducer(noSelection, { type: "characters", first: "Mr. Sherlock Holmes" }).character).toBe("Mr. Sherlock Holmes");
  });

  it("a character switch by dropdown, roster or click leaves exactly one ringed node", () => {
    // What BookView does: every action goes to the selection; switches also clear the highlight. Rings = selected + highlighted.
    type State = { sel: Selection; hi: Highlight };
    const apply = (s: State, a: SelectionAction): State => ({ sel: selectionReducer(s.sel, a), hi: clearsHighlight(a) ? noHighlight : s.hi });
    const ringed = (s: State) => new Set([...s.hi.nodes, ...(s.sel.node == null ? [] : [s.sel.node])]);
    const answered = (s: State): State => ({ ...s, hi: highlightReducer(s.hi, { type: "answer", toolCalls: [{ nodeIds: [1, 2, 3, 28, 45], edgeIds: [7] }] }) });

    let s = answered(apply({ sel: noSelection, hi: noHighlight }, { type: "avatar", name: "Mr. Sherlock Holmes", nodeId: 1 }));
    expect(ringed(s).size).toBe(5); // the answer's nodes, until the next switch
    s = apply(s, { type: "dropdown", name: "Dr. Watson", nodeId: 3 });
    expect([...ringed(s)]).toEqual([3]);
    s = apply(answered(s), { type: "avatar", name: "Mr. Barrymore", nodeId: 23 });
    expect([...ringed(s)]).toEqual([23]);
    s = apply(answered(s), { type: "node", id: 28, characterName: "Mr. Stapleton" });
    expect([...ringed(s)]).toEqual([28]);
    expect(s.sel.character).toBe("Mr. Stapleton");
    // The popover's "Chat" button, closing the chat and a roster change don't switch the selection: an answer's highlight stays.
    const keeps: SelectionAction[] = [{ type: "chat", name: "Mr. Stapleton" }, { type: "closeChat" }, { type: "roster", names: [] }];
    for (const a of keeps) expect(clearsHighlight(a)).toBe(false);
  });
});
