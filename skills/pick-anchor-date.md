version: 2

# Skill: pick-anchor-date

## Instructions

You are given the events and facts of the first chapter of a novel (each with the text's own time expression when it has one, and a quote from the text) and its first narrated event. You have these, not the whole text. Pick one plausible calendar date for that event. Every other date in the book's timeline will be worked out from this one, so it is an assumption, stated once: it has to be plausible, not provable.

- `date`: a full calendar day, `YYYY-MM-DD`. Always give one. Make it up from the text's own clues: a year or a date on a document or an inscription and how long ago the text says that was; the season and the weather; the time of day; the technology and customs shown.
- When the clues give only a year or a season, choose a day inside it. When they give nothing, choose a plausible day for the period the text shows.
- `clue`: one sentence naming the clue in the text the date rests on.
- Use only what you are given. Do not use what you know of the book, of its author, of published chronologies or of when it was written.

## Learned rules

## Examples

The quotes below are copied from chapters 5–7 of the book.

### Example 1

The first narrated event: "Stapleton returns from chasing a butterfly and greets his sister and Dr. Watson."

> “Yes, I was chasing a Cyclopides. He is very rare and seldom found in the late autumn.

```json
{
  "date": "1889-10-19",
  "clue": "The speaker says the butterfly is seldom found in the late autumn, so the scene is in late autumn; the text shown gives no year, so a plausible one for the period is chosen."
}
```

### Example 2

The first narrated event: "Stapleton warns Dr. Watson about the Grimpen Mire."

> Even in dry seasons it is a danger to cross it, but after these autumn rains it is an awful place.

```json
{
  "date": "1889-10-15",
  "clue": "The speaker mentions these autumn rains, so the scene is in a wet autumn; no year is given, so a plausible one for the period is chosen."
}
```
