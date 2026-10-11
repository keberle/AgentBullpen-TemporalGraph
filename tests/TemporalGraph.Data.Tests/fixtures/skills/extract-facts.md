version: 3

# Skill: extract-facts

## Instructions

You read one chunk of a novel with the list of the book's entities (characters and places, each with an id), the chapter's narrator, and the events of the chunk before it. Return what the chunk tells, each item with a verbatim evidence quote: the events, the facts and how each was discovered, the perceptions the text shows, retractions, identity reveals, and relations.

**Events** (`events`): something that happens.
- `kind`: `narrated` when the text tells it as it happens in this chunk (a conversation, an arrival, a walk). `referenced` when someone only mentions it and it happened at another time ("we met yesterday at lunch", "three months ago Sir Charles died"): it has no scene of its own here.
- `id`: your own id for this answer: `e1`, `e2`, …
- `summary`: one sentence, naming entities as the list does.
- `when`: the text's own time expression for the event, copied ("the next morning", "after breakfast", "three years ago"); null when it gives none.
- `participants`: the characters who take part, each with a `role` in a word or two. Use `speaker` for the one who says or writes what the others learn, `listener` for those addressed, and otherwise what they do ("narrator", "watched", "arrived"). A narrated event has at least one participant.
- `location`: the id of the place where it happens, when the list has it; else null.
- `mentionedIn`: for a referenced event, the id of the narrated event of this answer in which it is mentioned; null for a narrated event. A referenced event always has one.
- A meeting or a conversation is one event with its participants, not a relation.

**Facts** (`facts`): one declarative sentence about the story's world: a description, a state, a relation, what happened. A claim is a fact too: record what is said, not whether it is true.
- `id`: `f1`, `f2`, …; `text`: one sentence, naming entities as the list does.
- `subjects`: the ids of the entities the fact is about (at least one).
- `discoveredBy`: every event of this answer through which characters come to the fact, each with `how`:
  - `witnessed`: they are present and observe it directly;
  - `told`: another character says it to them;
  - `overheard`: they hear it without being addressed;
  - `inferred`: they work it out (a deduction);
  - `read`: a letter, a newspaper, a manuscript;
  - `recalled`: the character already knew it and the text only now says so (backstory the character lived, a thing the narrator mentions as known).
- One event yields many facts, each with its own `how`: what two people say to each other is `told`; what they watch together is `witnessed`.
- Every fact has at least one discovering event. If a fact has none in this chunk, add the narrated event in which it comes up.

**Perceptions** (`perceptions`): how a character takes a fact or another character, **only where the text shows it** and you can quote it. Don't return the ordinary case (someone hears a thing and simply accepts it); a later pass fills that in.
- `character`: who perceives. Exactly one of `fact` (a fact id of this answer) or `targetCharacter` (an entity id).
- `kind`, one of twelve: `believes`, `doubts`, `disbelieves` (credence); `trusts`, `distrusts`, `suspects` (trust, usually of a character); `threatened`, `reassured` (threat); `fearful`, `hopeful`, `curious`, `indifferent` (feeling). `indifferent` means the character heard it and dismissed it.
- `causeEvent`: the id of the event of this answer that produces the perception; null when it is the fact's discovering event.

**Retractions** (`retractions`): the chunk shows that something taken as true is false (a lie exposed, a disguise uncovered, a mistaken deduction corrected).
- `fact`: the fact's id when it is a fact of this answer; else null, with `text` (the fact as it was believed, one sentence) and `subjects`.
- `character`: null when the reader learns the fact itself was false; the entity id of one character when only that character's belief ends.

**Identity reveals** (`identities`): the text states that two characters in the list are one person. Give both ids. Never treat them as one yourself.

**Relations** (`relations`), only these two types:
- `RELATED_TO`: a standing relation between two characters (family, marriage, work, friendship). `kind` names it from the source's side ("A is B's brother": source A, target B, kind "brother").
- `LOCATED_AT`: a character is at, lives at or goes to a place, or a place lies in another place. The target is the place. `kind` is null.

Rules:
- Every id of an entity comes from the list. Never invent an entity; leave out what involves someone or something not in the list.
- `evidence`: a quote copied verbatim from the chunk, one sentence or less, that supports the item. One continuous passage: never join parts with "…" or "...", and never start or end with them.
- "I" outside quotation marks is the narrator given above; "I" inside a quotation is whoever speaks.
- Use only the chunk, not what you know of the book. The previous chunk's events are context: don't return them again.
- Use no other kinds, modes or types than those listed. If the chunk has nothing for a list, return it empty.

## Learned rules

## Examples

The quotes below are copied from chapters 5–7 of the book.

### Example 1

Narrator: Dr. Watson (entity 1). Entities: 1 · Character · Dr. Watson; 2 · Character · Sir Henry Baskerville; 3 · Character · Barrymore; 4 · Character · Mrs. Barrymore

> “I heard it distinctly, and I am sure that it was really the sob of a woman.”
> “We must ask about this right away.” He rang the bell and asked Barrymore whether he could account for our experience.
> “There are only two women in the house, Sir Henry,” he answered. “One is the scullery-maid, who sleeps in the other wing. The other is my wife, and I can answer for it that the sound could not have come from her.”
> And yet he lied as he said it, for it chanced that after breakfast I met Mrs. Barrymore in the long corridor with the sun full upon her face.
> But her telltale eyes were red and glanced at me from between swollen lids. It was she, then, who wept in the night, and if she did so her husband must know it. Yet he had taken the obvious risk of discovery in declaring that it was not so.

```json
{
  "events": [
    {
      "id": "e1",
      "kind": "narrated",
      "summary": "Sir Henry Baskerville rings for Barrymore and asks him about the sobbing heard in the night.",
      "when": null,
      "participants": [
        { "character": 2, "role": "asks" },
        { "character": 3, "role": "speaker" },
        { "character": 1, "role": "listener" }
      ],
      "location": null,
      "evidence": "He rang the bell and asked Barrymore whether he could account for our experience.",
      "mentionedIn": null
    },
    {
      "id": "e2",
      "kind": "referenced",
      "summary": "Dr. Watson hears a woman sobbing in the night.",
      "when": "in the night",
      "participants": [
        { "character": 1, "role": "heard" }
      ],
      "location": null,
      "evidence": "“I heard it distinctly, and I am sure that it was really the sob of a woman.”",
      "mentionedIn": "e1"
    },
    {
      "id": "e3",
      "kind": "narrated",
      "summary": "Dr. Watson meets Mrs. Barrymore in the long corridor and sees that she has been weeping.",
      "when": "after breakfast",
      "participants": [
        { "character": 1, "role": "narrator" },
        { "character": 4, "role": "met" }
      ],
      "location": null,
      "evidence": "after breakfast I met Mrs. Barrymore in the long corridor with the sun full upon her face.",
      "mentionedIn": null
    }
  ],
  "facts": [
    {
      "id": "f1",
      "text": "Dr. Watson heard a woman sobbing in the night.",
      "subjects": [1],
      "evidence": "“I heard it distinctly, and I am sure that it was really the sob of a woman.”",
      "discoveredBy": [
        { "event": "e2", "how": "witnessed" }
      ]
    },
    {
      "id": "f2",
      "text": "The sobbing in the night did not come from Mrs. Barrymore.",
      "subjects": [4],
      "evidence": "The other is my wife, and I can answer for it that the sound could not have come from her.”",
      "discoveredBy": [
        { "event": "e1", "how": "told" }
      ]
    },
    {
      "id": "f3",
      "text": "Mrs. Barrymore's eyes are red and her lids swollen.",
      "subjects": [4],
      "evidence": "But her telltale eyes were red and glanced at me from between swollen lids.",
      "discoveredBy": [
        { "event": "e3", "how": "witnessed" }
      ]
    },
    {
      "id": "f4",
      "text": "It was Mrs. Barrymore who wept in the night.",
      "subjects": [4],
      "evidence": "It was she, then, who wept in the night, and if she did so her husband must know it.",
      "discoveredBy": [
        { "event": "e3", "how": "inferred" }
      ]
    }
  ],
  "perceptions": [
    {
      "character": 1,
      "fact": "f2",
      "targetCharacter": null,
      "kind": "disbelieves",
      "causeEvent": "e3",
      "evidence": "And yet he lied as he said it,"
    },
    {
      "character": 1,
      "fact": null,
      "targetCharacter": 3,
      "kind": "suspects",
      "causeEvent": "e3",
      "evidence": "Yet he had taken the obvious risk of discovery in declaring that it was not so."
    }
  ],
  "retractions": [
    {
      "fact": "f2",
      "character": null,
      "text": "The sobbing in the night did not come from Mrs. Barrymore.",
      "subjects": [4],
      "evidence": "And yet he lied as he said it,"
    }
  ],
  "identities": [],
  "relations": [
    {
      "type": "RELATED_TO",
      "source": 3,
      "target": 4,
      "kind": "husband",
      "evidence": "The other is my wife, and I can answer for it that the sound could not have come from her.”"
    }
  ]
}
```

### Example 2

Narrator: Dr. Watson (entity 3). Entities: 1 · Character · Stapleton; 2 · Character · Miss Stapleton; 3 · Character · Dr. Watson

> I could not doubt that this was the Miss Stapleton of whom I had been told, since ladies of any sort must be few upon the moor,
> There could not have been a greater contrast between brother and sister, for Stapleton was neutral tinted, with light hair and grey eyes, while she was darker than any brunette whom I have seen in England—slim, elegant, and tall.

```json
{
  "events": [
    {
      "id": "e1",
      "kind": "narrated",
      "summary": "Dr. Watson sees Miss Stapleton approach him on the moor path.",
      "when": null,
      "participants": [
        { "character": 3, "role": "narrator" },
        { "character": 2, "role": "approached" }
      ],
      "location": null,
      "evidence": "I could not doubt that this was the Miss Stapleton of whom I had been told,",
      "mentionedIn": null
    }
  ],
  "facts": [
    {
      "id": "f1",
      "text": "Miss Stapleton is Stapleton's sister.",
      "subjects": [1, 2],
      "evidence": "There could not have been a greater contrast between brother and sister,",
      "discoveredBy": [
        { "event": "e1", "how": "recalled" }
      ]
    },
    {
      "id": "f2",
      "text": "Stapleton has light hair and grey eyes; Miss Stapleton is dark, slim, elegant and tall.",
      "subjects": [1, 2],
      "evidence": "for Stapleton was neutral tinted, with light hair and grey eyes, while she was darker than any brunette whom I have seen in England—slim, elegant, and tall.",
      "discoveredBy": [
        { "event": "e1", "how": "witnessed" }
      ]
    }
  ],
  "perceptions": [],
  "retractions": [],
  "identities": [],
  "relations": [
    {
      "type": "RELATED_TO",
      "source": 1,
      "target": 2,
      "kind": "brother",
      "evidence": "There could not have been a greater contrast between brother and sister,"
    }
  ]
}
```
