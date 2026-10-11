// Spoilers on: which dated facts are true in the story by a given date, whenever the reader learns them?
// Parameters: none. Edit the date ('1889-12-31', an ISO date compared with storyFromHi as text) to ask about another day.
MATCH (f:Fact)-[r:ABOUT]->(c) WHERE r.storyFromHi IS NOT NULL AND r.storyFromHi <= '1889-12-31' RETURN DISTINCT f.text AS fact, r.storyFrom AS storyTime, r.storyFromHi AS trueBy, r.revealedPos AS revealed ORDER BY trueBy
