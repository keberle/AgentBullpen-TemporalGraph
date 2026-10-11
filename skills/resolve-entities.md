version: 4

# Skill: resolve-entities

## Instructions

You are given two chains of mentions from a novel. A chain is a set of mentions that a coreference tool grouped: the names used (with counts), passages from the book around some of the mentions, and sometimes passages where both chains appear. Decide how the two chains are related.

`relation` is one of:
- `same`: one person (or one place) under two forms of a name. The narrator uses the two interchangeably from the first time they appear together: a shorter or titled form of the other (surname only, with or without Mr., Dr. or Sir), a spelling variant, or a phrase the narrator plainly uses for that one person throughout.
- `identity-reveal`: the text presents the two as different people, and later a character or the narrator **states** that they are one person (a false name, a disguise, a hidden marriage). The two are never merged; the statement is recorded. This needs a passage here that states the identity.
- `different`: two people or two places. Titles that mark different people (Mr. and Mrs., or Miss, with the same surname), members of one family, a person and a place named after them.

Rules:
- Judge only from the names and the passages given. Use nothing you know of the book.
- When one chain is a noun phrase ("the baronet", "his wife"): it is `same` only when every passage of it is about that one person. A phrase the book uses for several people is `different` from each of them.
- If you are not sure, answer `different`.
- `sameChains`: for `same`, the two chain ids; otherwise an empty list.
- `canonicalName`: for `same`, the fullest proper name used in the two chains, copied exactly; otherwise null.
- `quote`: for `identity-reveal`, the sentence that states the identity, copied verbatim from one of the passages given, one continuous passage; otherwise null.
- `reason`: one line.

## Learned rules

- A building or institution is often named by its full name without the word for its kind (for example "St. Anne's" for "St. Anne's Infirmary"). Treat the short name as the same entity when its evidence quotes use it for that building or institution, even though the same words could also name the surrounding district. Judge by what the quotes refer to.

## Examples

The quotes below are copied from chapter 4 or later of the book.

### Example 1

Chain A (id 17): names used: "Sir Henry Baskerville".
> “This is Sir Henry Baskerville,” said Dr. Mortimer.

Chain B (id 18): names used: "Sir Henry".
> “Pray take a seat, Sir Henry.

```json
{
  "relation": "same",
  "sameChains": [17, 18],
  "canonicalName": "Sir Henry Baskerville",
  "quote": null,
  "reason": "Sir Henry is the short titled form of Sir Henry Baskerville, and both passages address the same man."
}
```

### Example 2

Chain A (id 23): names used: "Barrymore".
> “Ha! Where is Barrymore?”

Chain B (id 70): names used: "Mrs. Barrymore".
> breakfast I met Mrs. Barrymore in the long corridor

```json
{
  "relation": "different",
  "sameChains": [],
  "canonicalName": null,
  "quote": null,
  "reason": "Mrs. marks a different person from Barrymore with the same surname."
}
```

### Example 3

Chain A (id 75): names used: "Miss Stapleton".
> I could not doubt that this was the Miss Stapleton of whom I had

Chain B (id 108): names used: "Mrs. Stapleton".
> minute we had torn off the gag, unswathed the bonds, and Mrs.
> Stapleton sank upon the floor in front of us. As her beautiful

Passages where both appear:
> have given me. The lady who has passed here as Miss Stapleton is
> in reality his wife.”

```json
{
  "relation": "identity-reveal",
  "sameChains": [],
  "canonicalName": null,
  "quote": "The lady who has passed here as Miss Stapleton is in reality his wife.”",
  "reason": "The book presents Miss Stapleton as a sister; a character then states that she is the wife."
}
```
