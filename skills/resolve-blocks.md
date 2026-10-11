version: 1

# Skill: resolve-blocks

## Instructions

You are given several **name groups** from a novel. A group is a set of mentions that a coreference tool put together: the names it uses (with counts), the chapters it appears in, and two passages from the book around its mentions. The groups in one question share a name word or are spelled alike, so some of them may be one person (or one place) under several forms of a name, and some are different people who only share a name.

Return:
- `same`: the sets of group ids that are **one** person or place. A set has two or more ids. A group that is nobody else's is in no set. Put three groups in one set only when all three are the same one; never chain them (A with B because of one passage, B with C because of another) unless A and C are the same too.
- `proof`: one entry for every set in `same`: its `ids`, the `canonicalName` (the fullest proper name used for that person, copied exactly from the names given), and `quote`: a sentence copied verbatim from the passages given that shows the forms are used for the one person. One continuous passage. A set without a quote found in the passages is not counted.
- `reveals`: pairs of groups (`a`, `b`) that the text presents as different people and then **states** are one person (a false name, a disguise, a hidden marriage), with the `quote` that states it. Never put such a pair in `same`: the two stay apart and the statement is recorded.

What is the same: a shorter or titled form of the other (surname only, with or without Mr., Dr. or Sir), a spelling variant, a phrase the narrator plainly uses for that one person throughout.

What is different: titles that mark different people (Mr. and Mrs., or Miss, with the same surname), members of one family, namesakes of different generations, a person and a place named after them, a family as a whole and one of its members.

Rules:
- Judge only from the names and the passages given. Use nothing you know of the book.
- When a group is a noun phrase ("the baronet", "his wife"): it is the same as a named group only when every passage of it is about that one person. A phrase the book uses for several people is in no set.
- If you are not sure, leave the groups apart.
- Use only the group ids listed in the question. Return empty lists when nothing is the same.

## Learned rules

- A building or institution is often named by its full name without the word for its kind (for example "St. Anne's" for "St. Anne's Infirmary"). Treat the short name as the same entity when its passages use it for that building or institution, even though the same words could also name the surrounding district. Judge by what the passages refer to.

## Examples

The names and passages below are made up for the example; they are from no book.

### Example 1

Group 4: Character; names used: "Mr. Hale" ×12, "Hale" ×30; chapters 2–9.
  - chapter 2: "Mr. Hale took off his hat. “You will forgive me,” said Hale, “for coming so late.”"
Group 7: Character; names used: "Mrs. Hale" ×8; chapters 3–9.
  - chapter 3: "Mrs. Hale had not expected her husband before supper."
Group 9: Character; names used: "Edward Hale" ×3; chapters 5–6.
  - chapter 5: "“Edward Hale, at your service,” said Mr. Hale, and bowed."

```json
{
  "same": [ [4, 9] ],
  "proof": [ { "ids": [4, 9], "canonicalName": "Edward Hale", "quote": "“Edward Hale, at your service,” said Mr. Hale, and bowed." } ],
  "reveals": []
}
```
