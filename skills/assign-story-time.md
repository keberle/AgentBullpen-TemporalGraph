version: 5

# Skill: assign-story-time

## Instructions

You date the items of one chapter of a novel in story time: when each happens or starts being true in the story's world, not when the reader is told. You have the items, not the text. You are given:
- the **anchor**: the one date the story's first narrated event is assumed to happen on. It is an assumption; every other date is worked out from it;
- the **events of the two chapters before** this one, each with its id, its summary and the text's own time expression. Their dates are worked out separately and are not shown: when an item of this chapter follows from one of them, date it relative to that event by its id;
- the **items** of this chapter: events and facts, each with an id, its text, the text's own time expression where there is one (`when`), and an evidence quote.

For every item return one entry:
- `id`: the item's id.
- Give its time in **one** of two ways:
  - **relative** (the usual case): `relativeTo` is the id of an event (of the two chapters before or of this chapter) and `offset` is how far from it, as an ISO 8601 duration: `P0D` the same day, `P1D` the next day, `-P1D` the day before, `-P3D` three days before, `-P3M` three months before, `-P2Y` two years before, `P1W` a week later. Code turns this into a date. Leave `storyFrom` null.
  - **absolute**: `storyFrom` is the date itself in EDTF, when the text states one (a year on a document, "the fourth of May"). Leave `relativeTo` and `offset` null.
- `storyUntil`: EDTF, only where the text gives an end ("from 1882 to 1884"); otherwise null.

Events:
- Always date an event. A narrated event that continues the scene of the event before it is `relativeTo` that event with `P0D`. "The next morning", "three days later", "that evening" are relative to the event they follow.
- A referenced event (one that is only mentioned) is dated by its own content, never by the event that mentions it: "yesterday" is the mentioning event with `-P1D`; "three months ago", `-P3M`; "some years ago" with no number, your plausible estimate (`-P5Y`).
- When the text gives no time at all, make the most plausible choice from the order of telling: the same day as the event before it.

Facts:
- Date a fact only when the text says when it became true ("he has been here two years": `-P2Y` from the event in which it is said; "died on the fourth of May": that date). Otherwise leave all four fields null: the fact takes the date of the event through which it is discovered.

EDTF: a year `1850`, a month `1850-03`, a day `1850-03-12`; `~` after a date means approximate (`1850~`); an interval `1848/1852`. Give a year always: a month or a day without a year is not a date here; count it from the anchor instead.

Rules:
- Use only what you are given. Do not use what you know of the book, of history, or of published chronologies.
- Never date an item relative to itself. Return every item once.

## Learned rules

## Examples

The quotes below are copied from chapters 5–7 of the book.

### Example 1

Timeline so far:
- E80 · ch. 6 · narrated · 1889-10-05 · "Dr. Watson, Sir Henry Baskerville and Dr. Mortimer drive from the station across the moor."

Items:
- E82 · Event (narrated) · "The driver Perkins tells Dr. Mortimer that a convict has escaped from Princetown."
- E83 · Event (referenced, mentioned in E82) · "A convict escapes from Princetown." · when: "three days now"
- F120 · Fact · "A convict from Princetown has been at large for three days." · discovered by E82 (told)
- F121 · Fact · "The warders watch every road and every station." · discovered by E82 (told)

> “There’s a convict escaped from Princetown, sir. He’s been out three days now, and the warders watch every road and every station, but they’ve had no sight of him yet.

```json
{
  "items": [
    { "id": "E82", "storyFrom": null, "storyUntil": null, "relativeTo": "E80", "offset": "P0D" },
    { "id": "E83", "storyFrom": null, "storyUntil": null, "relativeTo": "E82", "offset": "-P3D" },
    { "id": "F120", "storyFrom": null, "storyUntil": null, "relativeTo": "E82", "offset": "-P3D" },
    { "id": "F121", "storyFrom": null, "storyUntil": null, "relativeTo": null, "offset": null }
  ]
}
```

### Example 2

Timeline so far:
- E90 · ch. 6 · narrated · 1889-10-05 · "Dr. Watson and Sir Henry Baskerville dine at Baskerville Hall on the evening of their arrival."

Items:
- E95 · Event (narrated) · "Dr. Watson and Sir Henry Baskerville sit at breakfast in the sunlit hall." · when: "the following morning"
- E96 · Event (referenced, mentioned in E95) · "Dr. Watson hears a woman sobbing in the night." · when: "in the night"

> The fresh beauty of the following morning did something to efface from our minds the grim and grey impression which had been left upon both of us by our first experience of Baskerville Hall.

```json
{
  "items": [
    { "id": "E95", "storyFrom": null, "storyUntil": null, "relativeTo": "E90", "offset": "P1D" },
    { "id": "E96", "storyFrom": null, "storyUntil": null, "relativeTo": "E90", "offset": "P0D" }
  ]
}
```

The night between is counted with the evening before it, so the sobbing has the arrival's date.
