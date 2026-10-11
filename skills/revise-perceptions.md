version: 2

# Skill: revise-perceptions

## Instructions

You read one chapter of a novel. You are given its events (each with an id and its participants) and the perceptions the characters hold as the chapter's events happen: for each, the character, the kind, the target (a fact with its id, or another character) and the chapter it dates from. Say which perceptions **change** in this chapter, and why.

A perception is one character's stance toward a fact or toward another character. Its `kind` is one of twelve, in four groups:
- credence: `believes`, `doubts`, `disbelieves`;
- trust (usually of a character): `trusts`, `distrusts`, `suspects`;
- threat: `threatened`, `reassured`;
- feeling: `fearful`, `hopeful`, `curious`, `indifferent` (`indifferent`: the character heard it and dismissed it).

A character holds at most one kind per group toward one target. A change is a new kind in a group: a belief that turns to doubt or disbelief when a lie is exposed, trust that turns to suspicion, a harmless fact that becomes a threat, a fear that is relieved. A first perception of one character about another that this chapter's events produce (someone comes to trust, distrust or suspect someone) is a change too.

For each change return:
- `character`: the entity id of the character whose perception changes.
- Exactly one of `fact` (the id, F…, of a listed fact) or `targetCharacter` (an entity id).
- `kind`: the new kind.
- `causeEvent`: the id (E…) of the event of this chapter that changes it.
- `evidence`: when the chapter text is given, a quote copied verbatim from it, one sentence or less, that shows the change (one continuous passage: never join parts with "…" or "..."). When only the events are given (no text), an empty string: the change then rests on its `causeEvent`.
- `why`: one line.

Rules:
- Only changes the chapter shows (its text, or its events when the text is not given). Don't return a perception that stays as listed, and don't return the ordinary case of someone hearing a thing and accepting it.
- Use only what you are given, not what you know of the book.
- Use no other kinds than the twelve. Return an empty list when nothing changes.

## Learned rules

## Examples

The quotes below are copied from chapters 5–7 of the book.

### Example 1

Events of this chapter:
- E96 · narrated · Sir Henry Baskerville (2), Barrymore (3), Dr. Watson (1) · "Sir Henry Baskerville rings for Barrymore and asks him about the sobbing heard in the night."
- E98 · narrated · Dr. Watson (1), Mrs. Barrymore (4) · "Dr. Watson meets Mrs. Barrymore in the long corridor and sees that she has been weeping."

Perceptions held:
- Dr. Watson (1) · believes · F141 "The sobbing in the night did not come from Mrs. Barrymore." · ch. 7
- Sir Henry Baskerville (2) · believes · F141 "The sobbing in the night did not come from Mrs. Barrymore." · ch. 7

> And yet he lied as he said it, for it chanced that after breakfast I met Mrs. Barrymore in the long corridor with the sun full upon her face.
> Yet he had taken the obvious risk of discovery in declaring that it was not so.

```json
{
  "changes": [
    {
      "character": 1,
      "fact": "F141",
      "targetCharacter": null,
      "kind": "disbelieves",
      "causeEvent": "E98",
      "evidence": "And yet he lied as he said it,",
      "why": "Watson sees that Mrs. Barrymore has wept, so he no longer believes Barrymore's answer."
    },
    {
      "character": 1,
      "fact": null,
      "targetCharacter": 3,
      "kind": "suspects",
      "causeEvent": "E98",
      "evidence": "Yet he had taken the obvious risk of discovery in declaring that it was not so.",
      "why": "Barrymore's lie makes Watson suspect him."
    }
  ]
}
```

Sir Henry was not there when Watson met Mrs. Barrymore, so his belief stays as listed and is not returned.
