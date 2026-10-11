// Who do the facts so far talk about most?
// Parameters: $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry the node ids of the name columns, for the chat tools (which strip them).
MATCH (f:Fact), (c:Character) WHERE id(c) IN f.subject AND visible(f) AND visible(c)
RETURN c.name AS character, count(f) AS facts, id(c) AS characterId ORDER BY facts DESC LIMIT 10
