version: 1

# Skill: extract-mentions

## Instructions

You read one chunk of a novel and list the mentions of characters and places in it.

- Return every named or clearly identifying mention of a person, or of a group acting as a character, and of a place, in the given chunk.
- List a mention each time it occurs in the chunk.
- For each mention give:
  - `text`: the mention exactly as it is written in the chunk;
  - `type`: `Character` (a person, or a group acting as a character) or `Location` (a place);
  - `evidence`: a quote copied verbatim from the chunk, one sentence or less, that contains the mention.
- Never return anything that is not in the chunk. Use only the chunk, not what you know of the book.
- Do not extract pronouns ("he", "she", "I", "you", "we", "they") in this pass, even when you know who they refer to.
- If the chunk has no such mentions, return an empty list.

## Learned rules

## Examples

The quotes below are copied from chapter 4 or later of the book.

### Example 1

> “This is Sir Henry Baskerville,” said Dr. Mortimer.

```json
{
  "mentions": [
    {
      "text": "Sir Henry Baskerville",
      "type": "Character",
      "evidence": "“This is Sir Henry Baskerville,” said Dr. Mortimer."
    },
    {
      "text": "Dr. Mortimer",
      "type": "Character",
      "evidence": "“This is Sir Henry Baskerville,” said Dr. Mortimer."
    }
  ]
}
```

### Example 2

> Address to Mr. Barrymore, Baskerville Hall.

```json
{
  "mentions": [
    {
      "text": "Mr. Barrymore",
      "type": "Character",
      "evidence": "Address to Mr. Barrymore, Baskerville Hall."
    },
    {
      "text": "Baskerville Hall",
      "type": "Location",
      "evidence": "Address to Mr. Barrymore, Baskerville Hall."
    }
  ]
}
```

### Example 3

> “It is Selden, the Notting Hill murderer.”

```json
{
  "mentions": [
    {
      "text": "Selden",
      "type": "Character",
      "evidence": "“It is Selden, the Notting Hill murderer.”"
    },
    {
      "text": "Notting Hill",
      "type": "Location",
      "evidence": "“It is Selden, the Notting Hill murderer.”"
    }
  ]
}
```
