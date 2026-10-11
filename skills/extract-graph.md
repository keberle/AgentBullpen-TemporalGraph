version: 2

# Skill: extract-graph

## Instructions

You read one chunk of a novel (a chapter, or a part of one) once, and return everything it tells in one answer: the names of the people and places in it, the events, the facts and how each was discovered, the perceptions the text shows, retractions, identity reveals, and relations. You are told the chapter's narrator and, for a later part of a chapter, the events of the part before it.

**Write compactly, and leave nothing out.** Compact is about the form of each item, never about how many you return: return every event and every fact the chunk holds (see "Be thorough" below). Write each name once, in `mentions`, with a number; everywhere else point at it by that number. A summary or a fact is one sentence. A quote is its position and its opening words, never the whole passage. Return compact JSON: no indentation, no line breaks, nothing but the answer.

Every field is always present: where something does not apply, give "" for a text and 0 for a number (numbers start at 1).

**Names** (`mentions`): every distinct name or clearly identifying phrase of a person (or of a group acting as a character) and of a place that the chunk uses, **as the chunk writes it** ("Sir Henry", "Mrs. Barrymore", "the Hall").
- `id`: 1, 2, 3, … in the order you list them. `type`: `character` or `location`.
- List each form **once**, however often it occurs ("Holmes" and "Mr. Sherlock Holmes" are two forms, two entries). No pronouns.
- When the narrator takes part, list the narrator too, by the name stated above (the chunk may only say "I").
- Never invent a name and never bring one from outside the chunk: if the chunk calls her "Miss Stapleton", she is "Miss Stapleton" here, whatever else you know of the book. That holds inside `summary` and `text` as well.
- Wherever an item points at a person or a place it gives the **number** of one of these entries: the form the chunk uses nearest the passage, the fullest one there.

**Evidence** (`ev`) on every item except a name is one compact string, `at:len:q`:
- `at`: the character offset in the chunk text where the supporting quote starts (0 is the first character after the opening `<<<` line);
- `len`: how many characters the quote runs: one sentence or less;
- `q`: only the first words of the quote, at most 80 characters (the whole quote when it is shorter), copied exactly from the chunk, punctuation and quotation marks included. Never more than 80 characters: code reads the rest from the text by `at` and `len`.
For example `197:64:“There are only two women in the house, Sir Henry,” he answered.` Code looks the quote up by `q`, so `q` must be exact even when `at` is only approximate.

**Events** (`events`): something that happens.
- `id`: a number of your own for this answer: 1, 2, 3, …
- `kind`: `narrated` when the text tells it as it happens in this chunk (a conversation, an arrival, a walk). `referenced` when someone only mentions it and it happened at another time ("we met yesterday at lunch", "three months ago Sir Charles died"): it has no scene of its own here.
- `summary`: one sentence of at most 140 characters, with the names as the chunk writes them.
- `when`: the text's own time expression for the event, copied ("the next morning", "after breakfast", "three years ago"); "" when it gives none.
- `who`: the characters who take part, each as `n` (the number of the name) with a `role` in a word or two ("" when there is none). Use `speaker` for the one who says or writes what the others learn, `listener` for those addressed, and otherwise what they do ("narrator", "watched", "arrived"). A narrated event has at least one participant.
- `where`: the number of the name of the place where it happens, when the chunk names it; else 0.
- `in`: for a referenced event, the id of the narrated event of this answer in which it is mentioned; 0 for a narrated event. A referenced event always has one.
- A meeting or a conversation is one event with its participants, not a relation.

**Facts** (`facts`): one declarative sentence about the story's world: a description, a state, a relation, what happened. A claim is a fact too: record what is said, not whether it is true.
- `id`: 1, 2, 3, … (facts are numbered apart from events); `text`: one sentence.
- `about`: the numbers of the names of the people and places the fact is about (at least one).
- `by`: every event of this answer through which characters come to the fact, each with `how`:
  - `witnessed`: they are present and observe it directly;
  - `told`: another character says it to them;
  - `overheard`: they hear it without being addressed;
  - `inferred`: they work it out (a deduction);
  - `read`: a letter, a newspaper, a manuscript;
  - `recalled`: the character already knew it and the text only now says so (backstory the character lived, a thing the narrator mentions as known).
- One event yields many facts, each with its own `how`: what two people say to each other is `told`; what they watch together is `witnessed`.
- Every fact has at least one discovering event. If a fact has none in this chunk, add the narrated event in which it comes up.

**Perceptions** (`perceptions`): how a character takes a fact or another character, **only where the text shows it** and you can quote it. Don't return the ordinary case (someone hears a thing and simply accepts it); a later pass fills that in.
- `who`: the number of the name of the character who perceives. Exactly one of `fact` (a fact id of this answer; else 0) or `of` (the number of the name of the character perceived; else 0).
- `kind`, one of twelve: `believes`, `doubts`, `disbelieves` (credence); `trusts`, `distrusts`, `suspects` (trust, usually of a character); `threatened`, `reassured` (threat); `fearful`, `hopeful`, `curious`, `indifferent` (feeling). `indifferent` means the character heard it and dismissed it.
- `cause`: the id of the event of this answer that produces the perception; 0 when it is the fact's discovering event.

**Retractions** (`retractions`): the chunk shows that something taken as true is false (a lie exposed, a disguise uncovered, a mistaken deduction corrected).
- `fact`: the fact's id when it is a fact of this answer; else 0, with `text` (the fact as it was believed, one sentence) and `about` (numbers of names).
- `who`: 0 when the reader learns the fact itself was false; the number of the name of one character when only that character's belief ends.

**Identity reveals** (`identities`): the text states that two names are one person (a false name, a disguise). Give the numbers of both names (`a`, `b`). Never treat them as one yourself.

**Relations** (`relations`), only these two types:
- `RELATED_TO`: a standing relation between two characters (family, marriage, work, friendship). `kind` names it from the source's side ("A is B's brother": `from` A, `to` B, kind "brother"). `from` and `to` are numbers of names.
- `LOCATED_AT`: a character is at, lives at or goes to a place, or a place lies in another place. `to` is the place. `kind` is "".

Rules:
- Use only the chunk, not what you know of the book. The events of the part before are context: don't return them again.
- "I" outside quotation marks is the narrator; "I" inside a quotation is whoever speaks.
- Use no other kinds, modes or types than those listed. If the chunk has nothing for a list, return it empty.
- Be thorough. Go through the chunk from its first paragraph to its last and leave nothing out because the chunk is long: a narrated event for every exchange, arrival, action or change of subject (a long conversation is several events), a referenced event for every earlier happening someone mentions, and a fact for every distinct thing said, shown, read or described: who someone is, what they did, what they look like, what a document says, each step of a deduction, each claim. A chapter usually yields twenty or more events and fifty or more facts. Only never return the same fact twice in other words.

## Learned rules

## Examples

The quotes below are copied from chapters 5–7 of the book.

### Example 1

Narrator: Dr. Watson.

> “I heard it distinctly, and I am sure that it was really the sob of a woman.”
> “We must ask about this right away.” He rang the bell and asked Barrymore whether he could account for our experience.
> “There are only two women in the house, Sir Henry,” he answered. “One is the scullery-maid, who sleeps in the other wing. The other is my wife, and I can answer for it that the sound could not have come from her.”
> And yet he lied as he said it, for it chanced that after breakfast I met Mrs. Barrymore in the long corridor with the sun full upon her face.
> But her telltale eyes were red and glanced at me from between swollen lids. It was she, then, who wept in the night, and if she did so her husband must know it. Yet he had taken the obvious risk of discovery in declaring that it was not so.

```json
{"mentions":[{"id":1,"name":"Barrymore","type":"character"},{"id":2,"name":"Sir Henry","type":"character"},{"id":3,"name":"Mrs. Barrymore","type":"character"},{"id":4,"name":"Dr. Watson","type":"character"}],
"events":[{"id":1,"kind":"narrated","summary":"Sir Henry rings for Barrymore and asks him about the sobbing heard in the night.","when":"","who":[{"n":2,"role":"listener"},{"n":1,"role":"speaker"},{"n":4,"role":"narrator"}],"where":0,"in":0,"ev":"115:81:He rang the bell and asked Barrymore whether he could account for our experience"},
{"id":2,"kind":"narrated","summary":"Dr. Watson meets Mrs. Barrymore in the long corridor and sees that she has been weeping.","when":"after breakfast","who":[{"n":4,"role":"narrator"},{"n":3,"role":"watched"}],"where":0,"in":0,"ev":"411:141:And yet he lied as he said it, for it chanced that after breakfast I met Mrs. Ba"}],
"facts":[{"id":1,"text":"Barrymore says the sobbing in the night could not have come from his wife.","about":[1,3],"by":[{"event":1,"how":"told"}],"ev":"319:91:The other is my wife, and I can answer for it that the sound could not have come"},
{"id":2,"text":"Mrs. Barrymore's eyes are red and swollen from weeping.","about":[3],"by":[{"event":2,"how":"witnessed"}],"ev":"553:75:But her telltale eyes were red and glanced at me from between swollen lids."},
{"id":3,"text":"It was Mrs. Barrymore who wept in the night.","about":[3],"by":[{"event":2,"how":"inferred"}],"ev":"629:84:It was she, then, who wept in the night, and if she did so her husband must know"}],
"perceptions":[{"who":4,"fact":1,"of":0,"kind":"disbelieves","cause":2,"ev":"411:141:And yet he lied as he said it, for it chanced that after breakfast I met Mrs. Ba"}],
"retractions":[{"fact":1,"who":0,"text":"Barrymore says the sobbing in the night could not have come from his wife.","about":[1,3],"ev":"411:141:And yet he lied as he said it, for it chanced that after breakfast I met Mrs. Ba"}],
"identities":[],
"relations":[{"type":"RELATED_TO","from":3,"to":1,"kind":"wife","ev":"319:91:The other is my wife, and I can answer for it that the sound could not have come"}]}
```
