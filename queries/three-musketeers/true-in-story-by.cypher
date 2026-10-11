// Which facts are true in the story by a given date, whenever the reader learns them? Every fact has a story date (derived from the book's one assumed anchor date).
// Parameters: none. Edit the date ('1625-04-06', the day before the story opens; an ISO date compared with storyFromHi as text) to ask about another day.
MATCH (f:Fact) WHERE f.storyFromHi <= '1625-04-06' RETURN f.text AS fact, f.storyFrom AS storyTime, f.storyFromHi AS trueBy, f.revealedPos AS revealed ORDER BY trueBy
